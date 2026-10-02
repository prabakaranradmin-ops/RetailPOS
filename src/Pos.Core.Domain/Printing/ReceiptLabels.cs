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

/// <summary>How a lane lays out its customer's bill. Either way it is the same invoice, in law and in figures.</summary>
public enum ReceiptLayout
{
    /// <summary>
    /// Price, quantity and amount in columns, the tax block and all four tenders printed every
    /// time. The layout every lane had before there was a choice.
    /// </summary>
    Standard = 0,

    /// <summary>
    /// The shorter counter bill: item, quantity and amount only, the HSN and GST under each line,
    /// one large total, and only the tenders actually used. Modelled on the bill Tamil Nadu
    /// provision stores already hand out.
    /// </summary>
    Compact = 1,
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

    /// <summary>
    /// The paise given up or added to reach a whole rupee. Printed only when there is one.
    /// </summary>
    public required string RoundOff { get; init; }

    /// <summary>Heading for the reorder list at the foot of the day-end report.</summary>
    public required string LowStock { get; init; }

    /// <summary>Heading for deliveries past or near their use-by date, at the foot of the day-end report.</summary>
    public required string CheckTheDates { get; init; }

    /// <summary>Beside a date that has passed.</summary>
    public required string Expired { get; init; }

    public required string Reprint { get; init; }
    public required string BillNumber { get; init; }
    public required string Date { get; init; }
    public required string Time { get; init; }
    public required string Customer { get; init; }
    public required string Mobile { get; init; }
    public required string Lane { get; init; }
    public required string HeldAs { get; init; }

    public required string ItemName { get; init; }

    /// <summary>The item column's heading on the compact bill, which has less room for it.</summary>
    public required string ItemShort { get; init; }

    /// <summary>The one large figure at the foot of the compact bill: what is handed over.</summary>
    public required string TotalAmount { get; init; }

    /// <summary>Who the compact bill is made out to when nobody was named at the counter.</summary>
    public required string CashCustomer { get; init; }

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
    public required string CashCounted { get; init; }
    public required string CountedBy { get; init; }
    public required string DrawerOverBy { get; init; }
    public required string DrawerShortBy { get; init; }
    public required string DrawerExactlyRight { get; init; }
    public required string DrawerNotCounted { get; init; }
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

    /// <summary>Heading for money customers paid back against what they owed on credit.</summary>
    public required string CreditCollected { get; init; }

    /// <summary>The drawer line for credit paid back in cash, added to what should be counted.</summary>
    public required string CreditCollectedInCash { get; init; }

    /// <summary>Credit paid back by card or UPI, which goes to the bank rather than the drawer.</summary>
    public required string CreditCollectedToBank { get; init; }

    /// <summary>Why the section is not in sales: it is money owed from earlier days, and untaxed.</summary>
    public required string CreditCollectedNote { get; init; }

    /// <summary>Heading of the slip given to a customer who pays back credit.</summary>
    public required string PaymentReceived { get; init; }

    /// <summary>What they paid on the slip.</summary>
    public required string AmountPaid { get; init; }

    /// <summary>What they still owe after paying.</summary>
    public required string StillOwed { get; init; }

    /// <summary>
    /// Says what the slip is not. It carries no goods and no tax, and a customer - or an inspector -
    /// must not be able to mistake it for an invoice.
    /// </summary>
    public required string PaymentSlipNote { get; init; }

    /// <summary>The drawer line for cash paid to suppliers out of the till.</summary>
    public required string PaidToSuppliers { get; init; }

    /// <summary>The drawer line for cash handed back on returns.</summary>
    public required string RefundedInCash { get; init; }

    /// <summary>The drawer line for the change the day started with.</summary>
    public required string OpeningFloat { get; init; }

    /// <summary>The big figure on a shelf label: what the shop charges.</summary>
    public required string OurPrice { get; init; }

    /// <summary>What a shelf label says the customer saves against the MRP.</summary>
    public required string YouSave { get; init; }

