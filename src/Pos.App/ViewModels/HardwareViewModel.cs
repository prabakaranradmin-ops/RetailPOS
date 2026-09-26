using System.Collections.ObjectModel;
using System.IO;
using Pos.Core.Configuration;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;
using Pos.Core.Hardware.Windows;

namespace Pos.App.ViewModels;

/// <summary>
/// The peripheral checks and the bill preview, driven from the owner's screen instead of a command
/// prompt.
/// </summary>
/// <remarks>
/// The checks themselves are <see cref="PeripheralCheck"/>, unchanged and shared with the
/// <c>pos</c> tool. This only supplies the two things a window does differently: progress arrives
/// as a list rather than a stream of console lines, and a question that only a person can answer
/// becomes a dialog rather than a y/N prompt. A lane signed off from here is the same lane the
/// hardware sign-off sheet describes.
///
/// Every check runs off the UI thread. They print, fire drawers and sit reading a serial port for
/// ten seconds; on the dispatcher thread that would freeze the till's own window while a queue
/// waited.
/// </remarks>
public sealed class HardwareViewModel : ObservableObject
{
    private readonly PosSettings _settings;
    private readonly ITextRasterizer? _rasterizer;
    private readonly Func<string, bool> _confirm;
    private readonly Action<Action> _post;

    private bool _busy;
    private string _previewText = string.Empty;
    private string? _previewImagePath;
    private string _scannedCode = string.Empty;
    private string _summary = string.Empty;
    private Pane _pane = Pane.Log;

    /// <param name="confirm">
    /// Asks the operator something only they can see — did paper come out, did the drawer open.
    /// Called from a background thread, so an implementation that shows a dialog has to marshal.
    /// </param>
    /// <param name="post">Runs an action on the UI thread. Tests pass one that runs it directly.</param>
    public HardwareViewModel(
        PosSettings settings,
        ITextRasterizer? rasterizer,
        Func<string, bool> confirm,
        Action<Action> post)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _rasterizer = rasterizer;
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    /// <summary>Progress from the running check, newest last.</summary>
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>The bill this lane would print, as characters.</summary>
    public string PreviewText
    {
        get => _previewText;
        private set => Set(ref _previewText, value);
    }

    /// <summary>Where the rendered dots were written, once they have been.</summary>
    public string? PreviewImagePath
    {
        get => _previewImagePath;
        private set => Set(ref _previewImagePath, value);
    }

    /// <summary>
    /// Which of the three things the panel is showing: a running check, the composed bill, or the
    /// dots themselves. One at a time, because they answer different questions and stacking them
    /// would leave the operator reading last week's log under this minute's receipt.
    /// </summary>
    public bool ShowsLog => _pane == Pane.Log;

    public bool ShowsPreviewText => _pane == Pane.Text;

    public bool ShowsPreviewImage => _pane == Pane.Image;

    private enum Pane
    {
        Log,
        Text,
        Image,
    }

    private void Show(Pane pane)
    {
        _pane = pane;

        Raise(nameof(ShowsLog));
        Raise(nameof(ShowsPreviewText));
        Raise(nameof(ShowsPreviewImage));
    }

    /// <summary>Where a keyboard-wedge scanner's read lands, because it types into whatever has focus.</summary>
    public string ScannedCode
    {
        get => _scannedCode;
        set => Set(ref _scannedCode, value);
    }

