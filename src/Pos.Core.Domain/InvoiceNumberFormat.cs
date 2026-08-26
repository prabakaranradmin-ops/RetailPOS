namespace Pos.Core.Domain;

/// <summary>
/// How a lane composes an invoice number: <c>{prefix}/{FY}/{sequence}</c>, optionally with a lane
/// segment — <c>RM/26-27/11358</c> or <c>RM/26-27/L1-11358</c>.
/// </summary>
/// <remarks>
/// <para>
/// The sequence itself is minted per lane and per financial year inside the invoice's own
/// transaction, which is what makes it gapless and what lets several tills number independently
/// with nothing coordinating them.
/// </para>
/// <para>
/// <b>The lane segment is not decoration.</b> Two tills each mint 1, 2, 3… of their own, so with
/// <see cref="IncludeLaneSegment"/> off they issue the same invoice numbers as each other and
/// nothing in the system will notice — there is no server to catch the collision. Turn it off only
/// on a store that has exactly one till and will never have two.
/// </para>
/// </remarks>
public sealed record InvoiceNumberFormat
{
    /// <summary>The shop's own prefix, as in the <c>RM</c> of <c>RM/26-27/11358</c>.</summary>
    public string StorePrefix { get; init; } = "INV";

    /// <summary>
    /// Whether the lane id appears before the sequence. Leave this on unless the store has one till.
    /// See the remarks on this type for what turning it off costs.
    /// </summary>
    public bool IncludeLaneSegment { get; init; } = true;

    /// <summary>
    /// Zero-padding on the sequence. Zero prints it as-is, which is what a shop counter bill
    /// normally shows; set it to 6 for a fixed-width <c>000123</c>.
    /// </summary>
    public int SequencePadding { get; init; }

    public static InvoiceNumberFormat Default { get; } = new();

    public string Format(string laneId, FiscalYear fiscalYear, long sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        var number = SequencePadding > 0
            ? sequence.ToString($"D{SequencePadding}", System.Globalization.CultureInfo.InvariantCulture)
            : sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var tail = IncludeLaneSegment ? $"{laneId}-{number}" : number;

        return $"{StorePrefix}/{fiscalYear.ShortLabel}/{tail}";
    }

    /// <summary>Throws if the format would produce something no invoice number should be.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StorePrefix))
            throw new ArgumentException("The invoice number needs a store prefix.", nameof(StorePrefix));

        // '/' is the separator and a run of them would make the parts ambiguous; the rest are
        // characters that make a number awkward to file, quote over the phone, or put in a filename.
        if (StorePrefix.IndexOfAny(['/', '\\', ' ', '\t', '\n']) >= 0)
            throw new ArgumentException($"The invoice prefix '{StorePrefix}' contains a separator or whitespace.", nameof(StorePrefix));

        if (SequencePadding is < 0 or > 12)
            throw new ArgumentOutOfRangeException(nameof(SequencePadding), SequencePadding, "Sequence padding must be between 0 and 12.");
    }
}