    /// <summary>The drawer line for expenses paid out of the till.</summary>
    public required string ExpensesPaid { get; init; }

    /// <summary>The drawer line for cash put in other than by a sale.</summary>
    public required string CashPutIn { get; init; }

    /// <summary>The drawer line for cash taken out to the bank or the owner.</summary>
    public required string CashTakenOut { get; init; }

    /// <summary>What a return is called. Left in English in every language: it is the term in law.</summary>
    public required string CreditNote { get; init; }

    /// <summary>The bill the goods on a credit note were sold on.</summary>
    public required string AgainstBill { get; init; }

    /// <summary>Why the goods came back.</summary>
    public required string Reason { get; init; }

    /// <summary>A line refunded but not put back on the shelf.</summary>
    public required string Damaged { get; init; }

    /// <summary>What the customer gets back, at the foot of a credit note.</summary>
    public required string Refund { get; init; }

    /// <summary>A refund taken off what the customer owes, rather than handed over.</summary>
    public required string OffTheKhata { get; init; }

    /// <summary>The tax a credit note, or the day's returns, took back.</summary>
    public required string TaxReversed { get; init; }

    /// <summary>Heading for the day's credit notes on the day-end report.</summary>
    public required string Returns { get; init; }
    public required string CreditNotes { get; init; }
    public required string ValueRefunded { get; init; }
    public required string NetAfterReturns { get; init; }
    public required string ReturnsNote { get; init; }

    public required string ByCashier { get; init; }
    public required string CashierName { get; init; }
    public required string CashHeld { get; init; }
    public required string Reconciled { get; init; }
    public required string DoesNotReconcile { get; init; }
    public required string GrossLessDiscount { get; init; }
    public required string TaxablePlusTax { get; init; }
    public required string TendersLessChange { get; init; }
    /// <summary>After a count of more than one: "2 bills still held".</summary>
    public required string BillsStillHeld { get; init; }

    /// <summary>After a count of one: "1 bill still held". The same words in a language that does not inflect.</summary>
    public required string BillStillHeld { get; init; }
    public required string HeldBillsNote { get; init; }

    /// <summary>After a count of more than one: "2 orders waiting".</summary>
    public required string OrdersWaiting { get; init; }

    /// <summary>After a count of one: "1 order waiting".</summary>
    public required string OrderWaiting { get; init; }
    public required string ScanToPay { get; init; }
    public required string UpiSlipNote { get; init; }
    public required string KhataStatement { get; init; }
    public required string Period { get; init; }
    public required string OpeningBalance { get; init; }
    public required string EntryBill { get; init; }
    public required string EntryPaid { get; init; }
    public required string EntryReturned { get; init; }
    public required string BoughtOnCredit { get; init; }
    public required string PaidBack { get; init; }
    public required string ReturnedGoods { get; init; }
    public required string OwedNow { get; init; }
    public required string OldestUnpaid { get; init; }
    public required string Days { get; init; }
    public required string AgeUpTo30 { get; init; }
    public required string Age31To60 { get; init; }
    public required string Age61To90 { get; init; }
    public required string AgeOver90 { get; init; }
    public required string StatementNote { get; init; }
    public required string NothingOwed { get; init; }
    public required string Offer { get; init; }
    public required string BillTo { get; init; }
    public required string BuyerGstin { get; init; }
    public required string PlaceOfSupply { get; init; }
    public required string OrdersWaitingNote { get; init; }

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
        CheckTheDates = "CHECK THE DATES (use by)",
        Expired = "EXPIRED",
        Reprint = "** REPRINT **",
        BillNumber = "Bill No",
        Date = "Date",
        Time = "Time",
        Customer = "Customer",
        Mobile = "Mobile",
        Lane = "Lane",
        HeldAs = "Held as",

