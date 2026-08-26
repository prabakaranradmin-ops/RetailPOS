using Pos.Core.Configuration;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.Core.Hardware.Scanning;
using Pos.Core.Hardware.Serial;
using Pos.Core.Hardware.Weighing;

namespace Pos.Diagnostics;

internal enum CheckResult
{
    Passed,
    Failed,

    /// <summary>Nothing configured for this peripheral, so there was nothing to test.</summary>
    NotConfigured,

    /// <summary>Needs a person to confirm what happened — paper came out, the drawer opened.</summary>
    NeedsHumanConfirmation,
}

/// <summary>
/// The interactive peripheral checks behind <c>pos test-hardware</c>.
/// </summary>
/// <remarks>
/// These are what the Phase 3 hardware-in-the-loop gate is run through. They cannot be automated:
/// nothing in software can tell whether paper actually came out of the printer or whether the
/// drawer physically opened, so the checks that end in a physical outcome ask the operator and
/// record the answer.
/// </remarks>
internal sealed class HardwareChecks(PosSettings settings, TextWriter output, TextReader input)
{
    private readonly PosSettings _settings = settings;

    public CheckResult Printer()
    {
        Heading("Printer");

        var printer = PeripheralFactory.CreatePrinter(_settings.Hardware);
        output.WriteLine($"  Configured as : {printer.Name}");
        output.WriteLine($"  Paper width   : {printer.PaperWidthChars} characters");

        if (!printer.IsConfigured)
        {
            output.WriteLine("  No printer is set up for this lane.");
            return CheckResult.NotConfigured;
        }

        var receipt = new ReceiptComposer(_settings.Store.ToProfile(), printer.PaperWidthChars, _settings.ReceiptLanguage)
            .Compose(SampleInvoice.Build(_settings.LaneId, _settings.InvoiceNumber.ToFormat()));

        output.WriteLine();
        output.WriteLine("  This is what should come out:");
        output.WriteLine();
        WriteIndented(receipt.ToPlainText());

        if (!Confirm("Send this to the printer?"))
            return CheckResult.NotConfigured;

        var outcome = printer.Print(receipt.ToEscPos(raster: printer.Raster));

        if (!outcome.Succeeded)
        {
            output.WriteLine($"  FAILED: {outcome.Detail}");
            return CheckResult.Failed;
        }

        output.WriteLine($"  Sent {outcome.BytesWritten} bytes.");

        return Confirm("Did the receipt print, and does it match?")
            ? CheckResult.Passed
            : CheckResult.Failed;
    }

    public CheckResult Drawer()
    {
        Heading("Cash drawer");

        var printer = PeripheralFactory.CreatePrinter(_settings.Hardware);
        var drawer = PeripheralFactory.CreateDrawer(_settings.Hardware, printer);

        output.WriteLine($"  Configured as : {drawer.Name}");

        if (!drawer.IsConfigured)
        {
            output.WriteLine("  No drawer is set up for this lane.");
            return CheckResult.NotConfigured;
        }

        if (!Confirm("Send the kick pulse?"))
            return CheckResult.NotConfigured;

        var result = drawer.Kick();
        output.WriteLine($"  Pulse result  : {result}");

        if (result != DrawerKickResult.Opened)
            return CheckResult.Failed;

        return Confirm("Did the drawer open?") ? CheckResult.Passed : CheckResult.Failed;
    }

    public CheckResult Scanner(TimeSpan window)
    {
        Heading("Barcode scanner");

        using var scanner = PeripheralFactory.CreateScanner(_settings.Hardware);
        output.WriteLine($"  Configured as : {scanner.Name}");

        if (scanner is KeyboardWedgeScannerService wedge)
        {
            // A keyboard-emulation scanner types into whatever has focus, so at a console it is
            // simply read as a line. That is the same path the till uses, minus the timing test the
            // UI does to tell a scan from typing.
            output.WriteLine("  This scanner types like a keyboard. Scan an item now, or press Enter to skip.");
            output.Write("  > ");

            var typed = input.ReadLine();

            if (string.IsNullOrWhiteSpace(typed))
                return CheckResult.NotConfigured;

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
            output.WriteLine($"  FAILED to open the port: {ex.Message}");
            return CheckResult.Failed;
        }

        output.WriteLine($"  Listening for {window.TotalSeconds:0} seconds. Scan something.");
        Thread.Sleep(window);
        scanner.Stop();

        if (reads.Count == 0)
        {
            output.WriteLine("  Nothing was scanned.");
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
        output.WriteLine($"  Configured as : {scale.Name}");

        if (!scale.IsConfigured)
        {
            output.WriteLine("  No scale is set up for this lane.");
            return CheckResult.NotConfigured;
        }

        try
        {
            scale.Start();
        }
        catch (Exception ex)
        {
            output.WriteLine($"  FAILED to open the port: {ex.Message}");
            return CheckResult.Failed;
        }

        var readings = 0;
        var sawStable = false;

        scale.WeightChanged += (_, reading) =>
        {
            readings++;
            sawStable |= reading.Stability == WeightStability.Stable;
        };

        output.WriteLine($"  Reading for {window.TotalSeconds:0} seconds. Put something on the pan.");

        var deadline = DateTimeOffset.UtcNow + window;

        while (DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(500);
            var current = scale.Current;
            output.WriteLine($"    {current.Stability,-8}  gross {current.Gross,8:0.000} kg   net {current.Net,8:0.000} kg");
        }

        scale.Stop();

        output.WriteLine($"  Frames parsed : {readings}");

        if (readings == 0)
        {
            output.WriteLine("  Nothing arrived. Check the port, the baud rate, and that the scale is set to stream.");
            return CheckResult.Failed;
        }

        if (!sawStable)
        {
            output.WriteLine("  Frames arrived but none were stable. Only a settled reading may be billed.");
            return CheckResult.Failed;
        }

        return CheckResult.Passed;
    }

    /// <summary>Lists what the machine can see, which is the first thing to check when a port is wrong.</summary>
    public void ListPorts()
    {
        Heading("Serial ports");

        var ports = SystemSerialPort.AvailablePorts();

        if (ports.Count == 0)
            output.WriteLine("  None found.");

        foreach (var port in ports)
            output.WriteLine($"  {port}");
    }

    private CheckResult Report(ScannedBarcode? read)
    {
        if (read is not { } code)
            return CheckResult.Failed;

        output.WriteLine($"  Read          : {code.Code}");
        output.WriteLine($"  Symbology     : {code.Symbology}");
        output.WriteLine($"  Check digit   : {(code.CheckDigitValid ? "valid" : "INVALID — this is a misread")}");

        return code.CheckDigitValid ? CheckResult.Passed : CheckResult.Failed;
    }

    private void Heading(string title)
    {
        output.WriteLine();
        output.WriteLine(title);
        output.WriteLine(new string('-', Math.Max(title.Length, 20)));
    }

    private void WriteIndented(string text)
    {
        foreach (var line in text.Split(Environment.NewLine))
            output.WriteLine("    | " + line);
    }

    private bool Confirm(string question)
    {
        output.Write($"  {question} [y/N] ");
        var answer = input.ReadLine();

        return answer is not null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }
}
