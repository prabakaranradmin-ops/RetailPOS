namespace Pos.Core.Domain;

/// <summary>
/// One payment against a bill. A bill may carry several (SRS 2.4, split tender).
/// </summary>
/// <param name="Type">How it was paid.</param>
/// <param name="Amount">Rupees handed over under this tender, always positive.</param>
/// <param name="ReferenceNo">
/// Card approval code, UPI transaction reference, or the point count for a loyalty tender.
/// Free text, shown on a reprint.
/// </param>
public readonly record struct Tender(TenderType Type, decimal Amount, string? ReferenceNo = null);
