using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pos.Core.Hardware.Printing;

/// <summary>
/// Sends the job straight to the Windows spooler as RAW data.
/// </summary>
/// <remarks>
/// RAW means the bytes reach the printer exactly as written, with no driver rendering in between —
/// which is the point. Printing a receipt through GDI would rasterise text into a bitmap and push
/// several hundred kilobytes at a device built to take a few hundred bytes of ESC/POS, turning an
/// instant receipt into a visible wait at the counter (ARCHITECTURE.md section 5).
/// <para>
/// This is the one genuinely untestable class in the hardware layer: it is a thin shim over four
/// Win32 calls, and the only way to know it works is to attach a printer. Everything it sends is
/// composed and asserted elsewhere.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RawSpoolPrinterService(string printerName, int paperWidthChars = ReceiptBuilder.Width80Mm)
    : IPrinterService
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(printerName);

    public string Name { get; } = printerName ?? string.Empty;

    public int PaperWidthChars { get; } = paperWidthChars;

    public PrintOutcome Print(byte[] job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!IsConfigured)
            return PrintOutcome.NotConfigured();

        if (job.Length == 0)
            return PrintOutcome.Printed(0);

        var printer = IntPtr.Zero;
        var buffer = IntPtr.Zero;
        var documentStarted = false;
        var pageStarted = false;

        try
        {
            if (!OpenPrinter(Name, out printer, IntPtr.Zero))
                return PrintOutcome.Failed($"Could not open printer '{Name}': {LastError()}");

            var info = new DocInfo
            {
                DocName = "RetailPOS receipt",
                OutputFile = null,
                DataType = "RAW",
            };

            if (!StartDocPrinter(printer, 1, ref info))
                return PrintOutcome.Failed($"The spooler refused the job for '{Name}': {LastError()}");

            documentStarted = true;

            if (!StartPagePrinter(printer))
                return PrintOutcome.Failed($"The spooler refused the page for '{Name}': {LastError()}");

            pageStarted = true;

            buffer = Marshal.AllocHGlobal(job.Length);
            Marshal.Copy(job, 0, buffer, job.Length);

            if (!WritePrinter(printer, buffer, job.Length, out var written))
                return PrintOutcome.Failed($"Writing to '{Name}' failed: {LastError()}");

            if (written != job.Length)
                return PrintOutcome.Failed($"Only {written} of {job.Length} bytes reached '{Name}'.");

            return PrintOutcome.Printed(written);
        }
        catch (Exception ex)
        {
            // A print failure must not propagate into a completed sale.
            return PrintOutcome.Failed($"Printing to '{Name}' failed: {ex.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);

            if (printer != IntPtr.Zero)
            {
                if (pageStarted)
                    EndPagePrinter(printer);

                if (documentStarted)
                    EndDocPrinter(printer);

                ClosePrinter(printer);
            }
        }
    }

    private static string LastError() => new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartDocPrinter(IntPtr handle, int level, ref DocInfo info);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, IntPtr buffer, int count, out int written);
}

/// <summary>
/// Writes jobs to a file instead of a printer. Useful for capturing a real receipt byte stream to
/// compare against a printer that is misbehaving, and for a lane being set up before its printer
/// arrives.
/// </summary>
public sealed class FilePrinterService(string path, int paperWidthChars = ReceiptBuilder.Width80Mm) : IPrinterService
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(path);

    public string Name { get; } = path ?? string.Empty;

    public int PaperWidthChars { get; } = paperWidthChars;

    public PrintOutcome Print(byte[] job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!IsConfigured)
            return PrintOutcome.NotConfigured();

        try
        {
            var directory = Path.GetDirectoryName(Name);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var stream = new FileStream(Name, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(job);

            return PrintOutcome.Printed(job.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PrintOutcome.Failed($"Could not write to '{Name}': {ex.Message}");
        }
    }
}
