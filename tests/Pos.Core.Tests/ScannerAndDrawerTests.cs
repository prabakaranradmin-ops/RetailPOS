using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.Core.Hardware.Scanning;
using Pos.Core.Hardware.Serial;
using Xunit;

namespace Pos.Core.Tests;

public class BarcodeTests
{
    [Theory]
    [InlineData("8901234567890", Symbology.Ean13)]
    [InlineData("012345678905", Symbology.UpcA)]
    [InlineData("96385074", Symbology.Ean8)]
    [InlineData("ABC123", Symbology.Unknown)]
    [InlineData("12345", Symbology.Unknown)]
    [InlineData("", Symbology.Unknown)]
    public void SymbologyIsIdentifiedByLengthAndCharacterSet(string code, Symbology expected)
    {
        Assert.Equal(expected, Barcode.Identify(code));
    }

    /// <summary>
    /// Known-good codes with their published check digits — a UPC-A, an EAN-8, and the EAN-13 off
    /// a Ritter Sport bar. The rule weights digits 3 and 1 alternately from the right, and getting
    /// that alternation backwards still passes for roughly a tenth of codes, so these are fixed
    /// published values rather than anything this code computed for itself.
    /// </summary>
    [Theory]
    [InlineData("890123456789", 0)]
    [InlineData("01234567890", 5)]
    [InlineData("9638507", 4)]
    [InlineData("400638133393", 1)]
    public void TheCheckDigitFollowsTheModuloTenRule(string body, int expected)
    {
        Assert.Equal(expected, Barcode.CheckDigit(body));
    }

    [Theory]
    [InlineData("8901234567890")]
    [InlineData("012345678905")]
    [InlineData("96385074")]
    [InlineData("4006381333931")]
    public void AGenuineCodeValidates(string code)
    {
        Assert.True(Barcode.IsValid(code));
    }

    /// <summary>
    /// The failure this exists to catch: two adjacent digits swapped. A scanner rarely does this,
    /// but a cashier reading a smudged label into the keyboard does, and the transposed code
    /// happily matches a different product.
    /// </summary>
    [Fact]
    public void ATransposedPairIsCaught()
    {
        Assert.True(Barcode.IsValid("8901234567890"));

        // The last two body digits swapped, keeping the original check digit.
        Assert.False(Barcode.IsValid("8901234567980"));

        // And a swap further back in the body.
        Assert.False(Barcode.IsValid("8901234568790"));
    }

    [Fact]
    public void AWrongCheckDigitIsCaught()
    {
        Assert.False(Barcode.IsValid("8901234567894"));
    }

    /// <summary>
    /// Stores use internal codes and symbologies with no check digit. Refusing those would be
    /// worse than not verifying them, so anything unrecognised is passed through as valid.
    /// </summary>
    [Theory]
    [InlineData("INTERNAL-4471")]
    [InlineData("12345")]
    public void ACodeWithNoCheckDigitToTestIsAccepted(string code)
    {
        Assert.Equal(Symbology.Unknown, Barcode.Identify(code));
        Assert.True(Barcode.IsValid(code));
    }

    [Fact]
    public void AnEmptyCodeIsNeverValid()
    {
        Assert.False(Barcode.IsValid(""));
        Assert.False(Barcode.IsValid("   "));
        Assert.False(Barcode.IsValid(null));
    }

    [Fact]
    public void AppendingTheCheckDigitProducesAValidCode()
    {
        Assert.Equal("8901234567890", Barcode.WithCheckDigit("890123456789"));
        Assert.True(Barcode.IsValid(Barcode.WithCheckDigit("890123456789")));
    }

    [Fact]
    public void ANonNumericBodyIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Barcode.CheckDigit("89012345678A"));
    }
}

public class ScannerServiceTests
{
    private static (SerialScannerService Scanner, FakeSerialPort Port, List<ScannedBarcode> Reads) NewScanner()
    {
        var port = new FakeSerialPort();
        var scanner = new SerialScannerService(port);
        var reads = new List<ScannedBarcode>();

        scanner.BarcodeScanned += (_, code) => reads.Add(code);
        scanner.Start();

        return (scanner, port, reads);
    }