        ItemName = "Item",
        ItemShort = "Item",
        TotalAmount = "Total Amount",
        CashCustomer = "CASH",
        Rate = "Rate",
        Quantity = "Qty",
        Amount = "Amount",

        TaxableValue = "Taxable value",
        Discount = "Discount",
        Cgst = "CGST",
        Sgst = "SGST",
        Igst = "IGST",
        RoundOff = "Round off",
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

        // "Khata", the shop's own word, as on the screen: "Credit" was one of four names for money
        // a customer owes, and in Indian retail "store credit" usually means the opposite.
        Credit = "Khata",
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
        CashCounted = "Cash counted",
        CountedBy = "Counted by",
        DrawerOverBy = "OVER BY",
        DrawerShortBy = "SHORT BY",
        DrawerExactlyRight = "COUNTED: EXACTLY RIGHT",
        DrawerNotCounted = "Not counted at the close",
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
        CreditCollected = "Khata collected",
        CreditCollectedInCash = "Khata collected in cash",
        CreditCollectedToBank = "By card or UPI",
        CreditCollectedNote = "Paid back against the khata. Not sales, no tax.",
        PaymentReceived = "PAYMENT RECEIVED",
        AmountPaid = "Paid",
        StillOwed = "Still owed",
        PaymentSlipNote = "Against the khata. Not a tax invoice.",
        PaidToSuppliers = "Paid to suppliers",
        RefundedInCash = "Refunded on returns",
        OpeningFloat = "Opening float",
        OurPrice = "Our price",
        YouSave = "You save",
        ExpensesPaid = "Expenses paid",
        CashPutIn = "Cash put in",
        CashTakenOut = "Cash taken out",
        CreditNote = "CREDIT NOTE",
        AgainstBill = "Against bill",
        Reason = "Reason",
        Damaged = "damaged, not restocked",
        Refund = "Refund",
        OffTheKhata = "Off the khata",
        TaxReversed = "Tax reversed",
        Returns = "Returns",
        CreditNotes = "Credit notes",
        ValueRefunded = "Value refunded",
        NetAfterReturns = "Net after returns",
        ReturnsNote = "On their own documents. Sales above are unchanged.",
        ByCashier = "By cashier",
        CashierName = "Name",
        CashHeld = "Cash",
        Reconciled = "Reconciled: sales, tax and tenders all agree.",
        DoesNotReconcile = "*** DOES NOT RECONCILE ***",
        GrossLessDiscount = "gross less discount",
        TaxablePlusTax = "taxable plus tax",
        TendersLessChange = "tenders less change",
        BillsStillHeld = "bills still held",
        BillStillHeld = "bill still held",
        HeldBillsNote = "These are not sales. Recall or discard them.",
        OrdersWaiting = "orders waiting",
        OrderWaiting = "order waiting",
        ScanToPay = "SCAN TO PAY BY UPI",
        UpiSlipNote = "Not a bill. Your bill prints once it is paid.",
        KhataStatement = "KHATA STATEMENT",
        Period = "Period",
        OpeningBalance = "Owed at the start",
        EntryBill = "Bill",
        EntryPaid = "Paid",
        EntryReturned = "Returned",
        BoughtOnCredit = "Bought on khata",
        PaidBack = "Paid back",
        ReturnedGoods = "Goods returned",
        OwedNow = "OWED NOW",
        OldestUnpaid = "Oldest unpaid bill",
        Days = "days",
        AgeUpTo30 = "  Up to 30 days",
        Age31To60 = "  31 to 60 days",
        Age61To90 = "  61 to 90 days",
        AgeOver90 = "  Over 90 days",
        StatementNote = "Not a bill. Your khata as the shop's books have it.",
        NothingOwed = "Nothing is owed. Thank you.",
        Offer = "Offer",
        BillTo = "Bill to",
        BuyerGstin = "Buyer GSTIN",
        PlaceOfSupply = "Place of supply",
        OrdersWaitingNote = "To collect or deliver.",
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

