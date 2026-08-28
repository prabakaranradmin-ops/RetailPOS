using System.Reflection;
using Pos.Core.Domain;

namespace Pos.Core.Configuration;

/// <summary>Which build of the software this is.</summary>
public enum Variant
{
    /// <summary>Charges GST and issues tax invoices. The setting is the shop's to change.</summary>
    Gst = 0,

    /// <summary>
    /// Issues bills of supply and never charges tax. There is nothing to switch: a build shipped to
    /// a composition dealer does not offer to start collecting GST.
    /// </summary>
    NoTax = 1,
}

/// <summary>
/// The variant this executable was built as.
/// </summary>
/// <remarks>
/// <para>
/// Two builds go out from one codebase: one that charges GST and one that does not. The difference
/// is deliberately in the binary rather than in a settings file, because a settings file can be
/// copied from the wrong lane, edited by the wrong person, or simply not copied at all — and the
/// question it answers is which legal document the shop issues.
/// </para>
/// <para>
/// Stamped at build time by <c>-p:Variant=NoTax</c> and read back from the entry assembly, so the
/// tool and the till each report what they were actually built as rather than what a file beside
/// them claims.
/// </para>
/// <para>
/// Anything unrecognised, missing, or built without the property reads as <see cref="Variant.Gst"/>.
/// That is what every build before this existed was, and it is the safer way to be wrong: a shop
/// that charges GST and gets a tax invoice is correct, where one that does not and gets a tax
/// invoice has issued a document claiming tax it never collected.
/// </para>
/// </remarks>
public static class ProductVariant
{
    private static readonly Lazy<Variant> Resolved = new(ReadFromAssembly);

    /// <summary>What this executable was built as.</summary>
    public static Variant Current => Resolved.Value;

    /// <summary>True when this build never charges tax, whatever the settings say.</summary>
    public static bool ChargesNoTax => Current == Variant.NoTax;

    /// <summary>
    /// The tax mode this build will actually use, given what the settings ask for.
    /// </summary>
    /// <remarks>
    /// On the no-tax build the answer is always <see cref="TaxMode.Composition"/>. A settings file
    /// carried over from a GST lane cannot make this build start issuing tax invoices.
    /// </remarks>
    public static TaxMode Resolve(TaxMode configured) => Resolve(configured, Current);

    /// <inheritdoc cref="Resolve(TaxMode)"/>
    /// <remarks>
    /// Takes the variant rather than reading it, so the rule can be exercised for both builds. A
    /// test cannot restamp the assembly it is running in, and a rule only ever checked against the
    /// build that happens to be running is not checked at all.
    /// </remarks>
    public static TaxMode Resolve(TaxMode configured, Variant variant) =>
        variant == Variant.NoTax ? TaxMode.Composition : configured;

    /// <summary>How to describe this build to somebody reading a screen.</summary>
    public static string Description => Describe(Current);

    /// <inheritdoc cref="Description"/>
    public static string Describe(Variant variant) => variant == Variant.NoTax
        ? "This build issues a BILL OF SUPPLY and never charges GST."
        : "This build issues a TAX INVOICE and charges GST.";

    private static Variant ReadFromAssembly()
    {
        var assembly = Assembly.GetEntryAssembly();

        var stamped = assembly?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "Variant", StringComparison.OrdinalIgnoreCase))?
            .Value;

        return Enum.TryParse<Variant>(stamped, ignoreCase: true, out var variant) ? variant : Variant.Gst;
    }
}
