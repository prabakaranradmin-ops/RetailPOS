using System.Collections.ObjectModel;
using System.Globalization;
using Pos.Core.Analytics;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>
/// The owner's view of one customer at a time: who they are, what they spend, when, and on what.
/// </summary>
/// <remarks>
/// <para>
/// Everything shown is read back out of the bills by <see cref="CustomerQuery"/>; nothing is
/// stored for the purpose, so a customer's history cannot drift from the books it came from.
/// </para>
/// <para>
/// Behind the owner's PIN like the rest of this screen, and deliberately so: this is a list of
/// people's names, phone numbers and shopping habits, which is not something to leave one keystroke
/// from a cashier. It is also where a customer who asks to be forgotten is forgotten.
/// </para>
/// </remarks>
public sealed class CustomersViewModel : ObservableObject
{
    private static readonly CultureInfo Indian = CultureInfo.GetCultureInfo("en-IN");

    private readonly CustomerQuery _query;
    private readonly ICustomerStore _store;
    private readonly ICreditStore? _credit;

    private bool _onlyOwing;
    private string _totalOwedLine = string.Empty;
    private string _searchText = string.Empty;
    private CustomerSummary? _selected;
    private CustomerProfile? _profile;
    private string _editName = string.Empty;
    private string _editGstin = string.Empty;
    private string _editAddress = string.Empty;
    private Customer? _record;
    private string _status = string.Empty;

