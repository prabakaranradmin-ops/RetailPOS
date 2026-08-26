namespace Pos.Core.Hardware.Printing;

public enum PrintStatus
{
    Printed = 0,

    /// <summary>No printer is set up on this lane. Not an error — some counters have none.</summary>
    NoPrinterConfigured = 1,

    /// <summary>A printer is configured but the job did not reach it.</summary>
    Failed = 2,
}

/// <param name="Status">What happened.</param>
/// <param name="Detail">Something a shopkeeper can act on when it failed. Empty otherwise.</param>
/// <param name="BytesWritten">Size of the job that was sent.</param>
public readonly record struct PrintOutcome(PrintStatus Status, string Detail = "", int BytesWritten = 0)
{
    public bool Succeeded => Status == PrintStatus.Printed;

    public static PrintOutcome Printed(int bytes) => new(PrintStatus.Printed, string.Empty, bytes);

    public static PrintOutcome NotConfigured() => new(PrintStatus.NoPrinterConfigured);

    public static PrintOutcome Failed(string detail) => new(PrintStatus.Failed, detail);
}

/// <summary>
/// Sends a prepared ESC/POS job to the receipt printer.
/// </summary>
/// <remarks>
/// The interface takes bytes rather than a document because composing the receipt and delivering
/// it are separate problems: the layout is worth testing exhaustively and needs no hardware, while
/// delivery is a thin, untestable-without-a-device shim over the spooler or a serial port.
/// <para>
/// Nothing here throws for a hardware fault. A printer that is out of paper must not cost a sale
/// that has already been paid for, so failures are returned and the caller decides.
/// </para>
/// </remarks>
public interface IPrinterService
{
    /// <summary>False when the lane has no printer set up.</summary>
    bool IsConfigured { get; }

    /// <summary>Identifies the printer in diagnostics and error messages.</summary>
    string Name { get; }

    /// <summary>Characters per line, which the receipt layout is built against.</summary>
    int PaperWidthChars { get; }

    /// <summary>
    /// How this printer draws text it has no glyphs for, or null when it draws nothing.
    /// </summary>
    /// <remarks>
    /// It hangs off the printer because the answer is a property of the device — how wide its head
    /// is in dots, and what the lane has installed to draw with. A caller composing a receipt should
    /// not have to know either; it renders the layout for whatever printer it was handed.
    /// </remarks>
    RasterOptions? Raster => null;

    PrintOutcome Print(byte[] job);
}

/// <summary>Stands in on a lane with no printer. Reports honestly rather than pretending.</summary>
public sealed class NoPrinterService(int paperWidthChars = ReceiptBuilder.Width80Mm) : IPrinterService
{
    public bool IsConfigured => false;

    public string Name => "none";

    public int PaperWidthChars { get; } = paperWidthChars;

    public PrintOutcome Print(byte[] job) => PrintOutcome.NotConfigured();
}

/// <summary>
/// Keeps every job in memory instead of printing it. Used by the tests to assert what would have
/// been sent, and by the diagnostics tool to show a receipt without wasting paper.
/// </summary>
public sealed class LoopbackPrinterService(int paperWidthChars = ReceiptBuilder.Width80Mm) : IPrinterService
{
    private readonly List<byte[]> _jobs = [];

    public bool IsConfigured { get; set; } = true;

    public string Name => "loopback";

    public int PaperWidthChars { get; } = paperWidthChars;

    /// <summary>Settable so a test can drive the drawn path without a real printer.</summary>
    public RasterOptions? Raster { get; set; }

    /// <summary>Set to make the next print fail, for exercising the degraded path.</summary>
    public string? FailWith { get; set; }

    public IReadOnlyList<byte[]> Jobs => _jobs;

    public byte[] LastJob => _jobs.Count > 0 ? _jobs[^1] : [];

    public PrintOutcome Print(byte[] job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!IsConfigured)
            return PrintOutcome.NotConfigured();

        if (FailWith is { } reason)
            return PrintOutcome.Failed(reason);

        _jobs.Add(job);
        return PrintOutcome.Printed(job.Length);
    }

    public void Clear() => _jobs.Clear();
}
