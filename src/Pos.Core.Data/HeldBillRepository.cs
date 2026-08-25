using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// Parked bills in local storage, so a bill held before a power cut is still there afterwards.
/// </summary>
public sealed class HeldBillRepository : IHeldBillStore
{
    private const string LineColumns =
        "item_id, name_snapshot, hsn_snapshot, barcode_snapshot, batch_no, unit_type, " +
        "mrp, unit_price, is_tax_inclusive, gst_rate, quantity, discount, is_inter_state";

    private readonly PosDatabase _database;

    public HeldBillRepository(PosDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public HeldBill Park(string laneId, string token, DateTimeOffset heldAt, Customer? customer, IReadOnlyList<InvoiceLine> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
            throw new InvalidOperationException("There is nothing to park — the bill has no lines.");

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        long heldBillId;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO held_bills (lane_id, token, held_at, customer_id)
                VALUES ($lane, $token, $heldAt, $customerId);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$lane", laneId);
            command.Parameters.AddWithValue("$token", token);
            command.Parameters.AddWithValue("$heldAt", heldAt);
            command.Parameters.AddWithValue("$customerId", (object?)customer?.Id ?? DBNull.Value);

            heldBillId = Convert.ToInt64(command.ExecuteScalar());
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO held_bill_lines
                  (held_bill_id, line_no, {LineColumns})
                VALUES
                  ($billId, $lineNo, $itemId, $name, $hsn, $barcode, $batch, $unitType,
                   $mrp, $unitPrice, $taxInclusive, $gstRate, $quantity, $discount, $interState);
                """;

            foreach (var name in new[]
                     {
                         "$billId", "$lineNo", "$itemId", "$name", "$hsn", "$barcode", "$batch",
                         "$unitType", "$mrp", "$unitPrice", "$taxInclusive", "$gstRate",
                         "$quantity", "$discount", "$interState",
                     })
            {
                command.Parameters.Add(new SqliteParameter(name, null));
            }

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                command.Parameters["$billId"].Value = heldBillId;
                command.Parameters["$lineNo"].Value = i + 1;
                command.Parameters["$itemId"].Value = line.ItemId;
                command.Parameters["$name"].Value = line.NameSnapshot;
                command.Parameters["$hsn"].Value = line.HsnSnapshot;
                command.Parameters["$barcode"].Value = (object?)line.BarcodeSnapshot ?? DBNull.Value;
                command.Parameters["$batch"].Value = (object?)line.BatchNo ?? DBNull.Value;
                command.Parameters["$unitType"].Value = (int)line.Unit;
                command.Parameters["$mrp"].Value = line.Mrp;
                command.Parameters["$unitPrice"].Value = line.UnitPrice;
                command.Parameters["$taxInclusive"].Value = line.IsTaxInclusive ? 1 : 0;
                command.Parameters["$gstRate"].Value = line.GstRate;
                command.Parameters["$quantity"].Value = line.Quantity;
                command.Parameters["$discount"].Value = line.Discount;
                command.Parameters["$interState"].Value = line.IsInterState ? 1 : 0;

                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();

        return new HeldBill(heldBillId, token, heldAt, customer, lines.Select(l => l.Clone()).ToList());
    }

    public IReadOnlyList<HeldBillSummary> List(string laneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.id, h.token, h.held_at, c.name, c.mobile_no
            FROM held_bills h
            LEFT JOIN customers c ON c.id = h.customer_id
            WHERE h.lane_id = $lane
            ORDER BY h.held_at DESC, h.id DESC;
            """;
        command.Parameters.AddWithValue("$lane", laneId);

        var rows = new List<(long Id, string Token, DateTimeOffset HeldAt, string Label)>();

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var label = reader.IsDBNull(3)
                    ? reader.IsDBNull(4) ? "Walk-in" : reader.GetString(4)
                    : reader.GetString(3);

                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetDateTimeOffset(2), label));
            }
        }

        // The summary shows an item count and a total, so the lines are totted up per bill. The
        // recall list is a handful of rows at a till, not a report.
        var summaries = new List<HeldBillSummary>(rows.Count);

        foreach (var row in rows)
        {
            var lines = ReadLines(connection, row.Id);
            summaries.Add(new HeldBillSummary(
                row.Id,
                row.Token,
                row.HeldAt,
                lines.Count,
                row.Label,
                InvoiceTotals.From(lines).GrandTotal));
        }

        return summaries;
    }

    public HeldBill? Recall(string laneId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        long heldBillId;
        DateTimeOffset heldAt;
        Customer? customer;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT h.id, h.held_at, c.id, c.mobile_no, c.name, c.loyalty_balance, c.state_code
                FROM held_bills h
                LEFT JOIN customers c ON c.id = h.customer_id
                WHERE h.lane_id = $lane AND h.token = $token;
                """;
            command.Parameters.AddWithValue("$lane", laneId);
            command.Parameters.AddWithValue("$token", token);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
                return null;

            heldBillId = reader.GetInt64(0);
            heldAt = reader.GetDateTimeOffset(1);
            customer = reader.IsDBNull(2)
                ? null
                : new Customer
                {
                    Id = reader.GetInt64(2),
                    MobileNo = reader.GetString(3),
                    Name = reader.IsDBNull(4) ? null : reader.GetString(4),
                    LoyaltyBalance = reader.GetInt32(5),
                    StateCode = reader.IsDBNull(6) ? null : reader.GetString(6),
                };
        }

        var lines = ReadLines(connection, heldBillId, transaction);

        // Reading and removing in one transaction is what stops the same parked bill being
        // recalled twice — onto two lanes, or twice on one after a mis-keyed recall.
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM held_bills WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", heldBillId);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();

        return new HeldBill(heldBillId, token, heldAt, customer, lines);
    }

    public bool Discard(string laneId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM held_bills WHERE lane_id = $lane AND token = $token;";
        command.Parameters.AddWithValue("$lane", laneId);
        command.Parameters.AddWithValue("$token", token);

        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Smallest unused token on this lane, formatted H001 upward. Reusing a freed token keeps them
    /// short enough for a cashier to read off a slip and call out across a counter.
    /// </summary>
    public string NextToken(string laneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT token FROM held_bills WHERE lane_id = $lane;";
        command.Parameters.AddWithValue("$lane", laneId);

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                taken.Add(reader.GetString(0));
        }

        for (var n = 1; n <= 999; n++)
        {
            var candidate = $"H{n:D3}";

            if (!taken.Contains(candidate))
                return candidate;
        }

        throw new InvalidOperationException("This lane already has 999 bills parked; recall or discard some before parking another.");
    }

    private static List<InvoiceLine> ReadLines(SqliteConnection connection, long heldBillId, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {LineColumns}
            FROM held_bill_lines
            WHERE held_bill_id = $billId
            ORDER BY line_no;
            """;
        command.Parameters.AddWithValue("$billId", heldBillId);

        var lines = new List<InvoiceLine>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
            lines.Add(InvoiceRepository.ReadLine(reader));

        return lines;
    }
}
