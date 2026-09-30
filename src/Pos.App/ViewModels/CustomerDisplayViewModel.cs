using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Pos.Core.Domain;
using Pos.Core.Hardware.Display;

namespace Pos.App.ViewModels;

/// <summary>What the customer's screen is showing.</summary>
public enum CustomerDisplayState
{
    /// <summary>Nothing on the bill: the shop's name.</summary>
    Welcome = 0,

    /// <summary>Items going on: each as it is rung up, and the total.</summary>
    Bill = 1,

    /// <summary>Being paid: the total, what has been paid, and what is left.</summary>
    Paying = 2,

    /// <summary>Paid: the change, and thanks - until the next bill starts.</summary>
    Thanks = 3,

    /// <summary>
    /// Paying back a khata by UPI: the code for it, and nothing else. What a customer owes is not
    /// put on a screen the queue can read unless they are paying it.
    /// </summary>
    Khata = 4,
}

/// <summary>One line of the bill as the customer sees it.</summary>
public sealed record CustomerLine(string Name, string Quantity, string Amount);

/// <summary>
/// The words on the customer's screen, in the lane's bill language: a shop that hands out Tamil
/// bills has a Tamil customer display, not an English one beside them.
/// </summary>
/// <remarks>
/// Taken from the receipt's own labels where the receipt has the word, so the screen and the paper
/// call the total the same thing. The pole display stays in English whatever the lane: a character
/// display has no Tamil to show.
/// </remarks>
public sealed record CustomerDisplayWords(
    string Welcome,
    string ThankYou,
    string YourChange,
    string ScanToPay,
    string Total,
    string Paid,
    string Balance,
    string Saved,
    string PointsEarned,
    string PointsInAll)
{
    public static CustomerDisplayWords For(Pos.Core.Domain.Printing.ReceiptLanguage language)
    {
        var labels = Pos.Core.Domain.Printing.ReceiptLabels.For(language);

        return language == Pos.Core.Domain.Printing.ReceiptLanguage.Tamil
            ? new("வணக்கம்", "நன்றி — மீண்டும் வருக", "உங்கள் மீதம்", labels.ScanToPay, labels.Total,
                labels.AmountPaid, "செலுத்த வேண்டியது", labels.TodaysSaving, "புள்ளிகள் பெற்றீர்கள்", "மொத்த புள்ளிகள்")
            : new("Welcome", "Thank you — please visit again", "YOUR CHANGE", "SCAN TO PAY BY UPI", "TOTAL",
                "Paid", "Balance", "Saved", "points earned", "in all");
    }
}

