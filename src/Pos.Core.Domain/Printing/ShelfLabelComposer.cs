using System.Globalization;
using Pos.Core.Hardware.Printing;

namespace Pos.Core.Domain.Printing;

/// <summary>
/// Shelf labels on the till's own receipt printer: one label per cut, to trim and slide into the
/// shelf-edge strip.
/// </summary>
/// <remarks>
/// A shop with a receipt printer has a label printer already. Each label carries the name, the MRP,
/// the price the shop charges large enough to read from the aisle, what that saves against the MRP,
/// and the item's barcode drawn by the printer - or its SKU as a barcode for something sold loose -
/// so a label can be scanned straight off the shelf when the packet has no code of its own.
/// </remarks>
public sealed class ShelfLabelComposer(
    int paperWidthChars = ReceiptBuilder.Width80Mm,
    ReceiptLanguage language = ReceiptLanguage.English,
    string currencyPrefix = "Rs")
{
    private readonly ReceiptLabels _labels = ReceiptLabels.For(language);

    public ReceiptBuilder Compose(IEnumerable<ShelfLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var paper = new ReceiptBuilder(paperWidthChars);

        foreach (var label in labels)
        {
            var unit = language == ReceiptLanguage.Tamil ? Units.Of(label.Unit).Tamil : Units.Of(label.Unit).Code;

            paper.Text(label.Name, TextAlignment.Center, bold: true);

            // The MRP beside the saving: the two figures a customer checks the price against.
            paper.Columns($"MRP {currencyPrefix} {Amount(label.Mrp)}",
                label.Saving > 0m ? $"{_labels.YouSave} {currencyPrefix} {Amount(label.Saving)}" : string.Empty);

            paper.Text(_labels.OurPrice, TextAlignment.Center);
            paper.Text($"{currencyPrefix} {Amount(label.Price)} / {unit}", TextAlignment.Center, bold: true, widthMultiplier: 2, heightMultiplier: 2);
            paper.Barcode(label.Code);
            paper.Cut(CutMode.Partial, feedBeforeCut: 3);
        }

        return paper;
    }

    private static string Amount(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);
}
