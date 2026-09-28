using System.Globalization;
using Pos.Core.Domain.Printing;

namespace Pos.Core.Domain;

/// <summary>Where a unit sits in the owner's list, so forty of them can be scanned rather than read.</summary>
public enum UnitGroup
{
    Standard = 0,
    BunchesAndBundles = 1,
    HeapsAndCounts = 2,
    PacketsAndPinches = 3,
    GrainAndWeight = 4,
    FlowerLengths = 5,
}

/// <summary>
/// One way of selling something: what it is called in the catalogue file, on an English bill and
/// on a Tamil one, and whether a customer can buy part of one.
/// </summary>
/// <param name="Code">What the catalogue file says, and what an English bill prints.</param>
/// <param name="Tamil">What a Tamil bill prints.</param>
/// <param name="Fractional">
/// Whether a line may be for part of one. Half a padi of rice and a muzham and a half of jasmine are
/// ordinary sales; half a comb of bananas is not something a shop sells, and a till that allowed it
/// would let a mistyped 1.5 through as a real line.
/// </param>
/// <param name="Meaning">A few words for the owner choosing it, not for the bill.</param>
/// <param name="Aliases">Other spellings a catalogue file may use, compared without case.</param>
public sealed record UnitInfo(
    UnitType Type,
    string Code,
    string Tamil,
    bool Fractional,
    UnitGroup Group,
    string Meaning,
    IReadOnlyList<string> Aliases)
{
    /// <summary>The unit's name on a bill in the given language.</summary>
    public string NameIn(ReceiptLanguage language) => language == ReceiptLanguage.Tamil ? Tamil : Code;

    /// <summary>As the owner's list shows it: the Tamil beside the spelling, and what it means.</summary>
    public string Description => Tamil == Code ? $"{Code} — {Meaning}" : $"{Tamil}  {Code} — {Meaning}";

    public override string ToString() => Description;
}