        // Plain shop Tamil for the words the reference bill did not carry. They were English until
        // a shopkeeper said what they print, which left a Tamil lane's day-end report half English;
        // the pilot shop is to check them, as with the round-off line below.
        CheckTheDates = "தேதி சரிபார்க்கவும் (காலாவதி)",
        Expired = "காலாவதியானது",

        // Left in English, like TAX INVOICE: it is the mark somebody checking for a duplicate - an
        // inspector, or a customer disputing a second bill - looks for, and it prints as characters.
        Reprint = "** REPRINT **",
        BillNumber = "பில் நம்பர்",
        Date = "தேதி",
        Time = "நேரம்",
        Customer = "கஸ்டமர்",
        Mobile = "மொபைல்",
        Lane = "லேன்",
        HeldAs = "நிறுத்தியது",

        ItemName = "பொருளின் பெயர்",

        // These three are as the reference counter bill prints them: the heading in Tamil, and the
        // total and the walk-in customer in English, which is what the shops' own bills say.
        ItemShort = "பொருள்",
        TotalAmount = "Total Amount",
        CashCustomer = "CASH",
        Rate = "விலை",
        Quantity = "அளவு",
        Amount = "தொகை",

        TaxableValue = "வரிக்குரிய தொகை",
        Discount = "தள்ளுபடி",
        Cgst = "CGST",
        Sgst = "SGST",
        Igst = "IGST",

        // Worth a native eye before a shop prints it. The reference bill this set was taken from
        // prints this one line in English, so there was nothing to copy; this follows the rest of
        // the Tamil set rather than leaving a lone English label among translated ones.
        RoundOff = "வட்டமிடல்",
        Total = "மொத்தம்",
        Items = "பொருட்கள்",
        TotalQuantity = "அளவு",

        TaxSummary = "வரி விவரம்",
        TaxSummaryRate = "வரி %",
        TaxSummaryTaxable = "வரிக்குரிய",
        TaxSummaryTax = "வரி",

        // The reference bill prints these three in English, as the shops say them. The khata is கடன்,
        // the word the khata statement already prints.
        Cash = "Cash",
        Card = "Card",
        Upi = "UPI",
        Credit = "கடன்",
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

        // The count at closing, in the words somebody counting a drawer would say. On the pilot
        // shop's sign-off sheet with the other words chosen without them.
        CashCounted = "எண்ணிய ரொக்கம்",
        CountedBy = "எண்ணியவர்",
        DrawerOverBy = "கூடுதல்",
        DrawerShortBy = "குறைவு",
        DrawerExactlyRight = "சரியாக உள்ளது",
        DrawerNotCounted = "முடிக்கும்போது எண்ணப்படவில்லை",
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

        // The khata and the drawer, in the same plain shop Tamil as the rest of the report, and to
        // be checked with the pilot shop like the words at the top. கடன் is the khata statement's
        // own word for what a customer owes.
        CreditCollected = "கடன் வசூல்",
        CreditCollectedInCash = "ரொக்கமாக கடன் வசூல்",
        CreditCollectedToBank = "கார்டு அல்லது UPI மூலம்",
        CreditCollectedNote = "முன்பு வாங்கிய கடனுக்குச் செலுத்தியது. விற்பனை அல்ல, வரி இல்லை.",
        PaymentReceived = "பணம் பெறப்பட்டது",
        AmountPaid = "செலுத்தியது",
        StillOwed = "மீதி நிலுவை",
        PaymentSlipNote = "கடன் கணக்கிற்கு. இது வரி பில் அல்ல.",
        PaidToSuppliers = "சப்ளையர்களுக்கு கொடுத்தது",

        // "CREDIT NOTE" stays English for good - it is the document's name in law, as "TAX
        // INVOICE" is. The words around it are Tamil.
        RefundedInCash = "திருப்பியதற்கு கொடுத்த பணம்",
        OpeningFloat = "தொடக்க சில்லறை",