/// <summary>
/// The customer's side of the counter: a second screen facing them, and the pole display if there
/// is one, following the till as it bills.
/// </summary>
/// <remarks>
/// It watches the till and never drives it: nothing here can change a bill. A customer who can
/// see each item and its price go on as it is scanned is the best check on a mistake there is, and
/// the one the shop does not have to pay for.
/// </remarks>
public sealed class CustomerDisplayViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo Figures = CultureInfo.InvariantCulture;

    private readonly BillingViewModel _billing;
    private readonly IPoleDisplay _pole;
    private readonly Pos.Core.Domain.Printing.ReceiptLanguage _language;
    private (string Top, string Bottom) _poleShowing;
    private bool _thanking;

    public CustomerDisplayViewModel(
        BillingViewModel billing,
        string shopName,
        IPoleDisplay? pole = null,
        Pos.Core.Domain.Printing.ReceiptLanguage language = Pos.Core.Domain.Printing.ReceiptLanguage.English)
    {
        _billing = billing ?? throw new ArgumentNullException(nameof(billing));
        _pole = pole ?? new NoPoleDisplay();
        _language = language;
        Words = CustomerDisplayWords.For(language);
        ShopName = string.IsNullOrWhiteSpace(shopName) ? Words.Welcome : shopName.Trim();

        _billing.PropertyChanged += OnBillingChanged;
        _billing.Lines.CollectionChanged += OnLinesChanged;

        Refresh();
    }

    public string ShopName { get; }

    /// <summary>The screen's words, in the lane's bill language.</summary>
    public CustomerDisplayWords Words { get; }

    public CustomerDisplayState State { get; private set; }

    /// <summary>The bill's lines, the latest last, as many as fit.</summary>
    public ObservableCollection<CustomerLine> Lines { get; } = [];

    /// <summary>The item just rung up, large.</summary>
    public string LastItem { get; private set; } = string.Empty;

    public string LastAmount { get; private set; } = string.Empty;

    public string Total { get; private set; } = string.Empty;

    public string Saving { get; private set; } = string.Empty;

    public string Paid { get; private set; } = string.Empty;

    public string Balance { get; private set; } = string.Empty;

    public string Change { get; private set; } = string.Empty;

    /// <summary>
    /// While they pay by UPI, the code with the exact amount for them to scan, or null. The screen
    /// facing the customer is the best place for it: they need not reach across the counter.
    /// </summary>
    public string? UpiQr { get; private set; }

    /// <summary>The amount the code asks for, large beside it.</summary>
    public string UpiAmount { get; private set; } = string.Empty;

    /// <summary>Who the money goes to, as their app will show it.</summary>
    public string UpiPayee { get; private set; } = string.Empty;

    public bool ShowsUpiQr => UpiQr is not null;

    /// <summary>The bill's lines, except while the UPI code needs the room.</summary>
    public bool ShowsLines => !ShowsUpiQr;

    /// <summary>
    /// Who the bill is for, by first name, and their points only once a sale has changed them.
    /// </summary>
    /// <remarks>
    /// This screen faces the queue. It used to carry the full name and the points balance for the
    /// whole of every bill - "Lakshmi Narayanan Subramaniam - 0 points" - and a mobile number when
    /// there was no name. A first name greets the customer without telling the queue who they are.
    /// </remarks>
    public string Customer { get; private set; } = string.Empty;

    public bool IsWelcome => State == CustomerDisplayState.Welcome;
    public bool IsBill => State == CustomerDisplayState.Bill;
    public bool IsPaying => State == CustomerDisplayState.Paying;
    public bool IsThanks => State == CustomerDisplayState.Thanks;
    public bool IsKhata => State == CustomerDisplayState.Khata;
    public bool HasChange => Change.Length > 0;

    /// <summary>The most lines the screen lists; the rest scroll off the top.</summary>
    public const int MostLines = 12;

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A line going on is the next bill starting: the thanks for the last one is over.
        if (_billing.Lines.Count > 0)
            _thanking = false;

        Refresh();
    }

    private void OnBillingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BillingViewModel.LastSale) && _billing.LastSale is not null)
            _thanking = true;

        if (e.PropertyName is null
            or nameof(BillingViewModel.GrandTotal)
            or nameof(BillingViewModel.Mode)
            or nameof(BillingViewModel.AmountTendered)
            or nameof(BillingViewModel.AmountRemaining)
            or nameof(BillingViewModel.ChangeDue)
            or nameof(BillingViewModel.LastSale)
            or nameof(BillingViewModel.CustomerLabel)
            or nameof(BillingViewModel.SavingsLabel)
            or nameof(BillingViewModel.UpiQr)
            or nameof(BillingViewModel.IsCollectingAmount))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        State = true switch
        {
            _ when _billing.IsTendering => CustomerDisplayState.Paying,
            _ when _billing.IsCollectingAmount && _billing.ShowsUpiQr => CustomerDisplayState.Khata,
            _ when _billing.Lines.Count > 0 => CustomerDisplayState.Bill,
            _ when _thanking && _billing.LastSale is not null => CustomerDisplayState.Thanks,
            _ => CustomerDisplayState.Welcome,
        };

        Lines.Clear();

        foreach (var line in _billing.Lines.TakeLast(MostLines))
        {
            Lines.Add(new CustomerLine(
                line.Name,
                Units.WithQuantity(line.Line.Quantity, line.Line.Unit, _language),
                Money(line.Line.LineTotal)));
        }

        var last = _billing.Lines.LastOrDefault();
        LastItem = last?.Name ?? string.Empty;
        LastAmount = last is null ? string.Empty : Money(last.Line.LineTotal);

        Total = Money(_billing.GrandTotal);
        Saving = _billing.SavedAmount > 0m ? $"{Words.Saved} {Show.Money(_billing.SavedAmount)}" : string.Empty;
        Paid = Money(_billing.AmountTendered);
        Balance = Money(_billing.AmountRemaining);

        Change = State switch
        {
            CustomerDisplayState.Paying when _billing.ChangeDue > 0m => Money(_billing.ChangeDue),
            CustomerDisplayState.Thanks when _billing.LastSale is { ChangeDue: > 0m } sale => Money(sale.ChangeDue),
            _ => string.Empty,
        };

        UpiQr = State is CustomerDisplayState.Paying or CustomerDisplayState.Khata ? _billing.UpiQr : null;
        UpiAmount = UpiQr is null ? string.Empty : _billing.UpiCaption;
        UpiPayee = UpiQr is null ? string.Empty : _billing.UpiPayeeLine;

        if (State == CustomerDisplayState.Thanks && _billing.LastSale is { } done)
        {
            Total = Money(done.Invoice.AmountPayable);

            // The points only now, and only when this sale moved them.
            var name = FirstName(done.Invoice.Sale.Customer?.Name);
            var points = done.PointsEarned > 0 || done.PointsRedeemed > 0
                ? $"{done.PointsEarned} {Words.PointsEarned}, {done.NewLoyaltyBalance ?? 0} {Words.PointsInAll}"
                : string.Empty;

            Customer = string.Join("  ·  ", new[] { name, points }.Where(s => s.Length > 0));
        }
        else
        {
            Customer = _billing.HasCustomer ? FirstName(_billing.Customer?.Name) : string.Empty;
        }

        RaiseAll();
        UpdatePole();
    }

    /// <summary>The two lines the pole display shows for the state the till is in.</summary>
    public (string Top, string Bottom) PoleLines()
    {
        var width = _pole.Width;

        return State switch
        {
            CustomerDisplayState.Bill => (SerialPoleDisplay.Pair(LastItem, LastAmount, width), SerialPoleDisplay.Pair("TOTAL", Total, width)),
            CustomerDisplayState.Paying => HasChange
                ? (SerialPoleDisplay.Pair("TOTAL", Total, width), SerialPoleDisplay.Pair("CHANGE", Change, width))
                : (SerialPoleDisplay.Pair("TOTAL", Total, width), SerialPoleDisplay.Pair("BALANCE", Balance, width)),
            CustomerDisplayState.Thanks => HasChange
                ? (SerialPoleDisplay.Pair("CHANGE", Change, width), "THANK YOU")
                : ("THANK YOU", "VISIT AGAIN"),
            _ => (ShopName, "WELCOME"),
        };
    }

    private void UpdatePole()
    {
        if (!_pole.IsConfigured)
            return;

        var lines = PoleLines();

        // Only when it changes: a serial display written on every keystroke flickers.
        if (lines == _poleShowing)
            return;

        if (_pole.Show(lines.Top, lines.Bottom))
            _poleShowing = lines;
    }

    private static string Money(decimal value) => Show.Figure(value);

    /// <summary>The first word of a name; nothing - never the mobile number - when there is no name.</summary>
    private static string FirstName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    public void Dispose()
    {
        _billing.PropertyChanged -= OnBillingChanged;
        _billing.Lines.CollectionChanged -= OnLinesChanged;
    }
}