    [Fact]
    public void ATerminatedLineBecomesAScan()
    {
        var (scanner, port, reads) = NewScanner();
        using var _ = scanner;

        port.Receive("8901234567890\r\n");

        var read = Assert.Single(reads);
        Assert.Equal("8901234567890", read.Code);
        Assert.Equal(Symbology.Ean13, read.Symbology);
        Assert.True(read.CheckDigitValid);
    }

    [Fact]
    public void ACodeSplitAcrossReadsIsReassembled()
    {
        var (scanner, port, reads) = NewScanner();
        using var _ = scanner;

        port.ReceiveInChunks("8901234567890\r\n", chunkSize: 2);

        Assert.Single(reads);
        Assert.Equal("8901234567890", reads[0].Code);
    }

    [Fact]
    public void SeveralCodesInOneReadAllArrive()
    {
        var (scanner, port, reads) = NewScanner();
        using var _ = scanner;

        port.Receive("96385074\r\n012345678905\r\n");

        Assert.Equal(2, reads.Count);
        Assert.Equal("96385074", reads[0].Code);
        Assert.Equal("012345678905", reads[1].Code);
    }

    /// <summary>A scanner that catches a glint sends an empty line. Nobody wants a search for nothing.</summary>
    [Fact]
    public void BlankReadsAreDropped()
    {
        var (scanner, port, reads) = NewScanner();
        using var _ = scanner;

        port.Receive("\r\n\r\n   \r\n");

        Assert.Empty(reads);
    }

    /// <summary>
    /// A bad check digit is reported alongside the code rather than swallowed, so the till can ask
    /// the cashier to scan again instead of ringing up the wrong product.
    /// </summary>
    [Fact]
    public void AMisreadCodeIsDeliveredButFlagged()
    {
        var (scanner, port, reads) = NewScanner();
        using var _ = scanner;

        port.Receive("8901234567894\r\n");

        var read = Assert.Single(reads);
        Assert.False(read.CheckDigitValid);
        Assert.Equal("8901234567894", read.Code);
    }

    /// <summary>
    /// A line that never terminates must not grow the buffer without limit — that is a till that
    /// slowly eats memory across a trading day. The noise costs the frame it is attached to and
    /// nothing beyond it.
    /// </summary>
    [Fact]
    public void LineNoiseDoesNotGrowTheBufferWithoutBound()
    {
        var (scanner, port, reads) = NewScanner();
        using var _ = scanner;

        port.Receive(new string('x', 10_000));
        port.Receive("\r\n");
        port.Receive("96385074\r\n");

        var read = Assert.Single(reads);
        Assert.Equal("96385074", read.Code);
    }

    [Fact]
    public void StoppingEndsTheStream()
    {
        var (scanner, port, reads) = NewScanner();
        using var _ = scanner;

        scanner.Stop();
        port.Receive("96385074\r\n");

        Assert.Empty(reads);
        Assert.False(scanner.IsConnected);
    }

    /// <summary>
    /// The keyboard-emulation path puts its bursts through the same pipeline, so both kinds of
    /// scanner are validated and reported identically.
    /// </summary>
    [Fact]
    public void TheKeyboardWedgePathValidatesTheSameWay()
    {
        using var scanner = new KeyboardWedgeScannerService();
        var reads = new List<ScannedBarcode>();
        scanner.BarcodeScanned += (_, code) => reads.Add(code);

        scanner.Accept("8901234567890");
        scanner.Accept("8901234567894");
        scanner.Accept("   ");

        Assert.Equal(2, reads.Count);
        Assert.True(reads[0].CheckDigitValid);
        Assert.False(reads[1].CheckDigitValid);
    }

    [Fact]
    public void TheFakeScannerFiresScansOnDemand()
    {
        using var scanner = new FakeScannerService();
        var reads = new List<ScannedBarcode>();
        scanner.BarcodeScanned += (_, code) => reads.Add(code);

        scanner.Start();
        scanner.Scan("96385074");

        Assert.True(scanner.IsConnected);
        Assert.Single(reads);
    }
}

public class DrawerServiceTests
{
    [Fact]
    public void ThePassthroughDrawerSendsTheKickThroughThePrinter()
    {
        var printer = new LoopbackPrinterService();
        var drawer = new PrinterPassthroughDrawerService(printer);

        Assert.Equal(DrawerKickResult.Opened, drawer.Kick());
        Assert.Equal(EscPos.KickDrawer(), printer.LastJob);
    }

