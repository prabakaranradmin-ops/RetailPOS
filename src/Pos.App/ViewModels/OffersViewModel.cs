using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using Pos.Core.Domain;
using Pos.Core.Domain.Import;

namespace Pos.App.ViewModels;

/// <summary>One offer as the Catalogue tab lists it.</summary>
/// <param name="State">Whether it is giving anything today, and if not, why not.</param>
/// <param name="Given">What it gave over the last 30 days, or empty.</param>
public sealed record OfferRow(string Name, string What, string When, string State, string Given, bool IsOn);

/// <summary>
/// The shop's offers and schemes, on the Catalogue tab: what runs, what it has given, and the sheet
/// that sets them.
/// </summary>
/// <remarks>
/// The sheet is the list: loading one replaces every offer, so the sheet saved from here is always
/// the offers as they run, and taking a row out ends that offer. The till is given the new list at
/// once, so the next change to a bill is priced by it.
/// </remarks>
public sealed class OffersViewModel : ObservableObject
{
    private static readonly CultureInfo Indian = CultureInfo.GetCultureInfo("en-IN");

    private readonly IOfferStore _offers;
    private readonly Func<IReadOnlySet<string>> _skus;
    private readonly Func<IReadOnlySet<string>> _categories;
    private readonly Action<IReadOnlyList<Offer>>? _loaded;
    private readonly Func<DateTimeOffset> _now;

    private string _status = string.Empty;
    private string _headline = string.Empty;

