using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.Core.Hardware.Scanning;
using Pos.Core.Hardware.Serial;
using Pos.Core.Hardware.Weighing;

namespace Pos.Core.Configuration;

public enum CheckResult
{
    Passed,
    Failed,

    /// <summary>Nothing configured for this peripheral, so there was nothing to test.</summary>
    NotConfigured,
}

/// <summary>
/// The peripheral checks: print a test bill, fire the drawer, read a barcode, read the scale.
/// </summary>
/// <remarks>
/// These cannot be automated. Nothing in software can tell whether paper actually came out of the
/// printer or whether the drawer physically opened, so every check that ends in a physical outcome
/// asks the operator and records the answer.
///
/// How it asks is left to the caller. The command-line tool prints a question and reads y/N; the
/// owner's screen puts up a dialog. Both drive this one implementation, so a check run from a
/// window is the same check the hardware sign-off sheet asks for — a second copy behind the buttons
/// would drift, and the lane that passed would not be the lane that was tested.
/// </remarks>
/// <param name="report">Called with each line of progress, in the order it happens.</param>
/// <param name="confirm">
/// Asks the operator a yes/no question about something only they can see. Answering no fails the
/// check, which is the point: a "yes" that should have been "no" is a fault discovered mid-queue
/// instead.
/// </param>
public sealed class PeripheralCheck(
    PosSettings settings,
    Action<string> report,
    Func<string, bool> confirm,
    ITextRasterizer? rasterizer = null)
{
    private readonly PosSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly Action<string> _report = report ?? throw new ArgumentNullException(nameof(report));
    private readonly Func<string, bool> _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));

    /// <summary>
    /// Built the same way the till builds it, rasteriser included. A check that printed through a
    /// different path from the one a sale uses would be checking the wrong thing — on a Tamil lane
    /// it would put '?' on the test page and pass anyway.
    /// </summary>
    private IPrinterService CreatePrinter() => PeripheralFactory.CreatePrinter(_settings.Hardware, rasterizer);

    /// <summary>
    /// True when this lane's scanner types into whatever has focus rather than opening a port.
    /// </summary>
    /// <remarks>
    /// The caller needs to know because it owns the box the scan lands in — a console reads a line,
    /// a window puts the caret in a text field. Either way the code is handed back to
    /// <see cref="Scanner"/>, which is what decides whether it was a good read.
    /// </remarks>
    public bool ScannerTypesLikeAKeyboard
    {
        get
        {
            using var scanner = PeripheralFactory.CreateScanner(_settings.Hardware);
            return scanner is KeyboardWedgeScannerService;
        }
    }

    /// <summary>The bill this lane would print, as text. No hardware needed.</summary>
    public string PreviewText(int? paperWidthChars = null) => Preview(paperWidthChars).ToPlainText();

    /// <summary>The bill this lane would print, as the bytes that would reach the printer.</summary>
    public byte[] PreviewEscPos(int? paperWidthChars = null)
    {
        var printer = CreatePrinter();
        return Preview(paperWidthChars).ToEscPos(raster: printer.Raster);
    }

    /// <summary>
    /// The sample bill composed exactly as this lane would compose a real one — its store details,
    /// its paper width, its language, its tax mode and its rounding.
    /// </summary>
    public ReceiptBuilder Preview(int? paperWidthChars = null)
    {
        var width = paperWidthChars ?? _settings.Hardware.PrinterPaperWidthChars;

        return new ReceiptComposer(_settings.Store.ToProfile(), width, _settings.ReceiptLanguage)
            .Compose(SampleInvoice.Build(
                _settings.LaneId,
                _settings.InvoiceNumber.ToFormat(),
                _settings.TaxMode,
                _settings.RoundOffToRupee));
    }

    public CheckResult Printer()
    {
        Heading("Printer");

        var printer = CreatePrinter();
        _report($"Configured as : {printer.Name}");
        _report($"Paper width   : {printer.PaperWidthChars} characters");

        if (!printer.IsConfigured)
        {
            _report("No printer is set up for this lane.");
            return CheckResult.NotConfigured;
        }

        var receipt = Preview(printer.PaperWidthChars);

        // Shown before it is sent, never after. The operator is about to be asked whether what came
        // out is right, and they cannot answer that against a memory of what they expected.
        _report(string.Empty);
        _report("This is what should come out:");
        _report(string.Empty);

        foreach (var line in receipt.ToPlainText().Split(Environment.NewLine))
            _report("  | " + line);

        _report(string.Empty);

        var job = receipt.ToEscPos(raster: printer.Raster);

        if (_settings.ReceiptLanguage != ReceiptLanguage.English && printer.Raster is null)
            _report("WARNING: this lane prints Tamil labels but has no text renderer. They will print as '?'.");

        _report($"Job size      : {job.Length:N0} bytes");

        if (!_confirm("Send a test bill to the printer?"))
            return CheckResult.NotConfigured;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var outcome = printer.Print(job);
        clock.Stop();

        if (!outcome.Succeeded)
        {
            _report($"FAILED: {outcome.Detail}");
            return CheckResult.Failed;
        }

        ReportPrintSpeed(outcome.BytesWritten, clock.Elapsed);

        return _confirm("Did the receipt print, and does it match what is on screen?")
            ? CheckResult.Passed
            : CheckResult.Failed;
    }

    /// <summary>
    /// How long the job took to hand over, and what that works out to per second.
    /// </summary>
    /// <remarks>
    /// This matters because a drawn receipt is not the same size as a typed one. A Tamil bill on
    /// 80mm paper is around 27KB against 2KB for the same bill in English, and on a printer
    /// attached over a 9600-baud serial line 27KB is roughly half a minute — unusable at a counter.
    /// Over USB it is imperceptible. Nobody can tell which they have without measuring it.
    /// <para>
    /// The figure is a lower bound, and says so. The spooler returns once it has accepted the job,
    /// not once the paper has stopped moving, so the operator still has to watch the printer. What
    /// this catches is the case where even the handover is slow, which means the wire is.
    /// </para>
    /// </remarks>
    private void ReportPrintSpeed(int bytes, TimeSpan elapsed)
    {
        _report($"Sent {bytes:N0} bytes in {elapsed.TotalMilliseconds:N0} ms.");

        if (elapsed.TotalSeconds > 0.01)
            _report($"Handover rate : {bytes / elapsed.TotalSeconds / 1024:N0} KB/s");

        _report("That is the time to hand the job to the spooler, not the time until the paper");
        _report("stops. Time the paper yourself as well — see HARDWARE_SIGNOFF.");

        if (elapsed.TotalSeconds >= 3)
        {
            _report(string.Empty);
            _report("SLOW: this took over three seconds before the paper even started. A queue will");
            _report("feel that on every sale. If the printer is on a serial port, that is the cause;");
            _report("a drawn receipt is ten times the data of a typed one. Consider USB, or English.");
        }
    }

    public CheckResult Drawer()
    {
        Heading("Cash drawer");

        var printer = CreatePrinter();
        var drawer = PeripheralFactory.CreateDrawer(_settings.Hardware, printer);

        _report($"Configured as : {drawer.Name}");

        if (!drawer.IsConfigured)
        {
            _report("No drawer is set up for this lane.");
            return CheckResult.NotConfigured;
        }

        if (!_confirm("Send the kick pulse to the drawer?"))
            return CheckResult.NotConfigured;

        var result = drawer.Kick();
        _report($"Pulse result  : {result}");

        if (result != DrawerKickResult.Opened)
            return CheckResult.Failed;

        return _confirm("Did the drawer open?") ? CheckResult.Passed : CheckResult.Failed;
    }

    /// <param name="typed">
    /// What a keyboard-wedge scanner typed, when the caller has already collected it. A wedge types
    /// into whatever has focus, so the caller owns the box it lands in.
    /// </param>
    public CheckResult Scanner(TimeSpan window, string? typed = null)
    {
        Heading("Barcode scanner");

        using var scanner = PeripheralFactory.CreateScanner(_settings.Hardware);
        _report($"Configured as : {scanner.Name}");

        if (scanner is KeyboardWedgeScannerService wedge)
        {
            if (string.IsNullOrWhiteSpace(typed))
            {
                _report("This scanner types like a keyboard. Scan into the box and run this again.");
                return CheckResult.NotConfigured;
            }

            ScannedBarcode? captured = null;
            wedge.BarcodeScanned += (_, code) => captured = code;
            wedge.Accept(typed);

            return Report(captured);
        }

        var reads = new List<ScannedBarcode>();
        scanner.BarcodeScanned += (_, code) => reads.Add(code);

        try
        {
            scanner.Start();
        }
        catch (Exception ex)
        {
            _report($"FAILED to open the port: {ex.Message}");
            return CheckResult.Failed;
        }

        _report($"Listening for {window.TotalSeconds:0} seconds. Scan something.");
        Thread.Sleep(window);
        scanner.Stop();

        if (reads.Count == 0)
        {
            _report("Nothing was scanned.");
            return CheckResult.Failed;
        }

        foreach (var read in reads)
            Report(read);

        return reads.All(r => r.CheckDigitValid) ? CheckResult.Passed : CheckResult.Failed;
    }

    public CheckResult Scale(TimeSpan window)
    {
        Heading("Weighing scale");

        using var scale = PeripheralFactory.CreateScale(_settings.Hardware);
        _report($"Configured as : {scale.Name}");

        if (!scale.IsConfigured)
        {
            _report("No scale is set up for this lane.");
            return CheckResult.NotConfigured;
        }

        try
        {
            scale.Start();
        }
        catch (Exception ex)
        {
            _report($"FAILED to open the port: {ex.Message}");
            return CheckResult.Failed;
        }

        var readings = 0;
        var sawStable = false;

        scale.WeightChanged += (_, reading) =>
        {
            readings++;
            sawStable |= reading.Stability == WeightStability.Stable;
        };

        _report($"Reading for {window.TotalSeconds:0} seconds. Put something on the pan.");

        var deadline = DateTimeOffset.UtcNow + window;

        while (DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(500);
            var current = scale.Current;
            _report($"  {current.Stability,-8}  gross {current.Gross,8:0.000} kg   net {current.Net,8:0.000} kg");
        }

        scale.Stop();

        _report($"Frames parsed : {readings}");

        if (readings == 0)
        {
            _report("Nothing arrived. Check the port, the baud rate, and that the scale is set to stream.");
            return CheckResult.Failed;
        }

        if (!sawStable)
        {
            _report("Frames arrived but none were stable. Only a settled reading may be billed.");
            return CheckResult.Failed;
        }

        return CheckResult.Passed;
    }

    /// <summary>Lists what the machine can see, which is the first thing to check when a port is wrong.</summary>
    public IReadOnlyList<string> ListPorts()
    {
        Heading("Serial ports");

        var ports = SystemSerialPort.AvailablePorts();

        if (ports.Count == 0)
            _report("None found.");

        foreach (var port in ports)
            _report(port);

        return ports;
    }

    private CheckResult Report(ScannedBarcode? read)
    {
        if (read is not { } code)
            return CheckResult.Failed;

        _report($"Read          : {code.Code}");
        _report($"Symbology     : {code.Symbology}");
        _report($"Check digit   : {(code.CheckDigitValid ? "valid" : "INVALID — this is a misread")}");

        return code.CheckDigitValid ? CheckResult.Passed : CheckResult.Failed;
    }

    private void Heading(string title)
    {
        _report(string.Empty);
        _report(title);
        _report(new string('-', Math.Max(title.Length, 20)));
    }
}
