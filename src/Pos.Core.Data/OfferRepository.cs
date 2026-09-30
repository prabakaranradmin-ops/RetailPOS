using System.Globalization;
using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// The shop's offers and schemes, and what they gave.
/// </summary>
/// <remarks>
/// Replaced as a whole when an offers sheet is loaded - the sheet is the list. What an offer gave is
/// read back from the bills, by the offer name each discounted line carries, so the figure is what
/// the customers actually got and not what the offer was meant to give.
/// </remarks>
public sealed class OfferRepository(PosDatabase database) : IOfferStore
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public IReadOnlyList<Offer> All()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();

        // The SKU looked up in the catalogue now, so an item loaded after the offer is found, and one
        // taken out of the catalogue leaves the offer giving nothing rather than failing the bill.
        command.CommandText = """
            SELECT o.id, o.name, o.kind, o.sku, o.category, o.buy_qty, o.get_qty, o.percent, o.amount,
                   o.price, o.min_bill, o.free_qty, o.starts_on, o.ends_on, o.days,
                   (SELECT i.id FROM items i WHERE i.sku = o.sku COLLATE NOCASE LIMIT 1)
            FROM offers o
            ORDER BY o.name COLLATE NOCASE;
            """;

        var offers = new List<Offer>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            offers.Add(new Offer
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                Kind = Enum.TryParse<OfferKind>(reader.GetString(2), out var kind) ? kind : OfferKind.Percent,
                Sku = reader.IsDBNull(3) ? null : reader.GetString(3),
                Category = reader.IsDBNull(4) ? null : reader.GetString(4),
                Buy = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Get = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Percent = Money(reader, 7),
                Amount = Money(reader, 8),
                Price = Money(reader, 9),
                MinBill = Money(reader, 10),
                FreeQuantity = Money(reader, 11),
                From = Day(reader, 12),
                To = Day(reader, 13),
                Days = reader.IsDBNull(14) ? [] : reader.GetString(14).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(d => (DayOfWeek)int.Parse(d, CultureInfo.InvariantCulture)).ToList(),
                ItemId = reader.IsDBNull(15) ? null : reader.GetInt64(15),
            });
        }

        return offers;
    }

    public void ReplaceAll(IReadOnlyList<Offer> offers, DateTimeOffset loadedAt)
    {
        ArgumentNullException.ThrowIfNull(offers);

        if (offers.FirstOrDefault(o => o.Problem() is not null) is { } wrong)
            throw new ArgumentException($"'{wrong.Name}' cannot run: {wrong.Problem()}", nameof(offers));

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM offers;";
            clear.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO offers (name, kind, sku, category, buy_qty, get_qty, percent, amount, price,
                                    min_bill, free_qty, starts_on, ends_on, days, loaded_at)
                VALUES ($name, $kind, $sku, $category, $buy, $get, $percent, $amount, $price,
                        $minBill, $freeQty, $from, $to, $days, $at);
                """;

            foreach (var offer in offers)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$name", offer.Name.Trim());
                insert.Parameters.AddWithValue("$kind", offer.Kind.ToString());
                insert.Parameters.AddWithValue("$sku", (object?)offer.Sku?.Trim() ?? DBNull.Value);
                insert.Parameters.AddWithValue("$category", (object?)offer.Category?.Trim() ?? DBNull.Value);
                insert.Parameters.AddWithValue("$buy", offer.Buy > 0 ? (object)offer.Buy : DBNull.Value);
                insert.Parameters.AddWithValue("$get", offer.Get > 0 ? (object)offer.Get : DBNull.Value);
                insert.Parameters.AddWithValue("$percent", Text(offer.Percent));
                insert.Parameters.AddWithValue("$amount", Text(offer.Amount));
                insert.Parameters.AddWithValue("$price", Text(offer.Price));
                insert.Parameters.AddWithValue("$minBill", Text(offer.MinBill));
                insert.Parameters.AddWithValue("$freeQty", Text(offer.FreeQuantity));
                insert.Parameters.AddWithValue("$from", (object?)offer.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
                insert.Parameters.AddWithValue("$to", (object?)offer.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
                insert.Parameters.AddWithValue("$days", offer.Days.Count == 0 ? DBNull.Value : (object)string.Join(' ', offer.Days.Select(d => ((int)d).ToString(CultureInfo.InvariantCulture))));
                insert.Parameters.AddWithValue("$at", loadedAt);
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    public IReadOnlyList<OfferUse> Given(DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT l.offer_name, COUNT(DISTINCT l.invoice_id), {PaiseSql.Sum("l.discount")}
            FROM invoice_lines l JOIN invoices i ON i.id = l.invoice_id
            WHERE l.offer_name IS NOT NULL AND i.voided_at IS NULL
              AND i.created_at >= $from AND i.created_at < $to
            GROUP BY l.offer_name
            ORDER BY {PaiseSql.Sum("l.discount")} DESC;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        var uses = new List<OfferUse>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
            uses.Add(new OfferUse(reader.GetString(0), reader.GetInt32(1), PaiseSql.Rupees(reader.GetInt64(2))));

        return uses;
    }

    private static object Text(decimal value) => value == 0m ? DBNull.Value : value.ToString(CultureInfo.InvariantCulture);

    private static decimal Money(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? 0m : decimal.Parse(reader.GetString(column), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static DateOnly? Day(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : DateOnly.ParseExact(reader.GetString(column), "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
