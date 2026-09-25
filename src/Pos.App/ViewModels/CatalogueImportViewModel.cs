using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Pos.Core.Domain;
using Pos.Core.Domain.Import;

namespace Pos.App.ViewModels;

/// <summary>One thing wrong with the file, laid out for the grid.</summary>
public sealed record ImportProblemRow(string Where, string Column, string Problem);

/// <summary>
/// Loading a catalogue from the owner's screen, so a shop can stock its shelves without opening a
/// command prompt.
/// </summary>
/// <remarks>
/// None of the validation lives here. <see cref="ItemImporter"/> is the one place that decides what
/// a catalogue may contain, and this screen drives it — a second copy of those rules behind a window
/// would drift from the first and start accepting files the till cannot sell.
///
/// A file is always checked before it can be imported, and the result of that check belongs to the
/// file and the mode it was run against. Change either and the import goes cold again: a clean check
/// of one file is not permission to write another, and insert-only and update give different answers
/// about the same rows.
/// </remarks>
public sealed class CatalogueImportViewModel : ObservableObject
{
    /// <summary>
    /// How many problems reach the grid. A file with thousands wrong has its columns in the wrong
    /// place, and the first screenful says so as well as the whole list would.
    /// </summary>
    public const int MaxShown = 200;

    private static readonly CultureInfo Indian = CultureInfo.GetCultureInfo("en-IN");

    private readonly IItemStore _items;
    private readonly Func<string, TextReader> _open;

    private string _filePath = string.Empty;
    private bool _updateExisting;
    private bool _busy;
    private string _verdict = string.Empty;
    private string _held = string.Empty;

    /// <summary>
    /// Set by a clean check, and cleared by anything that could make that check stale — a different
    /// file, a different mode, or the import it authorised having been done.
    /// </summary>
    private bool _checkPassed;

