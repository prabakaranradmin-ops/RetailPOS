using System.Text;
using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// A bill to a business at the counter: F7 for the customer, Ctrl+G for their GSTIN and address, and
/// the bill is a tax invoice to them - taxed by their state, printed with who and where.
/// </summary>
public class BusinessTillTests
{
    private const string Kumar = "29AABCK1234M1ZG";

    private static BillingHarness Till() => new(
        Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 210m, gstRate: 5m));

    private static void Attach(BillingHarness till, string mobile = "9800011122")
    {
        till.Press(Key.F7);
        till.ViewModel.EditBuffer = mobile;
        till.Press(Key.Enter);
    }

    private static void GiveGstin(BillingHarness till, string gstin = Kumar, string address = "12 MG Road, Bengaluru")
    {
        Assert.True(till.Press(Key.G, ModifierKeys.Control));
        Assert.True(till.ViewModel.IsSettingBusiness);
        till.ViewModel.EditBuffer = gstin;
        till.Press(Key.Enter);
        Assert.Equal(BusinessStage.Address, till.ViewModel.BusinessStage);
        till.ViewModel.EditBuffer = address;
        till.Press(Key.Enter);
    }

    [Fact]
    public void CtrlGMakesTheCustomerABusinessAndRetaxesTheBill()
    {
        using var till = Till();
        till.AddCustomer("9800011122", name: "Kumar Traders");
        Attach(till);
        till.Scan("8901234567890");
        Assert.Equal(0m, till.ViewModel.Lines[0].IgstRate);

        GiveGstin(till);

        Assert.False(till.ViewModel.IsSettingBusiness);
        Assert.Equal(5m, till.ViewModel.Lines[0].IgstRate);
        Assert.Contains($"Kumar Traders is a business: GSTIN {Kumar}, 29-Karnataka", till.ViewModel.StatusMessage);
        Assert.Contains("taxed as inter-state: IGST", till.ViewModel.StatusMessage);

        var saved = till.Customers.FindByMobile("9800011122")!;
        Assert.Equal(Kumar, saved.Gstin);
        Assert.Equal("12 MG Road, Bengaluru", saved.Address);
    }

    [Fact]
    public void TheBillPrintsWhoItWasTo()
    {
        using var till = Till();
        till.AddCustomer("9800011122", name: "Kumar Traders");
        Attach(till);
        till.Scan("8901234567890");
        GiveGstin(till);

        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        var sale = till.Invoices.FindLatest(BillingHarness.LaneId)!;
        Assert.Equal(Kumar, sale.Sale.Buyer!.Gstin);

        var paper = Encoding.ASCII.GetString(till.Printer.LastJob);
        Assert.Contains("Bill to: Kumar Traders", paper);
        Assert.Contains(Kumar, paper);
        Assert.Contains("29-Karnataka", paper);
    }

    [Fact]
    public void AGstinThatDoesNotCheckOutIsSaidAndNotSaved()
    {
        using var till = Till();
        till.AddCustomer("9800011122", name: "Kumar Traders");
        Attach(till);

        till.Press(Key.G, ModifierKeys.Control);
        till.ViewModel.EditBuffer = "29AABCK1234M1ZH";
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsSettingBusiness);
        Assert.Equal(BusinessStage.Gstin, till.ViewModel.BusinessStage);
        Assert.Contains("last character", till.ViewModel.StatusMessage);
        Assert.Null(till.Customers.FindByMobile("9800011122")!.Gstin);
    }

    [Fact]
    public void CtrlGWithNobodyOnTheBillSaysHow()
    {
        using var till = Till();

        till.Press(Key.G, ModifierKeys.Control);

        Assert.False(till.ViewModel.IsSettingBusiness);
        Assert.Contains("Attach the customer first with F7", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void EscapeSavesNothing()
    {
        using var till = Till();
        till.AddCustomer("9800011122", name: "Kumar Traders");
        Attach(till);

        till.Press(Key.G, ModifierKeys.Control);
        till.ViewModel.EditBuffer = Kumar;
        till.Press(Key.Enter);
        till.Press(Key.Escape);

        Assert.False(till.ViewModel.IsSettingBusiness);
        Assert.Null(till.Customers.FindByMobile("9800011122")!.Gstin);
    }

    /// <summary>A business is found by its GSTIN at F7 as readily as by its number.</summary>
    [Fact]
    public void F7FindsABusinessByItsGstin()
    {
        using var till = Till();
        var kumar = till.AddCustomer("9800011122", name: "Kumar Traders");
        till.Customers.SetBusiness(kumar.Id, Kumar, null);

        Attach(till, Kumar);

        Assert.True(till.ViewModel.HasCustomer);
        Assert.Contains("a bill to a business, GSTIN", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void AnEmptyBoxTakesTheGstinOff()
    {
        using var till = Till();
        var kumar = till.AddCustomer("9800011122", name: "Kumar Traders");
        till.Customers.SetBusiness(kumar.Id, Kumar, "12 MG Road");
        Attach(till);

        till.Press(Key.G, ModifierKeys.Control);
        Assert.Equal(Kumar, till.ViewModel.EditBuffer);
        till.ViewModel.EditBuffer = string.Empty;
        till.Press(Key.Enter);

        Assert.Null(till.Customers.FindByMobile("9800011122")!.Gstin);
        Assert.Contains("GSTIN is taken off", till.ViewModel.StatusMessage);
    }

    // ---- The owner's screen ----------------------------------------------------------------------

    [Fact]
    public void TheOwnerGivesACustomerAGstin()
    {
        using var temp = new TempDatabase();
        var customers = new CustomerRepository(temp.Database);
        customers.Add(new Customer { MobileNo = "9800011122", Name = "Kumar Traders", StateCode = "33" });
        var screen = new CustomersViewModel(new CustomerQuery(temp.Database), customers);
        screen.SearchText = "Kumar";
        screen.Selected = screen.Results[0];
        Assert.Contains("Not a business", screen.BusinessLine);

        screen.EditGstin = Kumar;
        screen.EditAddress = "12 MG Road, Bengaluru";

        Assert.Null(screen.SaveBusiness());
        Assert.Contains($"GSTIN {Kumar}, 29-Karnataka", screen.BusinessLine);

        screen.EditGstin = "not a gstin";
        Assert.NotNull(screen.SaveBusiness());
        Assert.Equal(Kumar, customers.FindByMobile("9800011122")!.Gstin);
    }
}
