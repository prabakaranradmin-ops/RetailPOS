namespace Pos.Core.Domain.Printing;

/// <summary>Which language a lane prints its receipt labels in.</summary>
public enum ReceiptLanguage
{
    /// <summary>Labels in English. Prints on any thermal printer with no rasterisation.</summary>
    English = 0,

    /// <summary>
    /// Labels in Tamil. The figures, the item names and the invoice number stay as they are —
    /// only the labels change, which is how a Tamil Nadu grocery bill is actually laid out.
    /// </summary>
    Tamil = 1,
}

/// <summary>
/// The words on a receipt, in one language.
/// </summary>
/// <remarks>
/// Held as data rather than branched on at each call site so that a layout is written once and
/// proved in both languages, and so that adding a third language does not mean revisiting the
/// composer. The Tamil strings are the ones a Thanjavur grocery bill actually carries, not
/// translations of the English ones — <c>கஸ்டமர்</c> is the transliterated "customer" that shops
/// print, and replacing it with a more literal word would be a worse receipt.
/// </remarks>
public sealed record ReceiptLabels
{
    public required string TaxInvoice { get; init; }
    public required string Reprint { get; init; }
    public required string BillNumber { get; init; }
    public required string Date { get; init; }
    public required string Time { get; init; }
    public required string Customer { get; init; }
    public required string Mobile { get; init; }
    public required string Lane { get; init; }
    public required string ParkedAs { get; init; }

    public required string ItemName { get; init; }
    public required string Rate { get; init; }
    public required string Quantity { get; init; }
    public required string Amount { get; init; }

    public required string TaxableValue { get; init; }
    public required string Discount { get; init; }
    public required string Cgst { get; init; }
    public required string Sgst { get; init; }
    public required string Igst { get; init; }
    public required string Total { get; init; }
    public required string Items { get; init; }
    public required string TotalQuantity { get; init; }

    public required string TaxSummary { get; init; }
    public required string TaxSummaryRate { get; init; }
    public required string TaxSummaryTaxable { get; init; }
    public required string TaxSummaryTax { get; init; }

    public required string Cash { get; init; }
    public required string Card { get; init; }
    public required string Upi { get; init; }
    public required string Credit { get; init; }
    public required string LoyaltyPoints { get; init; }
    public required string Change { get; init; }

    public required string TodaysSaving { get; init; }
    public required string TotalPointsEarned { get; init; }
    public required string PointsRedeemed { get; init; }
    public required string PointsEarnedThisBill { get; init; }

    public static ReceiptLabels For(ReceiptLanguage language) => language switch
    {
        ReceiptLanguage.Tamil => TamilLabels,
        _ => EnglishLabels,
    };

    public static ReceiptLabels EnglishLabels { get; } = new()
    {
        TaxInvoice = "TAX INVOICE",
        Reprint = "** REPRINT **",
        BillNumber = "Bill No",
        Date = "Date",
        Time = "Time",
        Customer = "Customer",
        Mobile = "Mobile",
        Lane = "Lane",
        ParkedAs = "Parked as",

        ItemName = "Item",
        Rate = "Rate",
        Quantity = "Qty",
        Amount = "Amount",

        TaxableValue = "Taxable value",
        Discount = "Discount",
        Cgst = "CGST",
        Sgst = "SGST",
        Igst = "IGST",
        Total = "TOTAL",
        Items = "Items",
        TotalQuantity = "Qty",

        TaxSummary = "Tax summary",
        TaxSummaryRate = "Rate",
        TaxSummaryTaxable = "Taxable",
        TaxSummaryTax = "Tax",

        Cash = "Cash",
        Card = "Card",
        Upi = "UPI",
        Credit = "Credit",
        LoyaltyPoints = "Points",
        Change = "Change",

        TodaysSaving = "Today's saving",
        TotalPointsEarned = "Total points earned",
        PointsRedeemed = "Points redeemed",
        PointsEarnedThisBill = "Points earned",
    };

    /// <summary>
    /// The Tamil set, taken from a printed Thanjavur grocery bill rather than composed here.
    /// </summary>
    public static ReceiptLabels TamilLabels { get; } = new()
    {
        // "TAX INVOICE" is left in English: it is the phrase the GST rules use and the one an
        // inspector looks for, so it is not a label to localise.
        TaxInvoice = "TAX INVOICE",
        Reprint = "** REPRINT **",
        BillNumber = "பில் நம்பர்",
        Date = "தேதி",
        Time = "நேரம்",
        Customer = "கஸ்டமர்",
        Mobile = "மொபைல்",
        Lane = "லேன்",
        ParkedAs = "நிறுத்தியது",

        ItemName = "பொருளின் பெயர்",
        Rate = "விலை",
        Quantity = "அளவு",
        Amount = "தொகை",

        TaxableValue = "வரிக்குரிய தொகை",
        Discount = "தள்ளுபடி",
        Cgst = "CGST",
        Sgst = "SGST",
        Igst = "IGST",
        Total = "மொத்தம்",
        Items = "பொருட்கள்",
        TotalQuantity = "அளவு",

        TaxSummary = "வரி விவரம்",
        TaxSummaryRate = "வரி %",
        TaxSummaryTaxable = "வரிக்குரிய",
        TaxSummaryTax = "வரி",

        Cash = "Cash",
        Card = "Card",
        Upi = "UPI",
        Credit = "Credit",
        LoyaltyPoints = "புள்ளிகள்",
        Change = "மீதம்",

        TodaysSaving = "இன்றைய சேமிப்பு",
        TotalPointsEarned = "இதுவரை பெற்ற மொத்த புள்ளிகள்",
        PointsRedeemed = "பயன்படுத்திய புள்ளிகள்",
        PointsEarnedThisBill = "இந்த பில்லில் பெற்ற புள்ளிகள்",
    };
}