    /// <param name="loaded">Given the new list after a sheet is loaded - the till takes it from here.</param>
    public OffersViewModel(
        IOfferStore offers,
        Func<IReadOnlySet<string>> skus,
        Func<IReadOnlySet<string>> categories,
        Action<IReadOnlyList<Offer>>? loaded = null,
        Func<DateTimeOffset>? now = null)
    {
        _offers = offers ?? throw new ArgumentNullException(nameof(offers));
        _skus = skus ?? throw new ArgumentNullException(nameof(skus));
        _categories = categories ?? throw new ArgumentNullException(nameof(categories));
        _loaded = loaded;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public ObservableCollection<OfferRow> Rows { get; } = [];

    /// <summary>What is wrong with the offers sheet just loaded, line by line.</summary>
    public ObservableCollection<string> SheetProblems { get; } = [];

    public bool HasSheetProblems => SheetProblems.Count > 0;

    public string Headline
    {
        get => _headline;
        private set => Set(ref _headline, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public void Load()
    {
        Rows.Clear();

        try
        {
            var now = _now();
            var today = DateOnly.FromDateTime(now.DateTime);
            var offers = _offers.All();
            var given = _offers.Given(now.AddDays(-30), now.AddMinutes(1));

            foreach (var offer in offers)
            {
                // A line two offers gave to is named with both; each is credited with the bills.
                var uses = given.Where(u => u.OfferName.Split(" + ").Contains(offer.Name, StringComparer.OrdinalIgnoreCase)).ToList();
                var givenText = uses.Count == 0
                    ? string.Empty
                    : $"{Show.Money(uses.Sum(u => u.Given))} on {Plural.Of(uses.Sum(u => u.Bills), "bill")}";

                var state = offer switch
                {
                    { Sku: not null, ItemId: null } => $"Gives nothing: no item has SKU {offer.Sku}",
                    _ when offer.To is { } to && to < today => $"Ended {to:dd-MM-yyyy}",
                    _ when offer.From is { } from && from > today => $"Starts {from:dd-MM-yyyy}",
                    _ when !offer.IsOn(today) => "Not today",
                    _ => "On today",
                };

                Rows.Add(new OfferRow(offer.Name, offer.Describe(), offer.When(), state, givenText, state == "On today"));
            }

            var on = Rows.Count(r => r.IsOn);

            Headline = Rows.Count == 0
                ? "No offers yet. Save the offers sheet: it has an example of each kind to copy."
                : $"{Plural.Of(Rows.Count, "offer")}, {on} on today. The till works them out on every bill as it is rung up.";
        }
        catch (Exception ex)
        {
            Status = $"The offers could not be read: {ex.Message}";
        }
    }

    // ---- The sheet -------------------------------------------------------------------------------

    public string SuggestedSheetName => $"offers-{_now():yyyy-MM-dd}.csv";

    /// <summary>Writes the offers as a sheet - or, with none yet, a sheet of examples to copy.</summary>
    public string? SaveSheet(string path)
    {
        try
        {
            var offers = _offers.All();
            File.WriteAllText(path, OfferSheet.Write(offers), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Status = offers.Count == 0
                ? $"Saved an offers sheet to {path}, with an example of each kind. Copy a row, take the # off the name, change it, save it as CSV and load it back here."
                : $"Saved {Plural.Of(offers.Count, "offer")} to {path}. Change, add or take out rows, save it as CSV and load it back here: the sheet is the whole list.";
            return null;
        }
        catch (Exception ex)
        {
            return Status = $"Could not save it: {ex.Message}";
        }
    }

    /// <summary>Reads a filled-in sheet and works out the offers it lists. Writes nothing.</summary>
    /// <returns>The plan when the sheet is clean, or null - with why.</returns>
    public OfferSheetPlan? CheckSheet(string path)
    {
        SheetProblems.Clear();
        OfferSheetPlan plan;

        try
        {
            using var reader = ItemCsvParser.OpenText(path);
            plan = OfferSheet.Read(reader, _skus(), _categories());
        }
        catch (Exception ex)
        {
            Status = $"Could not read it: {ex.Message}";
            Raise(nameof(HasSheetProblems));
            return null;
        }

        if (!plan.IsClean)
        {
            foreach (var problem in plan.Problems.Take(50))
                SheetProblems.Add($"Line {problem.Line}, {problem.Column}: {problem.Problem}");

            if (plan.Problems.Count > 50)
                SheetProblems.Add($"... and {plan.Problems.Count - 50} more.");

            Status = plan.Problems.Count == 1
                ? "One thing needs fixing in the sheet. No offer was changed."
                : $"{plan.Problems.Count} things need fixing in the sheet. No offer was changed.";

            Raise(nameof(HasSheetProblems));
            return null;
        }

        Raise(nameof(HasSheetProblems));
        return plan;
    }

    /// <summary>What to ask before loading: the offers it starts, keeps and ends.</summary>
    public string Question(OfferSheetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var current = _offers.All().Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coming = plan.Offers.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ending = current.Where(name => !coming.Contains(name)).ToList();

        var ask = new StringBuilder();
        ask.Append(plan.Offers.Count == 0
            ? "The sheet lists no offers. Loading it ends every offer the shop has."
            : $"Run these {Plural.Of(plan.Offers.Count, "offer")}? {coming.Count(n => !current.Contains(n))} new, {coming.Count(current.Contains)} already running.");

        if (ending.Count > 0)
            ask.Append($"\n\nThese are not on the sheet and will end: {string.Join(", ", ending.Take(10))}{(ending.Count > 10 ? $" and {ending.Count - 10} more" : "")}.");

        if (plan.Warnings.Count > 0)
        {
            ask.Append("\n\nWorth a second look:");

            foreach (var warning in plan.Warnings.Take(10))
                ask.Append("\n- ").Append(warning);
        }

        ask.Append("\n\nBills already paid are not changed. The next bill rung up gets the new offers.");
        return ask.ToString();
    }

    public string? ApplySheet(OfferSheetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        try
        {
            _offers.ReplaceAll(plan.Offers, _now());
            _loaded?.Invoke(_offers.All());
        }
        catch (Exception ex)
        {
            return Status = $"No offer was changed: {ex.Message}";
        }

        Load();
        Status = plan.Offers.Count == 0
            ? "Every offer has ended."
            : $"{Plural.Of(plan.Offers.Count, "offer")} loaded. The till is working them out from the next change to a bill.";
        return null;
    }
}
