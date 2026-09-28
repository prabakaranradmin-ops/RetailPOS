using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Pos.Core.Analytics;

namespace Pos.App.ViewModels;

/// <summary>
/// The owner's GST tab: one month's figures for the return, and saving them for the accountant.
/// </summary>
/// <remarks>
/// Opens on last month, because a return is filed for a month that has finished; the current
/// month can be looked at, but it is still changing. Nothing here writes to the books - it reads
/// them, and writes files only where the owner says.
/// </remarks>
public sealed class GstReturnViewModel : ObservableObject
{
    private static readonly CultureInfo Indian = CultureInfo.GetCultureInfo("en-IN");

    private readonly Func<DateOnly, GstReturnData> _gather;
    private readonly Func<GstReturnData, string, IReadOnlyList<string>> _save;
    private readonly DateOnly _thisMonth;

    private DateOnly _month;
    private GstReturnData? _data;
    private string _status = string.Empty;

    public GstReturnViewModel(
        Func<DateOnly, GstReturnData> gather,
        Func<GstReturnData, string, IReadOnlyList<string>> save,
        DateOnly? today = null)
    {
        _gather = gather ?? throw new ArgumentNullException(nameof(gather));
        _save = save ?? throw new ArgumentNullException(nameof(save));

        var now = today ?? DateOnly.FromDateTime(DateTime.Today);
        _thisMonth = new DateOnly(now.Year, now.Month, 1);
        _month = _thisMonth.AddMonths(-1);
    }

    public ObservableCollection<GstRateRow> RateWise { get; } = [];
    public ObservableCollection<GstLargeInvoiceRow> LargeInterState { get; } = [];
    public ObservableCollection<GstHsnRow> Hsn { get; } = [];
    public ObservableCollection<GstDocumentSeries> Documents { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>The first day of the month on screen.</summary>
    public DateOnly Month => _month;

    public string MonthLabel => _month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>Said when the month is not over, so nobody files from half a month.</summary>
    public string MonthNote => _month == _thisMonth
        ? "This month is not over yet. The figures will change until it is."
        : "A finished month.";

    /// <summary>No month after this one: there is nothing to file for a month that has not started.</summary>
    public bool CanGoLater => _month < _thisMonth;

    public bool IsLoaded => _data is not null;

    public string TaxableValue => Money(_data?.TaxableValue ?? 0m);
    public string Cgst => Money(_data?.Cgst ?? 0m);
    public string Sgst => Money(_data?.Sgst ?? 0m);
    public string Igst => Money(_data?.Igst ?? 0m);
    public string Tax => Money(_data?.Tax ?? 0m);
    public string NilRated => Money(_data?.NilRated ?? 0m);
    public string TaxInvoices => (_data?.TaxInvoices ?? 0).ToString("N0", Indian);

    public bool HasIgst => (_data?.Igst ?? 0m) != 0m;
    public bool HasLargeInterState => LargeInterState.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Nothing was issued in the month at all - said, rather than shown as a page of zeroes.</summary>
    public bool IsEmpty => _data is { HasAnything: false };

    public bool CanSave => _data is { HasAnything: true };

    /// <summary>What the save dialog offers: "gst-T1-2026-09.html".</summary>
    public string SuggestedFileName => _data is null
        ? "gst.html"
        : GstReturnFiles.Stem(_data) + ".html";

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public void EarlierMonth()
    {
        _month = _month.AddMonths(-1);
        Load();
    }

    public void LaterMonth()
    {
        if (!CanGoLater)
            return;

        _month = _month.AddMonths(1);
        Load();
    }

    /// <summary>Reads the month on screen from the books.</summary>
    public void Load()
    {
        try
        {
            _data = _gather(_month);
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            // The owner's screen must not take the till down with it.
            _data = null;
            Status = $"The figures could not be read: {ex.Message}";
        }

        Fill(RateWise, _data?.RateWise);
        Fill(LargeInterState, _data?.LargeInterState);
        Fill(Hsn, _data?.Hsn);
        Fill(Documents, _data?.Documents);
        Fill(Warnings, _data?.Warnings);

        RaiseAll();
    }

    /// <summary>Writes the page and the CSVs.</summary>
    /// <returns>Null when they were written, or why not.</returns>
    public string? Save(string pagePath)
    {
        if (_data is null || !_data.HasAnything)
            return Status = "There is nothing to save for this month.";

        try
        {
            var files = _save(_data, pagePath);

            Status = $"Saved {files.Count} files to {Path.GetDirectoryName(pagePath)}: the page to read, and "
                   + $"{files.Count - 1} CSV files for the accountant. They hold the shop's turnover - keep them private.";

            return null;
        }
        catch (Exception ex)
        {
            return Status = $"Could not save them: {ex.Message}";
        }
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T>? source)
    {
        target.Clear();

        foreach (var item in source ?? [])
            target.Add(item);
    }

    private static string Money(decimal value) => value.ToString("N2", Indian);
}