/// <summary>
/// The units a Tamil Nadu shop sells in: the metric four, and the traditional ones customers still
/// ask for by name — a seepu of bananas, a kattu of greens, a padi of rice, a muzham of jasmine.
/// </summary>
/// <remarks>
/// <para>
/// A traditional unit is a unit of sale, not a conversion. The shop prices a muzham of jasmine and
/// the bill charges that price times the number of muzhams. Nothing here turns a padi into
/// kilograms: what a padi holds differs from district to district and from grain to grain, and a
/// bill that printed a converted figure would state something the shop never measured.
/// </para>
/// <para>
/// Every member of <see cref="UnitType"/> has a row here, and the numbers are what the database
/// holds, so a row is added at the end and never renumbered.
/// </para>
/// </remarks>
public static class Units
{
    private static readonly UnitInfo[] Table =
    [
        // ---- The metric units. Printed as the bill in the photo prints them, in Latin letters
        // on a Tamil bill too: "3 Pcs" is what a Tamil Nadu counter bill actually says.
        Row(UnitType.Each, "Pcs", "Pcs", false, UnitGroup.Standard, "counted, one at a time", "pc", "piece", "pieces", "each", "nos", "no"),
        Row(UnitType.Kilogram, "Kg", "Kg", true, UnitGroup.Standard, "weighed", "kgs", "kilo", "kilogram", "kilograms", "கிலோ"),
        Row(UnitType.Litre, "L", "L", true, UnitGroup.Standard, "measured by volume", "ltr", "litre", "liter", "litres", "லிட்டர்"),
        Row(UnitType.Metre, "m", "m", true, UnitGroup.Standard, "measured by length", "mtr", "metre", "meter", "metres", "மீட்டர்"),

        // ---- Fruit and vegetables that come on the stem.
        Row(UnitType.Seepu, "Seepu", "சீப்பு", false, UnitGroup.BunchesAndBundles, "one comb of bananas"),
        Row(UnitType.Thaar, "Thaar", "தார்", false, UnitGroup.BunchesAndBundles, "a whole bunch of bananas, many combs"),
        Row(UnitType.Kothu, "Kothu", "கொத்து", false, UnitGroup.BunchesAndBundles, "a cluster on one sprig — grapes, curry leaves", "kotthu"),
        Row(UnitType.Kulai, "Kulai", "குலை", false, UnitGroup.BunchesAndBundles, "a heavy natural bunch — coconuts, palm fruit, areca"),
        Row(UnitType.Kattu, "Kattu", "கட்டு", false, UnitGroup.BunchesAndBundles, "a tied bundle — greens, coriander, mint, sugarcane", "kattu"),
        Row(UnitType.Pidi, "Pidi", "பிடி", false, UnitGroup.BunchesAndBundles, "a fistful — curry leaves, greens"),
        Row(UnitType.Mattai, "Mattai", "மட்டை", false, UnitGroup.BunchesAndBundles, "a coconut in its husk"),

        // ---- Market heaps, counts and pieces.
        Row(UnitType.Kooru, "Kooru", "கூறு", false, UnitGroup.HeapsAndCounts, "a sorted heap at a fixed price"),
        Row(UnitType.Koodai, "Koodai", "கூடை", false, UnitGroup.HeapsAndCounts, "a basketful"),
        Row(UnitType.Sulai, "Sulai", "சுளை", false, UnitGroup.HeapsAndCounts, "a pod or segment — jackfruit"),
        Row(UnitType.Pal, "Pal", "பல்", false, UnitGroup.HeapsAndCounts, "a single clove of garlic"),
        Row(UnitType.Muzhu, "Muzhu", "முழு", false, UnitGroup.HeapsAndCounts, "a whole one — pumpkin, lemon", "muzhusu", "muzhuse", "முழுசு"),
        Row(UnitType.Keetru, "Keetru", "கீற்று", false, UnitGroup.HeapsAndCounts, "a slice or wedge — watermelon, pumpkin, coconut", "pathai", "பத்தை"),
        Row(UnitType.Jodi, "Jodi", "ஜோடி", false, UnitGroup.HeapsAndCounts, "a pair", "pair"),
        Row(UnitType.Kavuli, "Kavuli", "கவுளி", false, UnitGroup.HeapsAndCounts, "100 betel leaves", "kavali"),
        Row(UnitType.Suvadu, "Suvadu", "சுவடு", false, UnitGroup.HeapsAndCounts, "50 betel leaves"),
        Row(UnitType.Adukku, "Adukku", "அடுக்கு", false, UnitGroup.HeapsAndCounts, "a layered stack — betel leaves"),

        // ---- Packets, strips and pinches.
        Row(UnitType.Saram, "Saram", "சரம்", false, UnitGroup.PacketsAndPinches, "a tear-off strip of sachets"),
        Row(UnitType.Attai, "Attai", "அட்டை", false, UnitGroup.PacketsAndPinches, "a card of pinned sachets or tablets", "card"),
        Row(UnitType.Pottalam, "Pottalam", "பொட்டலம்", false, UnitGroup.PacketsAndPinches, "a paper packet tied with twine — spices"),
        Row(UnitType.Sittigai, "Sittigai", "சிட்டிகை", false, UnitGroup.PacketsAndPinches, "a pinch — asafoetida, salt", "chittigai", "pinch"),
        Row(UnitType.Thuli, "Thuli", "துளி", false, UnitGroup.PacketsAndPinches, "a drop — ghee, honey, essence", "sottu", "சொட்டு", "drop"),

        // ---- Grain and weight measures. Fractional: a customer asks for half a padi as readily
        // as for a whole one.
        Row(UnitType.Aazhakku, "Aazhakku", "ஆழாக்கு", true, UnitGroup.GrainAndWeight, "the smallest grain measure, about 200 ml", "azhakku", "alakku"),
        Row(UnitType.Uzhakku, "Uzhakku", "உழக்கு", true, UnitGroup.GrainAndWeight, "2 aazhakku", "ulakku"),
        Row(UnitType.Padi, "Padi", "படி", true, UnitGroup.GrainAndWeight, "8 aazhakku — rice, pulses"),
        Row(UnitType.AraiPadi, "AraiPadi", "அரைப்படி", true, UnitGroup.GrainAndWeight, "half a padi", "arai padi", "araippadi", "seru", "ser", "சேர்"),
        Row(UnitType.Marakkaal, "Marakkaal", "மரக்கால்", true, UnitGroup.GrainAndWeight, "8 padi — paddy, millets at harvest", "marakkal", "kuruni", "குறுணி"),
        Row(UnitType.Kalam, "Kalam", "கலம்", true, UnitGroup.GrainAndWeight, "12 marakkaal — bulk grain"),
        Row(UnitType.Moottai, "Moottai", "மூட்டை", false, UnitGroup.GrainAndWeight, "a sack — 25, 50 or 75 kg", "mootai", "sack", "bag"),
        Row(UnitType.Veesai, "Veesai", "வீசை", true, UnitGroup.GrainAndWeight, "about 1.4 kg", "visai"),
        Row(UnitType.Thulaam, "Thulaam", "துலாம்", true, UnitGroup.GrainAndWeight, "about 20 veesai — jaggery, tamarind", "thulam"),

        // ---- Strung flowers, sold by length.
        Row(UnitType.Muzham, "Muzham", "முழம்", true, UnitGroup.FlowerLengths, "elbow to fingertip — jasmine", "mulam"),
        Row(UnitType.Saan, "Saan", "சாண்", true, UnitGroup.FlowerLengths, "a handspan, about half a muzham", "chaan"),
        Row(UnitType.Maaru, "Maaru", "மாறு", true, UnitGroup.FlowerLengths, "an arm span, about 4 muzham", "maru"),
        Row(UnitType.Panthu, "Panthu", "பந்து", false, UnitGroup.FlowerLengths, "a rolled ball of strung flowers", "pandhu"),
    ];