    public CatalogueImportViewModel(IItemStore items, Func<string, TextReader>? open = null)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));

        // Overridable so the screen's own behaviour can be tested against a file that never exists
        // on disk. Production always reads a real one, byte for byte as the `pos` tool would.
        _open = open ?? ItemCsvParser.OpenText;

        RefreshHeld();
    }

    /// <summary>Raised once a catalogue has actually landed, so the screen around this can re-read.</summary>
    public event EventHandler? Imported;

    public ObservableCollection<ImportProblemRow> Problems { get; } = [];

    /// <summary>The file to load. Choosing another one withdraws the last check.</summary>
    public string FilePath
    {
        get => _filePath;
        set
        {
            if (Set(ref _filePath, value ?? string.Empty))
                Invalidate();
        }
    }

    /// <summary>
    /// Whether a SKU already in the catalogue is a price revision or a mistake. A first load wants
    /// this off, so a duplicate is caught; a re-import is nearly always a price change and wants it
    /// on.
    /// </summary>
    public bool UpdateExisting
    {
        get => _updateExisting;
        set
        {
            if (Set(ref _updateExisting, value))
                Invalidate();
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                Raise(nameof(CanCheck));
                Raise(nameof(CanImport));
            }
        }
    }

    /// <summary>What the last check or import found, in words a shopkeeper can act on.</summary>
    public string Verdict
    {
        get => _verdict;
        private set => Set(ref _verdict, value);
    }

    /// <summary>What the catalogue holds right now, so the screen says what it is changing.</summary>
    public string Held
    {
        get => _held;
        private set => Set(ref _held, value);
    }

    /// <summary>Every problem the file has, which may be more than the grid is showing.</summary>
    public int ProblemCount { get; private set; }

    public bool HasFile => _filePath.Trim().Length > 0;

    public bool CanCheck => HasFile && !IsBusy;

    public bool CanImport => _checkPassed && !IsBusy;

    /// <summary>
    /// Reads the file and reports what would happen, writing nothing.
    /// </summary>
    /// <remarks>
    /// Not optional, the way <c>--dry-run</c> is on the command line. The whole reason a bad
    /// catalogue is survivable is that somebody saw the problems before the prices reached the
    /// shelf, and a step people can skip is one they skip on the busy day it matters.
    /// </remarks>
    public void Check()
    {
        if (!HasFile)
        {
            Verdict = "Choose the catalogue file first.";
            return;
        }

        if (Run(dryRun: true) is not { } result)
            return;

        Show(result.Problems);

        if (result.IsClean && result.RowsRead == 0)
        {
            // A header and nothing under it breaks no rule, so the importer calls it clean. It is
            // still not what anybody meant by loading a catalogue, and importing it would report a
            // confident nought.
            _checkPassed = false;
            Verdict = "The file has a header but no item rows under it.";
        }
        else if (result.IsClean)
        {
            _checkPassed = true;

            Verdict = $"{Count(result.RowsRead)} row(s) read and nothing wrong. "
                      + $"Importing would add {Count(result.Inserted)} and change {Count(result.Updated)}. "
                      + "Nothing has been written yet.";
        }
        else
        {
            _checkPassed = false;

            Verdict = $"{Count(ProblemCount)} problem(s). Nothing has been imported — "
                      + "fix these in the spreadsheet, save it as CSV again, and check it once more.";
        }

        Raise(nameof(CanImport));
    }

    /// <summary>Writes the catalogue that the last check passed.</summary>
    public void Import()
    {
        if (!CanImport)
        {
            Verdict = "Check the file first. Nothing is imported until a check comes back clean.";
            return;
        }

        var before = Held;

        if (Run(dryRun: false) is not { } result)
            return;

        if (!result.Committed)
        {
            // The check passed and the write did not, so the file moved underneath us between the
            // two. Nothing was written — the importer is all or nothing — but the shopkeeper is
            // owed the reason rather than a button that did nothing.
            _checkPassed = false;
            Show(result.Problems);

            Verdict = $"The file changed since it was checked, and now has {Count(ProblemCount)} problem(s). "
                      + "Nothing was imported. Check it again.";

            Raise(nameof(CanImport));
            return;
        }

        Problems.Clear();
        ProblemCount = 0;
        Raise(nameof(ProblemCount));

        // One import per check. Without this, a second press would run the same file again — a
        // no-op in update mode, and a screenful of duplicate-SKU errors in insert mode, neither of
        // which is what the press meant.
        _checkPassed = false;
        Raise(nameof(CanImport));

        RefreshHeld();

        Verdict = $"Done. {Count(result.Inserted)} item(s) added, {Count(result.Updated)} changed. "
                  + $"The catalogue held {before} and now holds {Held}. The till can sell them straight away.";

        Imported?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Runs the importer, turning anything that stops it into a message on this screen.
    /// </summary>
    /// <remarks>
    /// A file that is open in Excel, a path that has gone, a disk that will not read — none of them
    /// may take the till down. The owner's screen reports what happened and the counter carries on
    /// selling either way.
    /// </remarks>
    private ImportResult? Run(bool dryRun)
    {
        IsBusy = true;

        try
        {
            using var reader = _open(_filePath.Trim());
            return new ItemImporter(_items).Import(reader, _updateExisting, dryRun);
        }
        catch (Exception ex)
        {
            _checkPassed = false;

            Problems.Clear();
            ProblemCount = 0;
            Raise(nameof(ProblemCount));
            Raise(nameof(CanImport));

            Verdict = $"The file could not be read: {ex.Message}"
                      + (dryRun ? string.Empty : " Nothing was imported.");

            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(IReadOnlyList<ImportProblem> problems)
    {
        ProblemCount = problems.Count;
        Problems.Clear();

        foreach (var problem in problems.Take(MaxShown))
        {
            Problems.Add(new ImportProblemRow(
                $"line {problem.Line}",
                problem.Column,
                problem.Problem));
        }

        Raise(nameof(ProblemCount));
    }

    /// <summary>Withdraws the last check, because what it was run against has changed.</summary>
    private void Invalidate()
    {
        if (_checkPassed)
        {
            _checkPassed = false;
            Verdict = "Check the file again — what you are importing has changed since the last check.";
        }

        Raise(nameof(HasFile));
        Raise(nameof(CanCheck));
        Raise(nameof(CanImport));
    }

    private void RefreshHeld()
    {
        try
        {
            Held = $"{Count(_items.Count())} item(s)";
        }
        catch (Exception ex)
        {
            Held = $"could not be read: {ex.Message}";
        }
    }

    private static string Count(int value) => value.ToString("N0", Indian);
}
