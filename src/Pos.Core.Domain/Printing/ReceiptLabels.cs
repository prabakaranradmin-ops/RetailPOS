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

    /// <summary>What a composition dealer's bill is called. Not a tax invoice, in law or in print.</summary>
    public required string BillOfSupply { get; init; }

    /// <summary>The total before payment on a bill that taxed nothing.</summary>
    public required string Subtotal { get; init; }

    /// <summary>Heading for the reorder list at the foot of the day-end report.</summary>
    public required string LowStock { get; init; }

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

    // The day-end report. It shares the tax and tender words above rather than carrying its own,
    // so a lane cannot end up calling the same figure two different things on two documents.
    public required string DayEndReport { get; init; }
    public required string Closed { get; init; }
    public required string FirstSale { get; init; }
    public required string ReportNumber { get; init; }
    public required string NoSalesInThisPeriod { get; init; }
    public required string CashInDrawerShouldBe { get; init; }
    public required string CashTaken { get; init; }
    public required string ChangeGiven { get; init; }
    public required string Sales { get; init; }
    public required string Invoices { get; init; }
    public required string GrossSales { get; init; }
    public required string NetSales { get; init; }
    public required string Tax { get; init; }
    public required string TotalTax { get; init; }
    public required string Tenders { get; init; }
    public required string RewardPoints { get; init; }
    public required string Redeemed { get; init; }
    public required string Earned { get; init; }
    public required string Voided { get; init; }
    public required string InvoicesVoided { get; init; }
    public required string ValueVoided { get; init; }
    public required string VoidsExcludedNote { get; init; }
    public required string ByCashier { get; init; }
    public required string CashierName { get; init; }
    public required string CashHeld { get; init; }
    public required string Reconciled { get; init; }
    public required string DoesNotReconcile { get; init; }
    public required string GrossLessDiscount { get; init; }
    public required string TaxablePlusTax { get; init; }
    public required string TendersLessChange { get; init; }
    public required string BillsStillParked { get; init; }
    public required string ParkedBillsNote { get; init; }

    public static ReceiptLabels For(ReceiptLanguage language) => language switch
    {
        ReceiptLanguage.Tamil => TamilLabels,
        _ => EnglishLabels,
    };

    public static ReceiptLabels EnglishLabels { get; } = new()
    {
        TaxInvoice = "TAX INVOICE",
        BillOfSupply = "BILL OF SUPPLY",
        Subtotal = "Subtotal",
        LowStock = "TO REORDER (have / level)",
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

        DayEndReport = "DAY-END REPORT (Z)",
        Closed = "Closed",
        FirstSale = "First sale",
        ReportNumber = "Report no",
        NoSalesInThisPeriod = "NO SALES IN THIS PERIOD",
        CashInDrawerShouldBe = "CASH IN DRAWER SHOULD BE",
        CashTaken = "Cash taken",
        ChangeGiven = "Change given",
        Sales = "Sales",
        Invoices = "Invoices",
        GrossSales = "Gross sales",
        NetSales = "Net sales",
        Tax = "Tax",
        TotalTax = "Total tax",
        Tenders = "Tenders",
        RewardPoints = "Reward points",
        Redeemed = "Redeemed",
        Earned = "Earned",
        Voided = "Voided",
        InvoicesVoided = "Invoices voided",
        ValueVoided = "Value voided",
        VoidsExcludedNote = "Excluded from sales and tax above.",
        ByCashier = "By cashier",
        CashierName = "Name",
        CashHeld = "Cash",
        Reconciled = "Reconciled: sales, tax and tenders all agree.",
        DoesNotReconcile = "*** DOES NOT RECONCILE ***",
        GrossLessDiscount = "gross less discount",
        TaxablePlusTax = "taxable plus tax",
        TendersLessChange = "tenders less change",
        BillsStillParked = "bill(s) still parked",
        ParkedBillsNote = "These are not sales. Recall or discard them.",
    };

    /// <summary>
    /// The Tamil set, taken from a printed Thanjavur grocery bill rather than composed here.
    /// </summary>
    public static ReceiptLabels TamilLabels { get; } = new()
    {
        // "TAX INVOICE" is left in English: it is the phrase the GST rules use and the one an
        // inspector looks for, so it is not a label to localise. "BILL OF SUPPLY" is the same
        // phrase for the same reason — it is what the document is called in law.
        TaxInvoice = "TAX INVOICE",
        BillOfSupply = "BILL OF SUPPLY",
        // Not மொத்தம் — that is already Total, and two lines reading the same word on one bill is
        // worse than a slightly formal word for the one above it.
        Subtotal = "இடைத்தொகை",
        LowStock = "ஆர்டர் செய்ய வேண்டியவை (உள்ளது / அளவு)",
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

        // The Z-report is the shopkeeper's own document rather than the customer's, so the wording
        // is the plain shop Tamil somebody counting a drawer at closing time would use.
        DayEndReport = "நாள் இறுதி அறிக்கை (Z)",
        Closed = "முடித்த நேரம்",
        FirstSale = "முதல் விற்பனை",
        ReportNumber = "அறிக்கை எண்",
        NoSalesInThisPeriod = "இந்த நேரத்தில் விற்பனை இல்லை",
        CashInDrawerShouldBe = "பணப்பெட்டியில் இருக்க வேண்டிய தொகை",
        CashTaken = "வந்த ரொக்கம்",
        ChangeGiven = "கொடுத்த மீதம்",
        Sales = "விற்பனை",
        Invoices = "பில்கள்",
        GrossSales = "மொத்த விற்பனை",
        NetSales = "நிகர விற்பனை",
        Tax = "வரி",
        TotalTax = "மொத்த வரி",
        Tenders = "பணம் செலுத்திய முறை",
        RewardPoints = "புள்ளிகள்",
        Redeemed = "பயன்படுத்தியது",
        Earned = "பெற்றது",
        Voided = "ரத்து செய்தவை",
        InvoicesVoided = "ரத்து செய்த பில்கள்",
        ValueVoided = "ரத்து செய்த தொகை",
        VoidsExcludedNote = "மேலே உள்ள விற்பனை மற்றும் வரியில் சேர்க்கப்படவில்லை.",
        ByCashier = "கேஷியர் வாரியாக",
        CashierName = "பெயர்",
        CashHeld = "ரொக்கம்",
        Reconciled = "சரிபார்க்கப்பட்டது: விற்பனை, வரி, பணம் ஒத்துப்போகிறது.",
        DoesNotReconcile = "*** ஒத்துப்போகவில்லை ***",
        GrossLessDiscount = "மொத்த விற்பனை - தள்ளுபடி",
        TaxablePlusTax = "வரிக்குரிய தொகை + வரி",
        TendersLessChange = "வந்த பணம் - கொடுத்த மீதம்",
        BillsStillParked = "பில் நிறுத்தி வைக்கப்பட்டுள்ளது",
        ParkedBillsNote = "இவை விற்பனை அல்ல. மீண்டும் எடுக்கவும் அல்லது நீக்கவும்.",
    };
}