        // On the shelf, where the customer reads it: the same word for price as the bill's column,
        // and the same word for saving as its foot.
        OurPrice = "எங்கள் விலை",
        YouSave = "சேமிப்பு",
        ExpensesPaid = "செலவுகள்",
        CashPutIn = "பெட்டியில் வைத்த பணம்",
        CashTakenOut = "பெட்டியிலிருந்து எடுத்த பணம்",
        CreditNote = "CREDIT NOTE",
        AgainstBill = "அசல் பில்",
        Reason = "காரணம்",
        Damaged = "சேதம், மீண்டும் அடுக்கவில்லை",
        Refund = "திருப்பித் தரும் தொகை",
        OffTheKhata = "கடனில் கழித்தது",
        TaxReversed = "திரும்பப் பெற்ற வரி",
        Returns = "திருப்பியவை",
        CreditNotes = "கிரெடிட் நோட்",
        ValueRefunded = "திருப்பிய தொகை",
        NetAfterReturns = "திருப்பியதற்குப் பின் நிகரம்",
        ReturnsNote = "தனி ஆவணங்களில். மேலே உள்ள விற்பனை மாறவில்லை.",
        ByCashier = "கேஷியர் வாரியாக",
        CashierName = "பெயர்",
        CashHeld = "ரொக்கம்",
        Reconciled = "சரிபார்க்கப்பட்டது: விற்பனை, வரி, பணம் ஒத்துப்போகிறது.",
        DoesNotReconcile = "*** ஒத்துப்போகவில்லை ***",
        GrossLessDiscount = "மொத்த விற்பனை - தள்ளுபடி",
        TaxablePlusTax = "வரிக்குரிய தொகை + வரி",
        TendersLessChange = "வந்த பணம் - கொடுத்த மீதம்",
        BillsStillHeld = "பில் நிறுத்தி வைக்கப்பட்டுள்ளது",
        BillStillHeld = "பில் நிறுத்தி வைக்கப்பட்டுள்ளது",
        HeldBillsNote = "இவை விற்பனை அல்ல. மீண்டும் எடுக்கவும் அல்லது நீக்கவும்.",
        OrdersWaiting = "ஆர்டர் காத்திருக்கிறது",
        OrderWaiting = "ஆர்டர் காத்திருக்கிறது",
        ScanToPay = "ஸ்கேன் செய்து UPI மூலம் செலுத்தவும்",
        UpiSlipNote = "இது பில் அல்ல. பணம் செலுத்திய பின் பில் வரும்.",
        KhataStatement = "கடன் கணக்கு அறிக்கை",
        Period = "காலம்",
        OpeningBalance = "தொடக்க நிலுவை",
        EntryBill = "பில்",
        EntryPaid = "செலுத்தியது",
        EntryReturned = "திருப்பியது",
        BoughtOnCredit = "கடனில் வாங்கியது",
        PaidBack = "திரும்பச் செலுத்தியது",
        ReturnedGoods = "திருப்பிய பொருட்கள்",
        OwedNow = "இப்போது நிலுவை",
        OldestUnpaid = "பழைய நிலுவை பில்",
        Days = "நாள்",
        AgeUpTo30 = "  30 நாள் வரை",
        Age31To60 = "  31 முதல் 60 நாள்",
        Age61To90 = "  61 முதல் 90 நாள்",
        AgeOver90 = "  90 நாளுக்கு மேல்",
        StatementNote = "இது பில் அல்ல. கடையின் கணக்கின்படி உங்கள் நிலுவை.",
        NothingOwed = "நிலுவை எதுவும் இல்லை. நன்றி.",
        Offer = "சலுகை",
        BillTo = "வாங்குபவர்",
        BuyerGstin = "வாங்குபவர் GSTIN",
        PlaceOfSupply = "விநியோக இடம்",
        OrdersWaitingNote = "எடுத்துச் செல்ல அல்லது அனுப்ப.",
    };
}
