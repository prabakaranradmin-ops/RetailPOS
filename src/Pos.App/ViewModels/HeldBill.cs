using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>
/// A parked bill, as shown in the recall list (SRS 2.5): token, when it was parked, how many
/// lines, and who it belongs to.
/// </summary>
/// <remarks>
/// Phase 2 keeps these in memory for the life of the session. Persisting them to local storage so
/// they survive a restart is Phase 4 work; the lines held here are already deep copies, so that
/// change is a storage concern only.
/// </remarks>
public sealed record HeldBill(
    string Token,
    DateTimeOffset HeldAt,
    IReadOnlyList<InvoiceLine> Lines,
    Customer? Customer)
{
    public int ItemCount => Lines.Count;

    public string CustomerLabel => Customer?.Name ?? Customer?.MobileNo ?? "Walk-in";
}