    /// <summary>True when this lane's scanner types rather than opening a port.</summary>
    public bool ScannerTypesLikeAKeyboard => Check(_ => { }).ScannerTypesLikeAKeyboard;

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
                Raise(nameof(CanRun));
        }
    }

    public bool CanRun => !IsBusy;

    /// <summary>How the last check came out, in a line.</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public Task CheckPrinter() => Run("Printer", check => check.Printer());

    public Task CheckDrawer() => Run("Cash drawer", check => check.Drawer());

    public Task CheckScale() => Run("Scale", check => check.Scale(TimeSpan.FromSeconds(10)));

    public Task CheckScanner()
    {
        // Captured before the background thread starts: the box belongs to the window, and reading
        // it from anywhere else is a cross-thread access waiting to happen.
        var typed = ScannedCode.Trim();

        return Run("Scanner", check => check.Scanner(TimeSpan.FromSeconds(10), typed.Length == 0 ? null : typed));
    }

    public Task ListPorts() => Run("Serial ports", check =>
    {
        check.ListPorts();

        // Listing is not a pass or a fail. It is the first thing to look at when a port is wrong,
        // and calling an empty machine "failed" would be saying something this does not know.
        return (CheckResult?)null;
    });

    /// <summary>
    /// Composes the bill this lane would print and shows it, touching no hardware at all.
    /// </summary>
    /// <param name="paperWidthChars">
    /// Overrides the lane's own width, for checking a layout against 58mm paper before buying it.
    /// </param>
    public void ShowPreview(int? paperWidthChars = null)
    {
        try
        {
            PreviewText = Check(_ => { }).PreviewText(paperWidthChars);
            Show(Pane.Text);
            Summary = $"The bill as this lane would print it, at {paperWidthChars ?? _settings.Hardware.PrinterPaperWidthChars} characters wide.";
        }
        catch (Exception ex)
        {
            Summary = $"The preview could not be composed: {ex.Message}";
        }
    }

    /// <summary>
    /// Renders the dots the printer would actually burn and writes them to a PNG.
    /// </summary>
    /// <remarks>
    /// This is the only way to check a Tamil bill without a roll of paper. The text preview counts
    /// characters, which says nothing about a script that is drawn rather than typed — a lane whose
    /// Tamil will come out as '?' looks perfectly fine in text.
    /// </remarks>
    /// <param name="into">Folder to write into.</param>
    public void RenderPreviewImage(string into)
    {
        try
        {
            if (Raster() is not { } raster)
            {
                Summary = "Nothing to draw: this lane has no text renderer, or drawing is switched off.";
                PreviewImagePath = null;
                return;
            }

            Directory.CreateDirectory(into);

            // A new name each time. Windows holds a lock on an image a control is showing, so
            // writing over the one on screen fails on the second press.
            var path = Path.Combine(into, $"receipt-preview-{DateTime.Now:yyyyMMdd-HHmmss}.png");

            var pixels = Check(_ => { }).Preview().ToBitmap(raster);
            ReceiptImage.SavePng(pixels, path);

            PreviewImagePath = path;
            Show(Pane.Image);
            Summary = $"Rendered {pixels.Width}x{pixels.Height} dots — this is what the paper will look like.";
        }
        catch (Exception ex)
        {
            Summary = $"The bill could not be drawn: {ex.Message}";
            PreviewImagePath = null;
        }
    }

    private RasterOptions? Raster() =>
        _rasterizer is null || _settings.Hardware.PrinterRasterMode == RasterMode.Never
            ? null
            : new RasterOptions(_rasterizer, _settings.Hardware.EffectivePaperWidthDots, _settings.Hardware.PrinterRasterMode);

    private PeripheralCheck Check(Action<string> report) =>
        new(_settings, report, _confirm, _rasterizer);

    /// <summary>
    /// Runs one check off the UI thread, reporting as it goes.
    /// </summary>
    /// <remarks>
    /// Nothing here is allowed to throw out. A serial port that will not open, a printer that has
    /// gone, a driver that faults — each becomes a line in the log and a summary, because the
    /// owner's screen sits on top of a till that has to carry on selling either way.
    /// </remarks>
    private async Task Run(string what, Func<PeripheralCheck, CheckResult?> check)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        Log.Clear();
        Show(Pane.Log);
        Summary = $"{what}: running…";

        try
        {
            var result = await Task.Run(() =>
            {
                try
                {
                    return check(Check(line => _post(() => Log.Add(line))));
                }
                catch (Exception ex)
                {
                    _post(() => Log.Add($"FAILED: {ex.Message}"));
                    return CheckResult.Failed;
                }
            });

            Summary = result switch
            {
                CheckResult.Passed => $"{what}: passed.",
                CheckResult.Failed => $"{what}: FAILED. The lane is not ready until this passes.",
                CheckResult.NotConfigured => $"{what}: nothing set up for this lane, so there was nothing to test.",
                _ => $"{what}: done.",
            };
        }
        finally
        {
            IsBusy = false;
        }
    }
}
