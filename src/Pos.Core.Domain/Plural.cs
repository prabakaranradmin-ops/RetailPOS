using System.Globalization;

namespace Pos.Core.Domain;

/// <summary>
/// A count with its noun in the right number: "1 bill", "3 bills", "1,250 bills".
/// </summary>
/// <remarks>
/// Instead of the "bill(s)" and "item(s)" that were in about a hundred sentences on the screen, the
/// web pages and the command line. A bracketed plural reads as a form, not a sentence, and "1 items"
/// was the other way the same shortcut went wrong. <c>PluralTests</c> fails the build on "(s)" in any
/// string a person reads.
/// </remarks>
public static class Plural
{
    private static readonly CultureInfo Indian = CultureInfo.GetCultureInfo("en-IN");

    /// <summary>"1 bill", "3 bills". An irregular plural is given: <c>Of(2, "copy", "copies")</c>.</summary>
    public static string Of(int count, string one, string? many = null) =>
        $"{count.ToString("N0", Indian)} {Noun(count, one, many)}";

    /// <summary>The noun alone, for a sentence that places the number itself.</summary>
    public static string Noun(int count, string one, string? many = null) =>
        count == 1 ? one : many ?? one + "s";
}
