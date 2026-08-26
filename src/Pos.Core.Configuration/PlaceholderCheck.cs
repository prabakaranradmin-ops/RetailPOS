namespace Pos.Core.Configuration;

/// <summary>
/// Refuses to open a lane whose settings still carry the markers the template shipped with.
/// </summary>
/// <remarks>
/// <para>
/// The templates are deliberately full of <c>FILL IN</c> and <c>CHANGE ME</c>, and a file where one
/// was missed is otherwise perfectly valid: it parses, it validates, and the lane trades all day
/// printing <c>GSTIN FILL IN - from the GST certificate</c> at the head of every invoice it issues.
/// </para>
/// <para>
/// Stopping the till is the lesser harm. A lane that will not start is discovered in the first
/// minute by the person who set it up; a placeholder on a GST invoice is discovered by an auditor,
/// after a month of them.
/// </para>
/// </remarks>
public static class PlaceholderCheck
{
    /// <summary>The markers the shipped templates use.</summary>
    private static readonly string[] Markers = ["FILL IN", "CHANGE ME", "CHANGEME"];

    /// <summary>True when the value still holds one of the template's markers.</summary>
    /// <remarks>
    /// <para>
    /// Matched at the start of the value rather than anywhere in it. Every marker in the templates
    /// leads the field — "CHANGE ME - Store Name", "FILL IN - from the GST certificate" — and
    /// searching the whole string finds them inside real words: "Exchange Mediators Ltd" contains
    /// "change me", and a shop by that name would be unable to open its own till.
    /// </para>
    /// <para>
    /// Empty is not a placeholder either. A blank printer name means the lane genuinely has no
    /// printer, and a blank FSSAI number means the shop was never issued one — both real answers.
    /// </para>
    /// </remarks>
    public static bool IsPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.TrimStart();

        foreach (var marker in Markers)
        {
            if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Throws if anything the shop is identified by, or numbered by, is still a placeholder.
    /// </summary>
    /// <remarks>
    /// Every offending field is named in one message rather than the first one found. Whoever is
    /// setting the lane up has the certificate in front of them now; making them run it four times
    /// to be told about four fields is how the fourth one gets guessed at.
    /// </remarks>
    public static void ThrowIfAnyRemain(PosSettings settings, string path)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var unset = new List<string>();

        void Check(string? value, string field)
        {
            if (IsPlaceholder(value))
                unset.Add($"{field} still reads '{value}'");
        }

        Check(settings.Store.Name, "store.name");
        Check(settings.Store.AddressLine1, "store.addressLine1");
        Check(settings.Store.AddressLine2, "store.addressLine2");
        Check(settings.Store.Gstin, "store.gstin");
        Check(settings.Store.FssaiNumber, "store.fssaiNumber");
        Check(settings.Store.CustomerCarePhone, "store.customerCarePhone");
        Check(settings.Store.FooterMessage, "store.footerMessage");
        Check(settings.InvoiceNumber.StorePrefix, "invoiceNumber.storePrefix");
        Check(settings.Hardware.PrinterName, "hardware.printerName");

        if (unset.Count == 0)
            return;

        throw new InvalidOperationException(
            $"The settings file at '{path}' has not been filled in: {string.Join("; ", unset)}. " +
            "Take the store's details from its own GST and FSSAI certificates, and the printer name " +
            "exactly as Windows shows it under Printers & Scanners. Until then the lane would print " +
            "those words on every bill.");
    }
}
