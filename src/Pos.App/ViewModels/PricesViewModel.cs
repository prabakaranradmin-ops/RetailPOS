using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Pos.Core.Analytics;
using Pos.Core.Domain;
using Pos.Core.Domain.Import;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;

namespace Pos.App.ViewModels;

/// <summary>
/// Prices in bulk and the shelf labels they put out of date, on the Catalogue tab.
/// </summary>
/// <remarks>
/// A price changed on the till's books is not a price changed on the shelf, and the customer reads
/// the shelf. So every change - from the price sheet, a catalogue re-import, a new item - puts the
/// item on the list of labels due, and it stays there until its label has been printed.
/// </remarks>
public sealed class PricesViewModel : ObservableObject
{
    private readonly IPriceStore _prices;
    private readonly Func<IReadOnlyList<ShelfLabel>, PrintOutcome> _print;
    private readonly string _shopName;
    private readonly Func<DateTimeOffset> _now;

    private bool _everyItem;
    private string _status = string.Empty;

    /// <param name="print">Sends labels to the till's printer.</param>
    public PricesViewModel(
        IPriceStore prices,
        Func<IReadOnlyList<ShelfLabel>, PrintOutcome> print,
        string shopName,
        Func<DateTimeOffset>? now = null)
    {
        _prices = prices ?? throw new ArgumentNullException(nameof(prices));
        _print = print ?? throw new ArgumentNullException(nameof(print));
        _shopName = string.IsNullOrWhiteSpace(shopName) ? "the shop" : shopName.Trim();
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>The labels that would print: those due, or every item.</summary>
    public ObservableCollection<ShelfLabel> Labels { get; } = [];

    /// <summary>What is wrong with the price sheet just loaded, line by line.</summary>
    public ObservableCollection<string> SheetProblems { get; } = [];

    public bool HasSheetProblems => SheetProblems.Count > 0;

    /// <summary>A label for every item rather than only the ones whose price changed.</summary>
    public bool EveryItem
    {
        get => _everyItem;
        set
        {
            if (Set(ref _everyItem, value))
                LoadLabels();
        }
    }

    public string LabelsHeadline => _everyItem
        ? $"{Plural.Of(Labels.Count, "item")} - a label for everything the shop sells."
        : Labels.Count == 0
            ? "Every shelf label is up to date: no price has changed since the labels were printed."
            : $"{Plural.Of(Labels.Count, "label")} due: a price changed, or the item is new, since its label was printed.";

    public bool CanPrint => Labels.Count > 0;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public void LoadLabels()
    {
        try
        {
            var labels = _everyItem ? _prices.AllLabels() : _prices.LabelsDue();

            Labels.Clear();

            foreach (var label in labels)
                Labels.Add(label);
        }
        catch (Exception ex)
        {
            Status = $"The labels could not be read: {ex.Message}";
        }

        Raise(nameof(LabelsHeadline));
        Raise(nameof(CanPrint));
    }

    // ---- The price sheet -------------------------------------------------------------------------

    public string SuggestedSheetName => $"price-sheet-{_now():yyyy-MM-dd}.csv";

    /// <summary>Writes a price sheet of every active item, to fill in with new prices.</summary>
    public string? SaveSheet(string path)
    {
        try
        {
            var items = _prices.PriceSheet();
            File.WriteAllText(path, PriceSheet.Write(items), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Status = $"Saved a price sheet of {Plural.Of(items.Count, "item")} to {path}. Fill in new_mrp or new_selling_price where a price changes, save it as CSV, and load it back here.";
            return null;
        }
        catch (Exception ex)
        {
            return Status = $"Could not save it: {ex.Message}";
        }
    }

    /// <summary>Reads a filled-in sheet and works out what it would change. Writes nothing.</summary>
    /// <returns>The plan when the sheet is clean and changes something, or null - with why.</returns>
    public PriceSheetPlan? CheckSheet(string path)
    {
        SheetProblems.Clear();
        PriceSheetPlan plan;

        try
        {
            using var reader = ItemCsvParser.OpenText(path);
            plan = PriceSheet.Read(reader, _prices.PriceSheet());
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
                ? "One thing needs fixing in the sheet. No price was changed."
                : $"{plan.Problems.Count} things need fixing in the sheet. No price was changed.";

            Raise(nameof(HasSheetProblems));
            return null;
        }

        Raise(nameof(HasSheetProblems));

        if (plan.Changes.Count == 0)
        {
            Status = "Nothing in that sheet changes a price. Fill in new_mrp or new_selling_price.";
            return null;
        }

        return plan;
    }

    /// <summary>What to ask before loading: how many prices, and anything worth a second look.</summary>
    public static string Question(PriceSheetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var ask = new StringBuilder();
        ask.Append($"Change the price of {Plural.Of(plan.Changes.Count, "item")}?\n\n");

        if (SheetWords.LeftAlone(plan.Blank, plan.Unchanged, "price") is { Length: > 0 } leftAlone)
            ask.Append(leftAlone).Append(' ');

        ask.Append("Nothing else about the items changes, and each changed item goes on the list of shelf labels due.");

        if (plan.Warnings.Count > 0)
        {
            ask.Append("\n\nWorth a second look:");

            foreach (var warning in plan.Warnings.Take(10))
                ask.Append("\n- ").Append(warning);

            if (plan.Warnings.Count > 10)
                ask.Append($"\n- ... and {plan.Warnings.Count - 10} more.");
        }

        return ask.ToString();
    }

    public string? ApplySheet(PriceSheetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        try
        {
            var changed = _prices.Apply(plan.Changes);
            LoadLabels();

            Status = changed == 1
                ? "1 price changed from the sheet. Its shelf label is due - print it below."
                : $"{Plural.Of(changed, "price")} changed from the sheet. Their shelf labels are due - print them below.";
            return null;
        }
        catch (Exception ex)
        {
            return Status = $"No price was changed: {ex.Message}";
        }
    }

    // ---- Labels ----------------------------------------------------------------------------------

    public string SuggestedLabelsName => $"shelf-labels-{_now():yyyy-MM-dd}.html";

    /// <summary>Prints the labels on the till's printer, and marks them done once they have printed.</summary>
    public string? Print()
    {
        if (Labels.Count == 0)
            return Status = "There are no labels to print.";

        var labels = Labels.ToList();
        PrintOutcome outcome;

        try
        {
            outcome = _print(labels);
        }
        catch (Exception ex)
        {
            return Status = $"The labels did not print: {ex.Message}";
        }

        if (outcome.Status == PrintStatus.NoPrinterConfigured)
            return Status = "This lane has no printer. Save the labels as a page instead, and print that.";

        if (!outcome.Succeeded)
            return Status = $"The labels did not print: {outcome.Detail}. They are still due.";

        return Done(labels, $"Printed {Plural.Of(labels.Count, "label")}. Cut them apart and put them on the shelf.");
    }

    /// <summary>Saves the labels as an A4 page, and marks them done.</summary>
    public string? SavePage(string path)
    {
        if (Labels.Count == 0)
            return Status = "There are no labels to save.";

        var labels = Labels.ToList();

        try
        {
            File.WriteAllText(path, ShelfLabelPage.Render(labels, _shopName), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex)
        {
            return Status = $"Could not save it: {ex.Message}";
        }

        return Done(labels, $"Saved {Plural.Of(labels.Count, "label")} to {path}. Open it and print it on A4 at actual size.");
    }

    private string? Done(IReadOnlyList<ShelfLabel> labels, string said)
    {
        try
        {
            _prices.MarkLabelled(labels.Select(l => l.ItemId), _now());
        }
        catch (Exception ex)
        {
            return Status = $"{said} They could not be marked as done, and will be listed again: {ex.Message}";
        }

        LoadLabels();
        Status = said;
        return null;
    }
}
