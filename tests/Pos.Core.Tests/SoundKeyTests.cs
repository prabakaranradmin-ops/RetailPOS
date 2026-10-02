using Pos.Core.Domain.Catalogue;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// How a name sounds: the ways a grocery's Tamil words are spelled in English, and the words in
/// Tamil script, folded to the same key.
/// </summary>
public class SoundKeyTests
{
    [Theory]
    // Typed in English the way it is said, against the word in Tamil.
    [InlineData("paruppu", "பருப்பு")]
    [InlineData("thuvaram paruppu", "துவரம் பருப்பு")]
    [InlineData("seeragam", "சீரகம்")]
    [InlineData("vaazhaippazham", "வாழைப்பழம்")]
    [InlineData("ennai", "எண்ணெய்")]
    [InlineData("inji", "இஞ்சி")]
    [InlineData("manjal", "மஞ்சள்")]
    [InlineData("milagu", "மிளகு")]
    [InlineData("thakkali", "தக்காளி")]
    [InlineData("poondu", "பூண்டு")]
    [InlineData("vengayam", "வெங்காயம்")]
    [InlineData("kothamalli", "கொத்தமல்லி")]
    [InlineData("thengai", "தேங்காய்")]
    [InlineData("ulundu", "உளுந்து")]
    [InlineData("kadugu", "கடுகு")]
    [InlineData("kadalai maavu", "கடலை மாவு")]
    [InlineData("arisi", "அரிசி")]
    [InlineData("uppu", "உப்பு")]
    [InlineData("vellam", "வெல்லம்")]
    [InlineData("then", "தேன்")]
    // And the spellings of one word against each other.
    [InlineData("paruppu", "baruppu")]
    [InlineData("paruppu", "parupu")]
    [InlineData("jeeragam", "seeragam")]
    [InlineData("cheeragam", "jeeragam")]
    [InlineData("vazhaipazham", "valaipalam")]
    [InlineData("ennai", "ennei")]
    [InlineData("yennai", "ennai")]
    [InlineData("ennai", "enney")]
    [InlineData("inchi", "inji")]
    [InlineData("thakkali", "takkali")]
    [InlineData("dhal", "dal")]
    [InlineData("kothamalli", "kottamalli")]
    [InlineData("thengai", "thenkai")]
    [InlineData("ulundu", "uzhunthu")]
    [InlineData("kadugu", "gadugu")]
    [InlineData("Toor Dal", "TOOR  dal")]
    public void TheSameWordSpelledTwoWaysSoundsTheSame(string one, string other) =>
        Assert.Equal(SoundKey.Of(one), SoundKey.Of(other));

    [Theory]
    [InlineData("paruppu", "parupu")]
    [InlineData("துவரம் பருப்பு", "tuvaram parupu")]
    [InlineData("Toor Dal 1kg", "tur tal 1k")]
    [InlineData("Sunflower Oil, 1 L", "sunplover oil 1 l")]
    [InlineData("சீரகம்", "sirakam")]
    [InlineData("தேங்காய்", "tenkai")]
    [InlineData("௧௨ வாழைப்பழம்", "12 valaipalam")]
    public void TheKeyIsExactly(string text, string key) => Assert.Equal(key, SoundKey.Of(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" -- , ")]
    public void NothingHasNoKey(string? text) => Assert.Equal(string.Empty, SoundKey.Of(text));

    [Fact]
    public void ALetterWrittenInTwoPartsIsTheSameAsOne()
    {
        // கொ typed as its two halves, ெ and ா, rather than the one character.
        Assert.Equal(SoundKey.Of("கொத்து"), SoundKey.Of("கொத்து"));
    }

    [Theory]
    [InlineData("arisi", "uppu")]
    [InlineData("milagu", "milagai")]
    [InlineData("then", "thinai")]
    [InlineData("வெல்லம்", "வெங்காயம்")]
    public void DifferentWordsStayDifferent(string one, string other) =>
        Assert.NotEqual(SoundKey.Of(one), SoundKey.Of(other));

    [Fact]
    public void HowLongAVowelIsIsNotAsked()
    {
        // English spellings drop it - paal and pal are both milk on a shelf label - so பால் (milk) and
        // பல் (a clove) share a key, and the cashier picks from both. Finding too much is the
        // cheaper mistake at a counter.
        Assert.Equal(SoundKey.Of("பால்"), SoundKey.Of("பல்"));
        Assert.Equal(SoundKey.Of("paal"), SoundKey.Of("பால்"));
    }

    [Fact]
    public void AnInheritedVowelIsWrittenAndThePulliTakesItAway()
    {
        // ம carries its a; ம் does not.
        Assert.Equal("ma", SoundKey.Of("ம"));
        Assert.Equal("m", SoundKey.Of("ம்"));
    }
}
