using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>The till's exceptions: voids, discounts by hand, cash refunds, cash out, sign-ons and refused PINs.</summary>
public sealed class TillEventRepository(PosDatabase database) : ITillEventStore
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public void Record(
        string laneId,
        DateTimeOffset at,
        TillEventKind kind,
        string? cashier,
        string? reference = null,
        decimal? amount = null,
        bool? approved = null,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO till_events (lane_id, happened_at, kind, cashier_name, reference, amount, approved, detail)
            VALUES ($lane, $at, $kind, $cashier, $reference, $amount, $approved, $detail);
            """;
        command.Parameters.AddWithValue("$lane", laneId);
        command.Parameters.AddWithValue("$at", at);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$cashier", (object?)Blank(cashier) ?? DBNull.Value);
        command.Parameters.AddWithValue("$reference", (object?)Blank(reference) ?? DBNull.Value);
        command.Parameters.AddWithValue("$amount", (object?)amount ?? DBNull.Value);
        command.Parameters.AddWithValue("$approved", approved is { } given ? (given ? 1 : 0) : DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)Blank(detail) ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<TillEvent> List(DateTimeOffset from, DateTimeOffset to, int limit = 1000)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, lane_id, happened_at, kind, cashier_name, reference, amount, approved, detail
            FROM till_events
            WHERE happened_at >= $from AND happened_at < $to
            ORDER BY happened_at DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100_000));

        var events = new List<TillEvent>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            // A kind a later build wrote and this one does not know is skipped rather than guessed at.
            if (!Enum.TryParse<TillEventKind>(reader.GetString(3), out var kind))
                continue;

            events.Add(new TillEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetDateTimeOffset(2),
                kind,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7) == 1,
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return events;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
