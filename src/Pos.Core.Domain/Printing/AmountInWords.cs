using System.Text;

namespace Pos.Core.Domain.Printing;

/// <summary>
/// An amount in words the Indian way - crore, lakh, thousand, hundred - as a full invoice states its
/// total: "Rupees Four Hundred and Fifty Paise Only".
/// </summary>
public static class AmountInWords
{
    private static readonly string[] Ones =
    [
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
        "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    ];

    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    /// <summary>Rupees and paise, rounded to the paisa, in words.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than nothing, or beyond 99,999 crore.</exception>
    public static string Rupees(decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "An amount in words is not negative.");

        var rounded = decimal.Round(amount, 2, MidpointRounding.ToEven);
        var rupees = (long)decimal.Truncate(rounded);
        var paise = (int)((rounded - rupees) * 100m);

        if (rupees > 999_999_999_999L)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Too large to write out.");

        var words = new StringBuilder("Rupees ");
        words.Append(rupees == 0 ? "Zero" : Whole(rupees));

        if (paise > 0)
            words.Append(" and ").Append(Whole(paise)).Append(" Paise");

        return words.Append(" Only").ToString();
    }

    private static string Whole(long number)
    {
        var parts = new List<string>();

        void Take(long divisor, string name)
        {
            if (number < divisor)
                return;

            parts.Add($"{Whole(number / divisor)} {name}");
            number %= divisor;
        }

        Take(10_000_000, "Crore");
        Take(100_000, "Lakh");
        Take(1_000, "Thousand");
        Take(100, "Hundred");

        if (number > 0)
            parts.Add(number < 20 ? Ones[number] : Tens[number / 10] + (number % 10 > 0 ? " " + Ones[number % 10] : string.Empty));

        return string.Join(" ", parts);
    }
}
