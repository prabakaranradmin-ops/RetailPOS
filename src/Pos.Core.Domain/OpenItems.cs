using System.Globalization;
using Pos.Core.Domain.Import;

namespace Pos.Core.Domain;

/// <summary>
/// An item the shop has on its shelf but not in its catalogue: sold as a line of its own, typed in
/// at the till, and left for the owner to add properly.
/// </summary>
/// <remarks>
/// <para>
/// A missing item used to hold up the queue: the cashier could not sell it, and the customer waited
/// while somebody found the owner. Now the cashier types what it is and what it costs, picks the GST
/// slab, and the bill goes on.
/// </para>
/// <para>
/// The line carries no catalogue item: <see cref="ItemId"/> is a number no item has, so nothing is
/// taken off any shelf count and no offer applies. Everything else - the tax, the bill, the return,
/// the day's figures - treats it as any other line, because the tax on it is just as real.
/// </para>
/// </remarks>
public static class OpenItem
{
    /// <summary>The item id an open line carries. No catalogue item has it: their ids start at 1.</summary>
    public const long ItemId = 0;

    /// <summary>The longest name the till takes for one: as long as a catalogue name prints in full.</summary>
    public const int MaxNameLength = 60;

    /// <summary>
    /// Above this, an item is worth adding to the catalogue before it is sold rather than after.
    /// </summary>
    public const decimal MaxPrice = 1_00_000m;

    /// <summary>
    /// The slabs the till offers when nothing in the catalogue suggests one: those in force since
    /// 22 September 2025. The 12% and 28% slabs went then, for nearly everything a grocery sells; an
    /// item of the shop's own still at one of them is offered it by name.
    /// </summary>
    public static IReadOnlyList<decimal> Slabs { get; } = [0m, 5m, 18m, 40m];

    /// <summary>True for a line typed in at the till rather than taken from the catalogue.</summary>
    public static bool IsOpen(this InvoiceLine line) => line.ItemId == ItemId;

    /// <summary>What is wrong with the name typed for one, or null when it will do.</summary>
    public static string? NameProblem(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length < 2)
            return "Type what it is, so the bill says and the owner knows what to add.";

        if (trimmed.Length > MaxNameLength)
            return $"That name is {trimmed.Length} letters long. {MaxNameLength} is the most a bill prints.";

        return null;
    }

    /// <summary>What is wrong with the price typed for one, or null when it will do.</summary>
    public static string? PriceProblem(decimal price)
    {
        if (price <= 0m)
            return "The price has to be more than nothing.";

        if (decimal.Round(price, 2) != price)
            return "A price goes to the paisa: two places after the point at most.";

        if (price > MaxPrice)
            return $"More than {MaxPriceLabel} for something not in the catalogue. Add it to the catalogue first.";

        return null;
    }

    // Grouped in lakhs, as a shop reads it: a custom format string groups only in thousands.
    private static string MaxPriceLabel => "₹" + MaxPrice.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));

    /// <summary>
    /// The item an open line is made from: sold as typed, at the one price, with the tax inside it
    /// as on any MRP.
    /// </summary>
    /// <param name="hsn">The HSN code, or empty when nothing suggested one.</param>
    /// <param name="barcode">The code a scan read that matched nothing, kept for the owner.</param>
    /// <exception cref="ArgumentException">A name or price <see cref="NameProblem"/> or <see cref="PriceProblem"/> would refuse.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A GST rate that is not a slab.</exception>
    public static Item For(string name, decimal price, decimal gstRate, string? hsn = null, string? barcode = null)
    {
        if (NameProblem(name) is { } nameProblem)
            throw new ArgumentException(nameProblem, nameof(name));

        if (PriceProblem(price) is { } priceProblem)
            throw new ArgumentException(priceProblem, nameof(price));

        if (!ItemCsvParser.ValidGstRates.Contains(gstRate))
            throw new ArgumentOutOfRangeException(nameof(gstRate), gstRate, "Not a GST slab.");

        return new Item
        {
            Id = ItemId,
            Sku = string.Empty,
            Name = name.Trim(),
            HsnCode = hsn?.Trim() ?? string.Empty,
            Barcode = barcode is { Length: > 0 } ? barcode.Trim() : null,
            Mrp = price,
            SellPrice = price,
            GstRate = gstRate,
            IsTaxInclusive = true,
            UnitType = UnitType.Each,
        };
    }

    /// <summary>
    /// True for text that reads as a barcode - 8 to 14 digits - and so was a scan that matched
    /// nothing, rather than a name typed into the search.
    /// </summary>
    public static bool LooksLikeBarcode(string? text) =>
        text is { Length: >= 8 and <= 14 } && text.All(char.IsAsciiDigit);
}

/// <summary>One sale of an item not in the catalogue, as the owner is shown it.</summary>
/// <param name="LineId">The invoice line, which is what is marked as dealt with.</param>
/// <param name="Price">What one sold for, tax included.</param>
/// <param name="Hsn">The HSN code it was sold under, or empty for none.</param>
/// <param name="Cashier">Who was on the till, or null when nobody was named.</param>
public sealed record OpenItemSale(
    long LineId,
    string InvoiceNo,
    DateTimeOffset SoldAt,
    string Name,
    string? Barcode,
    decimal Price,
    decimal GstRate,
    string Hsn,
    decimal Quantity,
    string? Cashier);

/// <summary>
/// The sales of one thing not in the catalogue: what the owner adds once, however many times it
/// was sold before they got to it.
/// </summary>
/// <param name="Sales">Newest first.</param>
public sealed record OpenItemGroup(IReadOnlyList<OpenItemSale> Sales)
{
    /// <summary>The most recent sale, whose name, price and slab the owner starts from.</summary>
    public OpenItemSale Latest => Sales[0];

    public int Times => Sales.Count;

    public decimal Quantity => Sales.Sum(s => s.Quantity);

    public IReadOnlyList<long> LineIds => [.. Sales.Select(s => s.LineId)];

    /// <summary>Who sold it, each named once, most recent first.</summary>
    public IReadOnlyList<string> Cashiers =>
        [.. Sales.Select(s => s.Cashier).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Sales of the same thing: the same barcode where a scan read one, otherwise the same name
    /// however it was spaced or capitalised. Newest first, as are the sales in each.
    /// </summary>
    public static IReadOnlyList<OpenItemGroup> Of(IEnumerable<OpenItemSale> sales)
    {
        ArgumentNullException.ThrowIfNull(sales);

        return [.. sales
            .OrderByDescending(s => s.SoldAt)
            .ThenByDescending(s => s.LineId)
            .GroupBy(Key, StringComparer.Ordinal)
            .Select(g => new OpenItemGroup([.. g]))];
    }

    private static string Key(OpenItemSale sale) =>
        sale.Barcode is { Length: > 0 } barcode
            ? "barcode:" + barcode
            : "name:" + string.Join(' ', sale.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}

/// <summary>Where the sales of items not in the catalogue wait for the owner.</summary>
public interface IOpenItemStore
{
    /// <summary>
    /// Every sale of an item not in the catalogue, on a bill that stands, that the owner has not yet
    /// dealt with. Newest first.
    /// </summary>
    IReadOnlyList<OpenItemSale> Waiting(int limit = 500);

    /// <summary>
    /// Takes sales off the list: added to the catalogue as <paramref name="addedAs"/>, or set aside
    /// when null. A line already dealt with is left as it was.
    /// </summary>
    void DealtWith(IEnumerable<long> lineIds, DateTimeOffset at, string? addedAs);
}
