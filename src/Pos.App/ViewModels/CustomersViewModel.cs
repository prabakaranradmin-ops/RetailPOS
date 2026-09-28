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

    /// <summary>What the shop is owed on credit, in all, for the head of the list.</summary>
    public string TotalOwedLine
    {
        get => _totalOwedLine;
        private set => Set(ref _totalOwedLine, value);
    }

    public bool HasCredit => _credit is not null;

    /// <summary>The chosen customer's khata, newest first, each line with the balance after it.</summary>
    public ObservableCollection<CreditMovement> Khata { get; } = [];

    public bool HasKhata => Khata.Count > 0;

    /// <summary>What the chosen customer owes, or empty when they owe nothing.</summary>
    public string Owes => _profile is { Customer.Owed: > 0m } p ? $"Owes {Money(p.Customer.Owed)} on credit" : string.Empty;

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

            TotalOwedLine = owing.Count == 0
                ? "Nobody owes the shop anything on credit."
                : $"{Money(owing.Sum(o => o.Owed))} owed to the shop by {owing.Count} customer(s).";
        }
        catch (Exception ex)
        {
            TotalOwedLine = $"What is owed could not be read: {ex.Message}";
        }
    }

    /// <summary>Customers matching the search, or the best customers when it is blank.</summary>
    public ObservableCollection<CustomerSummary> Results { get; } = [];

    /// <summary>Spend by month, oldest first, a zero for a month they did not come in.</summary>
    public ObservableCollection<TrendBar> Months { get; } = [];

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
                    ? "Nobody owes anything on credit."
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
        Status = $"{who} has been forgotten. {unlinked} bill(s) kept, no longer linked to anyone.";
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

        foreach (var name in new[]
                 {
                     nameof(HasSelection), nameof(ShowsHint), nameof(Title), nameof(Mobile), nameof(Points),
                     nameof(Visits), nameof(Spent), nameof(AverageBasket), nameof(FirstVisit),
                     nameof(LastVisit), nameof(SinceLastVisit), nameof(Owes), nameof(OwesAnything),
                     nameof(HasKhata),
                 })
        {
            Raise(name);
        }
    }

    private void FillMonths(CustomerProfile profile)
    {
        var best = profile.Months.Count == 0 ? 0m : profile.Months.Max(m => m.Spent);

        foreach (var month in profile.Months)
        {
            var date = month.Month.ToDateTime(TimeOnly.MinValue);

            Months.Add(new TrendBar(
                date.ToString("MMM", Indian),
                month.Spent,
                best == 0m ? 0 : (double)(month.Spent / best),
                $"{date.ToString("MMMM yyyy", Indian)} - {Money(month.Spent)} over {month.Bills} bill(s)"));
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
                $"{item.Quantity.ToString("0.###", Indian)} {unit} over {item.Bills} bill(s)",
                Money(item.Spent),
                best == 0m ? 0 : (double)(item.Spent / best)));
        }
    }

    private static string Money(decimal amount) => amount.ToString("N2", Indian);

    private static string Day(DateTimeOffset? at) =>
        at is { } value ? value.LocalDateTime.ToString("dd MMM yyyy", Indian) : "-";
}