    [Fact]
    public void TheSecondDrawerPinIsHonoured()
    {
        var printer = new LoopbackPrinterService();
        var drawer = new PrinterPassthroughDrawerService(printer, pin: 1);

        drawer.Kick();

        Assert.Equal(EscPos.KickDrawer(pin: 1), printer.LastJob);
    }

    /// <summary>
    /// A drawer on the printer's port goes offline with the printer. Reporting that is what lets
    /// the cashier reach for the key instead of pulling at a drawer that will not move.
    /// </summary>
    [Fact]
    public void APrinterFailureIsReportedAsADrawerFailure()
    {
        var printer = new LoopbackPrinterService { FailWith = "out of paper" };
        var drawer = new PrinterPassthroughDrawerService(printer);

        Assert.Equal(DrawerKickResult.Failed, drawer.Kick());
    }

    [Fact]
    public void NoPrinterMeansNoPassthroughDrawer()
    {
        var drawer = new PrinterPassthroughDrawerService(new NoPrinterService());

        Assert.False(drawer.IsConfigured);
        Assert.Equal(DrawerKickResult.NoDrawerAttached, drawer.Kick());
    }

    [Fact]
    public void TheSerialDrawerWritesTheKickToThePort()
    {
        var port = new FakeSerialPort();
        var drawer = new SerialDrawerService(port);

        Assert.Equal(DrawerKickResult.Opened, drawer.Kick());
        Assert.Equal(EscPos.KickDrawer(), port.Written.ToArray());
    }

    [Fact]
    public void TheSerialDrawerOpensThePortIfItIsNotAlreadyOpen()
    {
        var port = new FakeSerialPort();
        var drawer = new SerialDrawerService(port);

        Assert.False(port.IsOpen);
        drawer.Kick();
        Assert.True(port.IsOpen);
    }

    /// <summary>An unplugged drawer reports a failure rather than throwing into the sale.</summary>
    [Fact]
    public void AnUnpluggedSerialDrawerReportsFailure()
    {
        var port = new FakeSerialPort { FailWith = new IOException("The port is gone.") };
        var drawer = new SerialDrawerService(port);

        Assert.Equal(DrawerKickResult.Failed, drawer.Kick());
    }

    [Fact]
    public void ALaneWithNoDrawerSaysSo()
    {
        var drawer = new NoDrawerService();

        Assert.False(drawer.IsConfigured);
        Assert.Equal(DrawerKickResult.NoDrawerAttached, drawer.Kick());
    }
}

public class PrinterServiceTests
{
    [Fact]
    public void TheLoopbackPrinterKeepsEveryJob()
    {
        var printer = new LoopbackPrinterService();

        printer.Print([1, 2, 3]);
        printer.Print([4, 5]);

        Assert.Equal(2, printer.Jobs.Count);
        Assert.Equal(new byte[] { 4, 5 }, printer.LastJob);
    }

    [Fact]
    public void AConfiguredPrinterReportsWhatItWrote()
    {
        var outcome = new LoopbackPrinterService().Print([1, 2, 3]);

        Assert.True(outcome.Succeeded);
        Assert.Equal(3, outcome.BytesWritten);
    }

    [Fact]
    public void AFailingPrinterReportsRatherThanThrows()
    {
        var outcome = new LoopbackPrinterService { FailWith = "out of paper" }.Print([1]);

        Assert.Equal(PrintStatus.Failed, outcome.Status);
        Assert.Equal("out of paper", outcome.Detail);
    }

    [Fact]
    public void ALaneWithNoPrinterSaysSo()
    {
        var outcome = new NoPrinterService().Print([1]);

        Assert.Equal(PrintStatus.NoPrinterConfigured, outcome.Status);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void TheFilePrinterWritesTheJobToDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pos-print-{Guid.NewGuid():N}.bin");

        try
        {
            var outcome = new FilePrinterService(path).Print([0x1B, 0x40, (byte)'A']);

            Assert.True(outcome.Succeeded);
            Assert.Equal(new byte[] { 0x1B, 0x40, (byte)'A' }, File.ReadAllBytes(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void AnUnwritableFilePrinterReportsRatherThanThrows()
    {
        var outcome = new FilePrinterService(Path.Combine(Path.GetTempPath(), "pos-tests-dir", "nested")).Print([1]);

        // Either it wrote, or it reported cleanly. What it must not do is throw.
        Assert.True(outcome.Succeeded || outcome.Status == PrintStatus.Failed);
    }
}
