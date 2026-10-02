using Pos.Core.Configuration;
using Pos.Core.Domain;

namespace Pos.App;

/// <summary>
/// Who may do what at the till: the cashiers and their PINs, which actions wait for the owner's PIN,
/// and the record of each.
/// </summary>
/// <remarks>
/// Reads the lane's settings as they stand at each question rather than a copy taken at startup, so
/// a cashier added or an approval switched on from the owner's screen applies to the next thing the
/// till does, without restarting it.
/// </remarks>
public sealed class TillSecurity
{
    private readonly PosSettings _settings;
    private readonly ITillEventStore? _events;
    private readonly Func<string?, PinCredential?, bool> _verify;

    /// <param name="verify">How a PIN is checked against a stored one. The real, deliberately slow check unless a test says otherwise.</param>
    public TillSecurity(PosSettings settings, ITillEventStore? events = null, Func<string?, PinCredential?, bool>? verify = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _events = events;
        _verify = verify ?? DashboardLock.Verify;
    }

    /// <summary>A till with no cashiers, nothing waiting for the owner, and nothing recorded.</summary>
    public static TillSecurity None { get; } = new(new PosSettings());

    /// <summary>Whether the people on this till sign on with a PIN of their own.</summary>
    public bool UsesCashierPins => _settings.Cashiers.Count > 0;

    public IReadOnlyList<string> CashierNames => _settings.Cashiers.Select(c => c.Name.Trim()).ToList();

    /// <summary>Whether this is that cashier's PIN.</summary>
    public bool IsCashiersPin(string name, string? pin) =>
        _settings.Cashiers.FirstOrDefault(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) is { } cashier
        && _verify(pin, cashier.Pin);

    /// <summary>
    /// Whether this action waits for the owner's PIN. Never when the owner has no PIN set: an
    /// approval nobody can give would stop the till.
    /// </summary>
    public bool Needs(Guarded action, decimal discountPercent = 0m) =>
        _settings.Security.DashboardIsLocked && _settings.Approvals.Guards(action, discountPercent);

    /// <summary>Whether this is the owner's PIN.</summary>
    public bool IsOwnersPin(string? pin) => _verify(pin, _settings.Security.DashboardPin);

    /// <summary>Whether anybody can approve anything on this lane: only once the owner's PIN is set.</summary>
    public bool CanApprove => _settings.Security.DashboardIsLocked;

    /// <summary>
    /// Writes down something that happened. Never throws: a sale, a void or a close that has
    /// happened has happened, and failing to note it is not a reason to report it as failed.
    /// </summary>
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
        try
        {
            _events?.Record(laneId, at, kind, cashier, reference, amount, approved, detail);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or System.IO.IOException)
        {
        }
    }
}
