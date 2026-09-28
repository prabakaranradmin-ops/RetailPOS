namespace Pos.Core.Domain;

/// <summary>
/// The Unique Quantity Code a return reports each unit under, written as the GST tools write it:
/// "KGS-KILOGRAMS".
/// </summary>
/// <remarks>
/// <para>
/// The HSN summary in a return states how much of each code was sold, in one of a fixed list of
/// units. The metric units have their own codes. A traditional unit is reported under the code that
/// honestly describes it — a seepu is a bunch, a kattu a bundle, a moottai a bag, a jodi a pair —
/// and under OTH where nothing fits.
/// </para>
/// <para>
/// Nothing is converted to get there. A padi is not reported as 1.5 kilograms, because nobody
/// weighed it: it goes under OTH as the number of padis sold, which is what happened.
/// </para>
/// </remarks>
public static class Uqc
{
    public const string Pieces = "PCS-PIECES";
    public const string Kilograms = "KGS-KILOGRAMS";
    public const string Litres = "LTR-LITRES";
    public const string Metres = "MTR-METERS";
    public const string Numbers = "NOS-NUMBERS";
    public const string Bunches = "BUN-BUNCHES";
    public const string Bundles = "BDL-BUNDLES";
    public const string Bags = "BAG-BAGS";
    public const string Pairs = "PRS-PAIRS";
    public const string Packs = "PAC-PACKS";
    public const string Others = "OTH-OTHERS";

    public static string For(UnitType unit) => unit switch
    {
        UnitType.Each => Pieces,
        UnitType.Kilogram => Kilograms,
        UnitType.Litre => Litres,
        UnitType.Metre => Metres,

        UnitType.Seepu or UnitType.Thaar or UnitType.Kothu or UnitType.Kulai => Bunches,
        UnitType.Kattu => Bundles,
        UnitType.Moottai => Bags,
        UnitType.Jodi => Pairs,
        UnitType.Pottalam => Packs,

        // Counted one at a time: a coconut, a pod, a clove, a whole pumpkin, a slice.
        UnitType.Mattai or UnitType.Sulai or UnitType.Pal or UnitType.Muzhu or UnitType.Keetru => Numbers,

        // Measures, heaps, strips and pinches with no code of their own, and anything a later
        // build adds that this one does not know.
        _ => Others,
    };
}
