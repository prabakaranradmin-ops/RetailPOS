using System.Text;
using Pos.Core.Configuration;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Catching a settings file that was saved in the wrong encoding.
/// </summary>
/// <remarks>
/// The corruption is silent by nature — the mangled bytes are valid UTF-8 and valid JSON, so
/// nothing downstream can tell. What makes it worth a check of its own is that the receipt's
/// labels are compiled in and stay correct, so the bill looks fine apart from the shop's own name.
/// The tests that matter most here are the ones proving it does <em>not</em> fire, because a check
/// that rewrote a real name would be worse than the fault it is looking for.
/// </remarks>
public class TextEncodingCheckTests
{
    /// <summary>Mangles text the way an editor does: UTF-8 bytes read as Windows-1252.</summary>
    private static string Mangle(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        var builder = new StringBuilder(utf8.Length);

        foreach (var b in utf8)
        {
            builder.Append(b switch
            {
                0x80 => '€', 0x82 => '‚', 0x83 => 'ƒ', 0x84 => '„',
                0x85 => '…', 0x86 => '†', 0x87 => '‡', 0x88 => 'ˆ',
                0x89 => '‰', 0x8A => 'Š', 0x8B => '‹', 0x8C => 'Œ',
                0x8E => 'Ž', 0x91 => '‘', 0x92 => '’', 0x93 => '“',
                0x94 => '”', 0x95 => '•', 0x96 => '–', 0x97 => '—',
                0x98 => '˜', 0x99 => '™', 0x9A => 'š', 0x9B => '›',
                0x9C => 'œ', 0x9E => 'ž', 0x9F => 'Ÿ',
                _ => (char)b,
            });
        }

        return builder.ToString();
    }

    // ---- What it catches -------------------------------------------------------------------

    [Theory]
    [InlineData("ரவி மளிகை")]
    [InlineData("நன்றி, மீண்டும் வருக")]
    [InlineData("பொருளின் பெயர்")]
    [InlineData("श्री लक्ष्मी स्टोर्स")]
    [InlineData("ಶ್ರೀ")]
    [InlineData("Café Münster")]
    public void TextMangledByTheWrongEncodingIsRecognisedAndPutBack(string original)
    {
        var mangled = Mangle(original);

        Assert.NotEqual(original, mangled);
        Assert.Equal(original, TextEncodingCheck.Repair(mangled));
    }

    /// <summary>The exact string a lane printed on every bill before this check existed.</summary>
    [Fact]
    public void TheShopNameFromTheFieldIsPutBack()
    {
        Assert.Equal("ரவி மளிகை", TextEncodingCheck.Repair("à®°à®µà®¿ à®®à®³à®¿à®•à¯ˆ"));
    }

    // ---- What it leaves alone --------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Sri Lakshmi Stores")]
    [InlineData("RM/26-27/11358")]
    [InlineData("No. 3/324, Main Road")]
    [InlineData("33AEIPH7795F1Z9")]
    public void OrdinaryTextIsLeftAlone(string? text)
    {
        Assert.Null(TextEncodingCheck.Repair(text));
    }

    /// <summary>
    /// Text that is already correct must never be "repaired". A shop whose name is genuinely in
    /// Tamil would otherwise be refused for having got it right.
    /// </summary>
    [Theory]
    [InlineData("ரவி மளிகை")]
    [InlineData("நன்றி, மீண்டும் வருக")]
    [InlineData("श्री लक्ष्मी")]
    public void TextThatIsAlreadyCorrectIsLeftAlone(string text)
    {
        Assert.Null(TextEncodingCheck.Repair(text));
    }

    /// <summary>
    /// Accented Latin names are the false-positive risk: they sit in the same byte range as the
    /// mojibake and are perfectly ordinary things for a shop to be called.
    /// </summary>
    [Theory]
    [InlineData("Café Coimbatore")]
    [InlineData("Señor Provisions")]
    [InlineData("Zoë & Co")]
    [InlineData("Müller Stores")]
    [InlineData("Ångström")]
    [InlineData("naïve")]
    public void AccentedLatinTextIsLeftAlone(string text)
    {
        Assert.Null(TextEncodingCheck.Repair(text));
    }

    [Theory]
    [InlineData("Rs. 1,184.00")]
    [InlineData("₹1,184.00")]
    [InlineData("50% off — today only")]
    [InlineData("\"Best prices\" • since 1994")]
    public void PunctuationAndCurrencyAreLeftAlone(string text)
    {
        Assert.Null(TextEncodingCheck.Repair(text));
    }

    // ---- What it says ----------------------------------------------------------------------

    [Fact]
    public void TheErrorNamesTheFieldTheFileAndWhatItShouldHaveSaid()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TextEncodingCheck.ThrowIfMangled("à®°à®µà®¿ à®®à®³à®¿à®•à¯ˆ", "the store name", @"C:\lane\settings.json"));

        Assert.Contains("the store name", ex.Message);
        Assert.Contains(@"C:\lane\settings.json", ex.Message);
        Assert.Contains("ரவி மளிகை", ex.Message);
        Assert.Contains("UTF-8", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Sri Lakshmi Stores")]
    [InlineData("ரவி மளிகை")]
    public void NothingIsThrownForTextThatIsFine(string? text)
    {
        TextEncodingCheck.ThrowIfMangled(text, "the store name", @"C:\lane\settings.json");
    }
}
