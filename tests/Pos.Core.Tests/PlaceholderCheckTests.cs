using Pos.Core.Configuration;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Refusing to open a lane whose settings were copied but never finished.
/// </summary>
/// <remarks>
/// The shipped templates are full of markers on purpose. A file where one was missed still parses
/// and still validates, so without this the lane trades all day printing
/// <c>GSTIN FILL IN - from the GST certificate</c> at the head of every invoice.
/// </remarks>
public class PlaceholderCheckTests
{
    private static PosSettings Filled() => new()
    {
        LaneId = "L1",
        Store = new StoreProfileSettings
        {
            Name = "ரவி மளிகை",
            AddressLine1 = "No. 3/324, Main Road",
            Gstin = "33AEIPH7795F1Z9",
            FssaiNumber = "12426020000127",
            CustomerCarePhone = "9080678177",
        },
        InvoiceNumber = new InvoiceNumberSettings { StorePrefix = "RM" },
        Hardware = new HardwareSettings { PrinterName = "EPSON TM-T82 Receipt" },
    };

    // ---- What counts as unfinished -----------------------------------------------------------

    [Theory]
    [InlineData("FILL IN")]
    [InlineData("FILL IN - from the GST certificate")]
    [InlineData("CHANGE ME - Store Name")]
    [InlineData("CHANGEME")]
    [InlineData("fill in")]
    [InlineData("change me")]
    public void TheTemplatesOwnMarkersAreRecognised(string value)
    {
        Assert.True(PlaceholderCheck.IsPlaceholder(value));
    }

    /// <summary>
    /// Blank is a real answer, not an unfinished one: a lane with no printer, or a shop that was
    /// never issued an FSSAI number.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankIsNotAPlaceholder(string? value)
    {
        Assert.False(PlaceholderCheck.IsPlaceholder(value));
    }

    [Theory]
    [InlineData("ரவி மளிகை")]
    [InlineData("Sri Lakshmi Stores")]
    [InlineData("33AEIPH7795F1Z9")]
    [InlineData("EPSON TM-T82 Receipt")]
    [InlineData("RM")]
    [InlineData("No. 3/324, Main Road")]
    [InlineData("Exchange Mediators Ltd")]
    public void RealValuesAreLeftAlone(string value)
    {
        Assert.False(PlaceholderCheck.IsPlaceholder(value));
    }

    // ---- What it stops -----------------------------------------------------------------------

    [Fact]
    public void AFullyFilledLaneOpens()
    {
        PlaceholderCheck.ThrowIfAnyRemain(Filled(), @"C:\lane\settings.json");
    }

    [Fact]
    public void AnUnfilledGstinStopsTheLane()
    {
        var settings = Filled();
        settings.Store.Gstin = "FILL IN - from the GST certificate";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlaceholderCheck.ThrowIfAnyRemain(settings, @"C:\lane\settings.json"));

        Assert.Contains("store.gstin", ex.Message);
        Assert.Contains(@"C:\lane\settings.json", ex.Message);
    }

    [Fact]
    public void AnUnchangedInvoicePrefixStopsTheLane()
    {
        var settings = Filled();
        settings.InvoiceNumber.StorePrefix = "CHANGEME";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlaceholderCheck.ThrowIfAnyRemain(settings, @"C:\lane\settings.json"));

        Assert.Contains("invoiceNumber.storePrefix", ex.Message);
    }

    /// <summary>
    /// Whoever is setting the lane up has the certificate in front of them now. Making them run it
    /// four times to be told about four fields is how the fourth one gets guessed at.
    /// </summary>
    [Fact]
    public void EveryUnfilledFieldIsNamedAtOnce()
    {
        var settings = Filled();
        settings.Store.Name = "CHANGE ME - Store Name";
        settings.Store.Gstin = "FILL IN - from the GST certificate";
        settings.Store.FssaiNumber = "FILL IN - from the FSSAI licence";
        settings.Hardware.PrinterName = "CHANGE ME - exact Windows printer name";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlaceholderCheck.ThrowIfAnyRemain(settings, @"C:\lane\settings.json"));

        foreach (var field in new[] { "store.name", "store.gstin", "store.fssaiNumber", "hardware.printerName" })
            Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void ALaneWithNoPrinterAndNoFssaiNumberStillOpens()
    {
        var settings = Filled();
        settings.Hardware.PrinterName = "";
        settings.Store.FssaiNumber = null;
        settings.Store.CustomerCarePhone = null;

        PlaceholderCheck.ThrowIfAnyRemain(settings, @"C:\lane\settings.json");
    }

    /// <summary>The pilot file exactly as it ships: correct in shape, and not yet a shop.</summary>
    [Fact]
    public void ThePilotTemplateAsShippedIsRefused()
    {
        var settings = new PosSettings
        {
            Store = new StoreProfileSettings
            {
                Name = "FILL IN - the shop name, in Tamil",
                AddressLine1 = "FILL IN",
                AddressLine2 = "FILL IN",
                Gstin = "FILL IN - from the GST certificate",
                FssaiNumber = "FILL IN - from the FSSAI licence",
                CustomerCarePhone = "FILL IN",
            },
            InvoiceNumber = new InvoiceNumberSettings { StorePrefix = "RM" },
            Hardware = new HardwareSettings { PrinterName = "CHANGE ME - exact Windows printer name" },
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlaceholderCheck.ThrowIfAnyRemain(settings, @"C:\lane\settings.json"));

        // The prefix is the one thing the pilot file legitimately arrives with already set.
        Assert.DoesNotContain("invoiceNumber.storePrefix", ex.Message);
        Assert.Contains("store.gstin", ex.Message);
    }
}