    private static readonly Dictionary<UnitType, UnitInfo> ByType = Table.ToDictionary(u => u.Type);

    private static readonly Dictionary<string, UnitType> ByName = BuildNames();

    /// <summary>Every unit, in the order the owner's list shows them.</summary>
    public static IReadOnlyList<UnitInfo> All => Table;

    /// <summary>
    /// The unit's row. A number no row describes — a database written by a later build — is shown
    /// by its number and allowed fractions, so an old till can still open and reprint such a bill
    /// rather than refusing it.
    /// </summary>
    public static UnitInfo Of(UnitType unit) =>
        ByType.TryGetValue(unit, out var info)
            ? info
            : new UnitInfo(unit, ((int)unit).ToString(CultureInfo.InvariantCulture), ((int)unit).ToString(CultureInfo.InvariantCulture), true, UnitGroup.Standard, "a unit this build does not know", []);

    /// <summary>
    /// Reads a unit as a catalogue file or a person would write it: the English spelling, the Tamil,
    /// or a common variant, in any case.
    /// </summary>
    public static bool TryParse(string? text, out UnitType unit)
    {
        unit = UnitType.Each;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        return ByName.TryGetValue(Normalise(text), out unit);
    }

    /// <summary>
    /// The short English label the owner's screens use beside a quantity: "kg", "pc", "seepu".
    /// </summary>
    public static string ScreenLabel(UnitType unit) => unit switch
    {
        UnitType.Each => "pc",
        UnitType.Kilogram => "kg",
        UnitType.Litre => "L",
        UnitType.Metre => "m",
        _ => Of(unit).Code.ToLowerInvariant(),
    };

    /// <summary>What a bill prints in the quantity column: "2 சீப்பு", "2.75 Kg", "3 Pcs".</summary>
    public static string WithQuantity(decimal quantity, UnitType unit, ReceiptLanguage language) =>
        $"{quantity.ToString("0.###", CultureInfo.InvariantCulture)} {Of(unit).NameIn(language)}";

    /// <summary>A few spellings for an error message, so a shop that typed a unit wrongly sees what would do.</summary>
    public static string Examples => "Pcs, Kg, L, Seepu, Kattu, Padi, Muzham";

    private static UnitInfo Row(
        UnitType type,
        string code,
        string tamil,
        bool fractional,
        UnitGroup group,
        string meaning,
        params string[] aliases) =>
        new(type, code, tamil, fractional, group, meaning, aliases);

    private static Dictionary<string, UnitType> BuildNames()
    {
        var names = new Dictionary<string, UnitType>(StringComparer.Ordinal);

        foreach (var unit in Table)
        {
            foreach (var name in unit.Aliases.Prepend(unit.Code).Prepend(unit.Tamil))
            {
                var key = Normalise(name);

                // A spelling that meant two units would import a line as whichever was listed
                // last. The table is fixed, so this is a mistake in the table, caught at start-up.
                if (names.TryGetValue(key, out var existing) && existing != unit.Type)
                    throw new InvalidOperationException($"'{name}' names both {existing} and {unit.Type}.");

                names[key] = unit.Type;
            }
        }

        return names;
    }

    /// <summary>Case and spacing are not what distinguishes one unit from another.</summary>
    private static string Normalise(string text) =>
        string.Concat(text.Trim().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '.'))
            .Normalize(System.Text.NormalizationForm.FormC);
}
