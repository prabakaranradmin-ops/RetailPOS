namespace Pos.Core.Domain;

/// <summary>
/// Which kind of bill this lane issues.
/// </summary>
/// <remarks>
/// <para>
/// This is a legal distinction, not a display preference. A composition dealer is registered under
/// GST and has a GSTIN, but may not collect tax from the customer — so the document they issue is a
/// <b>bill of supply</b>, not a tax invoice, and it has to carry a declaration saying exactly that
/// (CGST Rules, rule 5(1)(f)). Printing a tax invoice that happens to show no tax is the one
/// outcome that would cause the shop trouble, which is why the mode changes the document rather
/// than hiding a column on it.
/// </para>
/// <para>
/// The mode is recorded on each invoice as it is written. A shop that crosses the turnover
/// threshold switches to <see cref="Gst"/>, and every bill it issued before that must still reprint
/// as the bill of supply it was.
/// </para>
/// </remarks>
public enum TaxMode
{
    /// <summary>A tax invoice: GST is extracted from the price and shown on the bill.</summary>
    Gst = 0,

    /// <summary>
    /// A bill of supply: no tax is charged, none is shown, and the price the customer pays is the
    /// price on the shelf.
    /// </summary>
    Composition = 1,
}

/// <summary>Text the law puts on a bill of supply.</summary>
public static class CompositionDeclaration
{
    /// <summary>
    /// Prescribed wording, printed on every bill of supply a composition dealer issues.
    /// </summary>
    /// <remarks>
    /// Not configurable and not translated. It is a phrase from the rules rather than a message
    /// from the shop, and a shopkeeper should not have to know it exists, let alone type it.
    /// </remarks>
    public const string Text =
        "Composition taxable person, not eligible to collect tax on supplies";
}