    public CustomersViewModel(CustomerQuery query, ICustomerStore store, ICreditStore? credit = null)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _credit = credit;
    }

    // ---- Credit ----------------------------------------------------------------------------------

    /// <summary>Just the customers who owe, most first: the end-of-month list.</summary>
    public bool OnlyOwing
    {
        get => _onlyOwing;
        set
        {
            if (Set(ref _onlyOwing, value))
                Search();
        }
    }

    /// <summary>What the shop is owed on the khata, in all, for the head of the list.</summary>
    public string TotalOwedLine
    {
        get => _totalOwedLine;
        private set => Set(ref _totalOwedLine, value);
    }

    public bool HasCredit => _credit is not null;

    /// <summary>
    /// Whether anybody owes anything: the total is amber then, and the quiet ink when nobody does.
    /// Money owed to the shop is something to see to, not a fault, and it was red.
    /// </summary>
    public bool ShopIsOwed
    {
        get => _shopIsOwed;
        private set => Set(ref _shopIsOwed, value);
    }

    private bool _shopIsOwed;

    /// <summary>The chosen customer's khata, newest first, each line with the balance after it.</summary>
    public ObservableCollection<CreditMovement> Khata { get; } = [];

    public bool HasKhata => Khata.Count > 0;

    /// <summary>What the chosen customer owes, or empty when they owe nothing.</summary>
    public string Owes => _profile is { Customer.Owed: > 0m } p ? $"Owes {Show.Money(p.Customer.Owed)} on the khata" : string.Empty;

    public bool OwesAnything => _profile is { Customer.Owed: > 0m };

    private void RefreshTotalOwed()
    {
        if (_credit is null)
        {
            TotalOwedLine = string.Empty;
            return;
        }

        try
        {
            var owing = _credit.Owing(10_000);

            ShopIsOwed = owing.Count > 0;
            TotalOwedLine = owing.Count == 0
                ? "Nobody owes the shop anything on the khata."
                : $"{Show.Money(owing.Sum(o => o.Owed))} owed to the shop by {Plural.Of(owing.Count, "customer")}.";
        }
        catch (Exception ex)
        {
            TotalOwedLine = $"What is owed could not be read: {ex.Message}";
        }
    }

    // ---- Statements ------------------------------------------------------------------------------

    /// <summary>The shop's name, for the message a statement is sent as.</summary>
    public string ShopName { get; set; } = string.Empty;

    /// <summary>The shop's UPI ID, for the code to pay a statement with. Null: no code.</summary>
    public UpiPayee? Upi { get; set; }

    /// <summary>Puts text on the clipboard. Set by the composition root.</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>Lays statements out as a page to print or send. Set by the composition root.</summary>
    public Func<IReadOnlyList<KhataStatement>, string>? RenderStatements { get; set; }

    /// <summary>
    /// The chosen customer's statement - everything since they last owed nothing - as a message
    /// on the clipboard, to paste into WhatsApp.
    /// </summary>
    /// <returns>What went wrong, or null.</returns>
    public string? CopyStatement()
    {
        if (Statement() is not { } statement)
            return Status;

        try
        {
            if (CopyText is not { } copy)
                return Status = "This screen cannot reach the clipboard.";

            copy(statement.Message(ShopName, Upi));
        }
        catch (Exception ex)
        {
            return Status = $"The message could not be copied: {ex.Message}";
        }

        Status = $"{statement.Customer.Name ?? statement.Customer.MobileNo}'s statement is on the clipboard: {Money(Math.Max(0m, statement.Closing))} owed. Paste it into WhatsApp.";
        return null;
    }

    /// <summary>The chosen customer's statement as a page, for the owner to save and print or send.</summary>
    /// <returns>The page, or null with <see cref="Status"/> saying why not.</returns>
    public string? StatementPage() =>
        Statement() is { } statement ? Render([statement]) : null;

    /// <summary>
    /// A statement for everybody who owes, one a page: the month-end round, printed in one go.
    /// </summary>
    /// <returns>The page, or null with <see cref="Status"/> saying why not.</returns>
    public string? EveryoneOwingPage()
    {
        if (_credit is null)
        {
            Status = "The khata is not available on this lane.";
            return null;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var statements = new List<KhataStatement>();

        try
        {
            foreach (var owing in _credit.Owing(10_000))
            {
                var customer = new Customer { Id = owing.CustomerId, MobileNo = owing.MobileNo, Name = owing.Name };
                statements.Add(KhataStatement.SinceLastClear(customer, _credit.Ledger(owing.CustomerId), today));
            }
        }
        catch (Exception ex)
        {
            Status = $"The khatas could not be read: {ex.Message}";
            return null;
        }

        if (statements.Count == 0)
        {
            Status = "Nobody owes anything on the khata.";
            return null;
        }

        return Render(statements);
    }

    private string? Render(IReadOnlyList<KhataStatement> statements)
    {
        if (RenderStatements is not { } render)
        {
            Status = "This screen cannot lay out a statement.";
            return null;
        }

        return render(statements);
    }

    /// <summary>A bill as a full A4 invoice, by its number. Set by the composition root.</summary>
    public Func<string, string?>? RenderBill { get; set; }

    /// <summary>The bill picked in the recent bills, for saving as a page.</summary>
    public CustomerBill? SelectedBill { get; set; }

    /// <summary>The picked bill as a full A4 invoice to print or send - the same bill, laid out for paper.</summary>
    /// <returns>The page, or null with <see cref="Status"/> saying why not.</returns>
    public string? BillPage()
    {
        if (SelectedBill is not { } bill)
        {
            Status = "Pick one of their recent bills first.";
            return null;
        }

        if (RenderBill is not { } render)
        {
            Status = "This screen cannot lay out a bill.";
            return null;
        }

        try
        {
            if (render(bill.InvoiceNo) is { } page)
                return page;

            Status = $"{bill.InvoiceNo} could not be found.";
        }
        catch (Exception ex)
        {
            Status = $"{bill.InvoiceNo} could not be read: {ex.Message}";
        }

        return null;
    }

    /// <summary>What the page was saved as: said once the owner has picked where.</summary>
    public void Saved(string path, int statements) =>
        Status = statements == 1 ? $"Statement saved to {path}." : $"{Plural.Of(statements, "statement")} saved to {path}, one a page.";

    private KhataStatement? Statement()
    {
        if (_profile is null)
        {
            Status = "Pick a customer first.";
            return null;
        }

        if (_credit is null)
        {
            Status = "The khata is not available on this lane.";
            return null;
        }

        var summary = _profile.Customer;
        var customer = new Customer { Id = summary.Id, MobileNo = summary.MobileNo, Name = summary.Name };

        try
        {
            var statement = KhataStatement.SinceLastClear(customer, _credit.Ledger(customer.Id), DateOnly.FromDateTime(DateTime.Today));

            if (statement.Lines.Count == 0 && statement.Closing == 0m)
            {
                Status = $"{customer.Name ?? customer.MobileNo} owes nothing on the khata: there is no statement to send.";
                return null;
            }

            return statement;
        }
        catch (Exception ex)
        {
            Status = $"Their khata could not be read: {ex.Message}";
            return null;
        }
    }

    /// <summary>Customers matching the search, or the best customers when it is blank.</summary>
    public ObservableCollection<CustomerSummary> Results { get; } = [];

    /// <summary>Spend by month, oldest first, a zero for a month they did not come in.</summary>
    public ObservableCollection<TrendBar> Months { get; } = [];

    /// <summary>The same months as a chart. Null when nobody is picked.</summary>
    public Pos.App.Charts.CategoryChartData? MonthsChart { get; private set; }

    /// <summary>What they buy most, by what it came to.</summary>
    public ObservableCollection<RankedRow> TopItems { get; } = [];

    public ObservableCollection<CustomerBill> RecentBills { get; } = [];

    /// <summary>Name or part of a mobile number. Blank lists the best customers.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty))
                Search();
        }
    }

    public CustomerSummary? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                LoadProfile();
        }
    }

    public bool HasSelection => _profile is not null;

    public bool ShowsHint => _profile is null;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>What the list heading says it is showing.</summary>
    public string ResultsHeading => (_searchText.Trim().Length == 0, _onlyOwing) switch
    {
        (true, true) => "WHO OWES WHAT",
        (true, false) => "BEST CUSTOMERS",
        (false, true) => $"OWING, MATCHING \"{_searchText.Trim()}\"",
        _ => $"MATCHING \"{_searchText.Trim()}\"",
    };

    // ---- The chosen customer ---------------------------------------------------------------------

    public string Title => _profile?.Customer.Label ?? string.Empty;

    public string Mobile => _profile?.Customer.MobileNo ?? string.Empty;

    public string Points => _profile is null ? "0" : _profile.Customer.LoyaltyBalance.ToString("N0", Indian);

    public string Visits => _profile is null ? "0" : _profile.Customer.Visits.ToString("N0", Indian);

    public string Spent => Money(_profile?.Customer.Spent ?? 0m);

    public string AverageBasket => Money(_profile?.AverageBasket ?? 0m);

    public string FirstVisit => Day(_profile?.FirstVisit);

    public string LastVisit => Day(_profile?.Customer.LastVisit);

    /// <summary>
    /// How long since they were last in, in words.
    /// </summary>
    /// <remarks>
    /// The single most useful line on this screen for a shop that knows its regulars: somebody who
    /// came in every week and has not been seen for six is a customer about to be lost, and that is
    /// easy to miss in a date.
    /// </remarks>
    public string SinceLastVisit
    {
        get
        {
            if (_profile?.Customer.LastVisit is not { } last)
                return "Has not bought anything yet.";

            var days = (DateTime.Today - last.LocalDateTime.Date).Days;

            return days switch
            {
                <= 0 => "In today.",
                1 => "Last in yesterday.",
                < 60 => $"Last in {days} days ago.",
                _ => $"Last in {days / 30} months ago.",
            };
        }
    }

    /// <summary>The name box, prefilled with what is on file.</summary>
    public string EditName
    {
        get => _editName;
        set => Set(ref _editName, value ?? string.Empty);
    }

    // ---- Actions ---------------------------------------------------------------------------------

    /// <summary>Reads the list again. Called when the tab opens.</summary>
    public void Search()
    {
        var keep = _selected?.Id;

        Results.Clear();

        try
        {
            foreach (var customer in _query.Find(_searchText, OnlyOwing ? 500 : 50, OnlyOwing))
                Results.Add(customer);

            Status = Results.Count == 0 && _searchText.Trim().Length > 0
                ? $"No customer matches \"{_searchText.Trim()}\"."
                : Results.Count == 0 && OnlyOwing
                    ? "Nobody owes anything on the khata."
                    : string.Empty;
        }
        catch (Exception ex)
        {
            // The owner's screen must not take the till down with it.
            Status = $"The customers could not be read: {ex.Message}";
        }

        Raise(nameof(ResultsHeading));
        RefreshTotalOwed();

        // Keep the customer on screen if they are still in the list, so correcting a search does
        // not throw away the one being looked at - and read them again while at it, since the
        // list was re-read for a reason and a renamed customer should not keep their old name.
        if (keep is { } id && Results.FirstOrDefault(r => r.Id == id) is { } still)
        {
            _selected = still;
            Raise(nameof(Selected));
            LoadProfile();
        }
    }

    /// <summary>The GSTIN box, prefilled with what is on file.</summary>
    public string EditGstin
    {
        get => _editGstin;
        set => Set(ref _editGstin, value ?? string.Empty);
    }

    /// <summary>The address box, prefilled with what is on file.</summary>
    public string EditAddress
    {
        get => _editAddress;
        set => Set(ref _editAddress, value ?? string.Empty);
    }

    /// <summary>Whether the chosen customer is a business, and what that means for their bills.</summary>
    public string BusinessLine => _record?.Gstin is { } gstin
        ? $"A business: GSTIN {gstin}, {GstStates.Label(Gstin.StateCode(gstin))}. Their bills are tax invoices to a registered buyer, filed bill by bill (B2B)."
        : "Not a business: their bills carry no GSTIN. Give them one here, or with Ctrl+G at the till.";

    /// <summary>Makes the chosen customer a business - their GSTIN and address - or takes the GSTIN off with an empty box.</summary>
    /// <returns>What went wrong, or null.</returns>
    public string? SaveBusiness()
    {
        if (_profile is null)
            return "Pick a customer first.";

        var gstin = EditGstin.Trim();

        if (gstin.Length > 0 && Gstin.Problem(gstin) is { } problem)
            return problem;

        try
        {
            _record = _store.SetBusiness(_profile.Customer.Id, gstin.Length == 0 ? null : gstin, EditAddress);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
        }
        catch (Exception ex)
        {
            return $"Could not save it: {ex.Message}";
        }

        EditGstin = _record.Gstin ?? string.Empty;
        EditAddress = _record.Address ?? string.Empty;
        Raise(nameof(BusinessLine));

        Status = _record.Gstin is { } saved
            ? $"Saved. {_record.Name ?? _record.MobileNo}'s bills from now on carry GSTIN {saved}."
            : $"The GSTIN is taken off. {_record.Name ?? _record.MobileNo}'s bills are ordinary bills again.";
        return null;
    }

    /// <summary>Gives the chosen customer a name, changes it, or clears it.</summary>
    /// <returns>What went wrong, or null.</returns>
    public string? SaveName()
    {
        if (_profile is null)
            return "Pick a customer first.";

        var name = EditName.Trim();

        try
        {
            _store.Rename(_profile.Customer.Id, name.Length == 0 ? null : name);
        }
        catch (Exception ex)
        {
            return $"Could not save it: {ex.Message}";
        }

        // The list is read again first and the message set after, because reading the list resets
        // the status line - the other way round, the owner never saw that the save had worked.
        Search();

        Status = name.Length == 0 ? "Name cleared." : $"Saved. Their bills will say {name} from now on.";
        return null;
    }

    /// <summary>
    /// Forgets the chosen customer: their name, number and points go, their bills stay.
    /// </summary>
    /// <returns>What went wrong, or null.</returns>
    public string? Forget()
    {
        if (_profile is null)
            return "Pick a customer first.";

        var who = _profile.Customer.Label;
        int unlinked;

        try
        {
            unlinked = _store.Forget(_profile.Customer.Id);
        }
        catch (Exception ex)
        {
            return $"Could not forget them: {ex.Message}";
        }

        if (unlinked < 0)
            return $"{who} is no longer on file.";

        _selected = null;
        Raise(nameof(Selected));
        LoadProfile();
        Search();

        // After the list is re-read, which clears the status line.
        Status = $"{who} has been forgotten. {Plural.Of(unlinked, "bill")} kept, no longer linked to anyone.";
        return null;
    }

    /// <summary>Selects a customer by id, if they are in the list.</summary>
    public void Select(long customerId) =>
        Selected = Results.FirstOrDefault(r => r.Id == customerId);

    private void LoadProfile()
    {
        Months.Clear();
        TopItems.Clear();
        RecentBills.Clear();
        Khata.Clear();
        MonthsChart = null;

        _profile = null;

        if (_selected is { } chosen)
        {
            try
            {
                _profile = _query.Profile(chosen.Id);
            }
            catch (Exception ex)
            {
                Status = $"That customer's history could not be read: {ex.Message}";
            }
        }

        if (_profile is { } profile)
        {
            FillMonths(profile);
            FillTopItems(profile);

            foreach (var bill in profile.RecentBills)
                RecentBills.Add(bill);

            try
            {
                foreach (var line in _credit?.History(profile.Customer.Id) ?? [])
                    Khata.Add(line);
            }
            catch (Exception ex)
            {
                Status = $"Their khata could not be read: {ex.Message}";
            }
        }

        EditName = _profile?.Customer.Name ?? string.Empty;

        // The GSTIN and address are on the customer's own record rather than in the summary.
        try
        {
            _record = _profile is { } picked ? _store.FindByMobile(picked.Customer.MobileNo) : null;
        }
        catch (Exception)
        {
            _record = null;
        }

        EditGstin = _record?.Gstin ?? string.Empty;
        EditAddress = _record?.Address ?? string.Empty;
        Raise(nameof(BusinessLine));

        foreach (var name in new[]
                 {
                     nameof(HasSelection), nameof(ShowsHint), nameof(Title), nameof(Mobile), nameof(Points),
                     nameof(Visits), nameof(Spent), nameof(AverageBasket), nameof(FirstVisit),
                     nameof(LastVisit), nameof(SinceLastVisit), nameof(Owes), nameof(OwesAnything),
                     nameof(HasKhata), nameof(MonthsChart),
                 })
        {
            Raise(name);
        }
    }

    private void FillMonths(CustomerProfile profile)
    {
        MonthsChart = OwnerCharts.CustomerMonths(profile.Months);

        var best = profile.Months.Count == 0 ? 0m : profile.Months.Max(m => m.Spent);

        foreach (var month in profile.Months)
        {
            var date = month.Month.ToDateTime(TimeOnly.MinValue);

            Months.Add(new TrendBar(
                date.ToString("MMM", CultureInfo.InvariantCulture),
                month.Spent,
                best == 0m ? 0 : (double)(month.Spent / best),
                $"{date.ToString("MMMM yyyy", CultureInfo.InvariantCulture)} - {Show.Money(month.Spent)} over {Plural.Of(month.Bills, "bill")}"));
        }
    }

    private void FillTopItems(CustomerProfile profile)
    {
        var best = profile.TopItems.Count == 0 ? 0m : profile.TopItems.Max(i => i.Spent);

        foreach (var item in profile.TopItems)
        {
            var unit = item.Unit == UnitType.Each ? "pcs" : Units.ScreenLabel(item.Unit);

            TopItems.Add(new RankedRow(
                item.Name,
                $"{item.Quantity.ToString("0.###", Indian)} {unit} over {Plural.Of(item.Bills, "bill")}",
                Money(item.Spent),
                best == 0m ? 0 : (double)(item.Spent / best)));
        }
    }

    private static string Money(decimal amount) => amount.ToString("N2", Indian);

    private static string Day(DateTimeOffset? at) =>
        at is { } value ? Show.Date(value.ToLocalTime()) : "-";
}
