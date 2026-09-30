using System.Globalization;
using System.Text.RegularExpressions;

namespace Pos.Core.Domain;

/// <summary>One line of an order as the customer wrote it: what they asked for, and how much.</summary>
/// <param name="Asked">The line as written.</param>
/// <param name="What">The words left once the quantity is taken out: what to look the item up by.</param>
/// <param name="Quantity">How much, in the unit written; one when nothing was.</param>
/// <param name="Unit">The unit written beside the quantity, when there was one: kg, g, l, ml, pc.</param>
public sealed record OrderLine(string Asked, string What, decimal Quantity, string? Unit)
{
    /// <summary>
    /// The quantity in the item's own unit: 500 g is 0.5 of an item sold by the kilo, 250 ml 0.25
    /// of one sold by the litre. A count written against a packet is the count.
    /// </summary>
    public decimal QuantityFor(UnitType unit) => (Unit, unit) switch
    {
        ("g", UnitType.Kilogram) or ("ml", UnitType.Litre) => Quantity / 1000m,
        _ => Quantity,
    };
}

/// <summary>
/// Reads an order someone typed into a phone - a WhatsApp message, a note taken on a call - into
/// lines the till can look up.
/// </summary>
/// <remarks>
/// <para>
/// People write orders every way: <c>2 kg sugar</c>, <c>sugar 2kg</c>, <c>toor dal 1kg x 2</c>,
/// <c>2 x toor dal</c>, <c>ghee 1/2 kg</c>, a bullet or a number in front. Each line is one item.
/// A multiplier (<c>x 2</c>) is the count; failing that, a number with a unit beside it is the
/// quantity in that unit; failing that, a bare number at the start or the end.
/// </para>
/// <para>
/// A size that is part of the product's name - the 1kg in "toor dal 1kg x 2" - stays in the words
/// when a multiplier gives the count, because it is how the packet is found.
/// </para>
/// </remarks>
public static partial class OrderText
{
    public static IReadOnlyList<OrderLine> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var lines = new List<OrderLine>();

        // A line each, or a list typed on one line with commas or semicolons between the items.
        foreach (var raw in text.Split(['\n', ';', ',']))
        {
            var line = Bullet().Replace(raw.Trim(), string.Empty).Trim();

            if (line.Length == 0 || Greeting().IsMatch(line))
                continue;

            lines.Add(ParseLine(line));
        }

        return lines;
    }

    private static OrderLine ParseLine(string line)
    {
        // "x 2", "2 x", "×2": the count of what the rest of the line names.
        var multiplier = Multiplier().Match(line);

        if (multiplier.Success)
        {
            var count = multiplier.Groups["n1"].Success ? multiplier.Groups["n1"].Value : multiplier.Groups["n2"].Value;
            var rest = Tidy(line.Remove(multiplier.Index, multiplier.Length));

            return new OrderLine(line, rest, Number(count), null);
        }

        // "2 kg", "500g", "1/2 kg", "1.5 ltr".
        var measured = Measured().Match(line);

        if (measured.Success)
        {
            return new OrderLine(line, Tidy(line.Remove(measured.Index, measured.Length)), Number(measured.Groups["n"].Value), UnitOf(measured.Groups["u"].Value));
        }

        // A bare number at the start or the end: "3 soap", "soap 3".
        var leading = Leading().Match(line);

        if (leading.Success)
            return new OrderLine(line, Tidy(line[leading.Length..]), Number(leading.Groups["n"].Value), null);

        var trailing = Trailing().Match(line);

        if (trailing.Success)
            return new OrderLine(line, Tidy(line[..trailing.Index]), Number(trailing.Groups["n"].Value), null);

        return new OrderLine(line, Tidy(line), 1m, null);
    }

    /// <summary>
    /// Finds the item a line asks for and how many of it, using the till's own search.
    /// </summary>
    /// <remarks>
    /// A weight written against something sold by the packet is the packet's size, not how many:
    /// "bath soap 100g" is one soap of the 100g kind. That line is looked up again with its size in
    /// it, to find the right packet, and counted as one.
    /// </remarks>
    /// <returns>The item and quantity, or null for a line nothing in the catalogue matches.</returns>
    public static (Item Item, decimal Quantity)? Resolve(OrderLine line, Func<string, IReadOnlyList<Item>> search)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(search);

        if (line.What.Length == 0 || search(line.What).FirstOrDefault() is not { } found)
            return null;

        if (line.Unit is "kg" or "g" or "l" or "ml" && !found.UnitType.AllowsFractionalQuantity())
            return (search(line.Asked).FirstOrDefault() ?? found, 1m);

        var quantity = line.QuantityFor(found.UnitType);

        if (!found.UnitType.AllowsFractionalQuantity())
            quantity = Math.Max(1m, decimal.Round(quantity, 0, MidpointRounding.AwayFromZero));

        return (found, quantity);
    }

    private static decimal Number(string text)
    {
        if (text.Contains('/', StringComparison.Ordinal))
        {
            var parts = text.Split('/');

            if (decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var top)
                && decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var bottom) && bottom != 0m)
            {
                return decimal.Round(top / bottom, 3, MidpointRounding.ToEven);
            }
        }

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0m ? value : 1m;
    }

    private static string UnitOf(string written) => written.ToLowerInvariant() switch
    {
        "kg" or "kgs" or "kilo" or "kilos" => "kg",
        "g" or "gm" or "gms" or "gram" or "grams" => "g",
        "l" or "ltr" or "ltrs" or "litre" or "litres" or "liter" or "liters" => "l",
        "ml" => "ml",
        _ => "pc",
    };

    private static string Tidy(string words) => Spaces().Replace(words.Trim(' ', '-', ',', ':', '.', '*'), " ");

    [GeneratedRegex(@"^(\d+[\.\)]\s+|[-*•·]\s*)")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(hi|hello|hai|vanakkam|please|pls|thanks|thank you|order)\b[\s,!.]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Greeting();

    [GeneratedRegex(@"(?:(?<=^|\s)[x×\*]\s*(?<n1>\d+(?:\.\d+)?)\b|\b(?<n2>\d+)\s*[x×\*](?=\s))", RegexOptions.IgnoreCase)]
    private static partial Regex Multiplier();

    [GeneratedRegex(@"(?<n>\d+(?:[\./]\d+)?)\s*(?<u>kgs?|kilos?|gms?|grams?|g|ltrs?|litres?|liters?|l|ml|pcs?|nos|packets?|pkts?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Measured();

    [GeneratedRegex(@"^(?<n>\d+(?:[\./]\d+)?)\s+")]
    private static partial Regex Leading();

    [GeneratedRegex(@"\s+(?<n>\d+(?:[\./]\d+)?)$")]
    private static partial Regex Trailing();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
