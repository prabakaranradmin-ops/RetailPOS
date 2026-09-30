using System.Globalization;

namespace Pos.Core.Domain;

/// <summary>What the number in a scale label is.</summary>
public enum ScaleValue
{
    /// <summary>The weight, in thousandths of a kilo by default: 01250 is 1.250 kg.</summary>
    Weight = 0,

    /// <summary>The price, in paise by default: 04500 is 45.00.</summary>
    Price = 1,
}

/// <summary>
/// How the shop's weighing scale lays out the barcode on the label it prints.
/// </summary>
/// <remarks>
/// <para>
/// An EAN-13 starting 20 to 29 is one GS1 keeps for use inside a shop: no product ever carries one,
/// so a code in that range is always something the shop printed itself. The scale puts the item's
/// code after the prefix, then the weight or the price, then the check digit:
/// <c>20 00123 01250 7</c> is item 123, 1.250 kg.
/// </para>
/// <para>
/// Scales differ in how many digits they give each part, so the layout is the shop's setting. The
/// default is the common one: a two-digit prefix, five for the item, five for a weight in grams.
/// </para>
/// </remarks>
public sealed record ScaleBarcodeFormat(
    IReadOnlyList<string> Prefixes,
    int ItemDigits = 5,
    int ValueDigits = 5,
    ScaleValue Value = ScaleValue.Weight,
    int Decimals = 3)
{
    /// <summary>Every in-store prefix, a five-digit item code and a weight in grams.</summary>
    public static ScaleBarcodeFormat Default { get; } = new([.. Enumerable.Range(20, 10).Select(p => p.ToString(CultureInfo.InvariantCulture))]);

    /// <summary>A layout that turns nothing into a scale label.</summary>
    public static ScaleBarcodeFormat Off { get; } = new([]);

    public bool IsOn => Prefixes.Count > 0;

    /// <summary>Why this layout cannot be right, or null.</summary>
    public string? Problem()
    {
        if (Prefixes.Count == 0)
            return null;

        if (Prefixes.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)))
            return "A scale prefix is digits only.";

        if (Prefixes.Select(p => p.Length).Distinct().Count() != 1)
            return "Every scale prefix has to be the same length.";

        if (Decimals is < 0 or > 3)
            return "A scale value has 0 to 3 decimal places.";

        if (ItemDigits < 1 || ValueDigits < 1 || Prefixes[0].Length + ItemDigits + ValueDigits != 12)
            return $"A prefix of {Prefixes[0].Length}, {ItemDigits} digits of item and {ValueDigits} of value do not make an EAN-13 with its check digit.";

        return null;
    }

    /// <summary>
    /// Reads a code as a scale label, or null when it is not one: the wrong length, a prefix that is
    /// not the scale's, or a check digit that does not agree - a misread, which is re-scanned rather
    /// than guessed at.
    /// </summary>
    public ScaleLabel? Read(string? code)
    {
        if (!IsOn || code is null)
            return null;

        var digits = code.Trim();

        if (digits.Length != 13 || !digits.All(char.IsAsciiDigit))
            return null;

        var prefix = Prefixes.FirstOrDefault(p => digits.StartsWith(p, StringComparison.Ordinal));

        if (prefix is null || Hardware.Scanning.Barcode.CheckDigit(digits[..12]) != digits[12] - '0')
            return null;

        var item = digits.Substring(prefix.Length, ItemDigits);
        var raw = long.Parse(digits.Substring(prefix.Length + ItemDigits, ValueDigits), CultureInfo.InvariantCulture);
        var value = raw / (decimal)Math.Pow(10, Decimals);

        return new ScaleLabel(digits, item, Value, value);
    }
}

/// <summary>A scale label read: which item, and how much of it or what it came to.</summary>
public sealed record ScaleLabel(string Code, string ItemCode, ScaleValue Kind, decimal Value)
{
    /// <summary>The item code without the zeros the scale pads it with: 00123 is SKU 123.</summary>
    public string ShortItemCode => ItemCode.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0";

    /// <summary>
    /// The quantity and the discount that make a line come to exactly what the label says.
    /// </summary>
    /// <remarks>
    /// A weight is the quantity itself. A price is divided by the item's price and rounded up to a
    /// gram, and the fraction of a paisa-worth that rounding adds is taken off as a discount, so the
    /// customer pays the label to the paisa and the tax is worked on what they paid.
    /// </remarks>
    /// <returns>Null, with why, when the label cannot be for this item.</returns>
    public (decimal Quantity, decimal Discount)? ForItem(Item item, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(item);
        problem = null;

        if (Value <= 0m)
        {
            problem = "The scale label says nothing was weighed.";
            return null;
        }

        var fractional = item.UnitType.AllowsFractionalQuantity();

        if (Kind == ScaleValue.Weight)
        {
            if (!fractional)
            {
                problem = $"{item.Name} is sold by the {Units.ScreenLabel(item.UnitType)}, not by weight.";
                return null;
            }

            return (Value, 0m);
        }

        if (item.SellPrice <= 0m)
        {
            problem = $"{item.Name} has no price to work the quantity out from.";
            return null;
        }

        var exact = Value / item.SellPrice;

        if (!fractional)
        {
            if (decimal.Truncate(exact) != exact)
            {
                problem = $"{Value:0.00} is not a whole number of {item.Name} at {item.SellPrice:0.00}.";
                return null;
            }

            return (exact, 0m);
        }

        var quantity = decimal.Ceiling(exact * 1000m) / 1000m;
        var discount = decimal.Round(quantity * item.SellPrice - Value, 2, MidpointRounding.ToEven);

        return (quantity, Math.Max(0m, discount));
    }
}
