using System.Globalization;
using System.Text;
using Pos.Core.Tax;

namespace Pos.Core.Domain.Printing;

/// <summary>
/// A settled bill as a message: what a customer gets on WhatsApp instead of - or as well as - the
/// paper bill.
/// </summary>
/// <remarks>
/// <para>
/// It carries what the paper does: the shop and its GSTIN, whether it is a tax invoice or a bill of
/// supply, the number and the time, each line with its HSN and rate, the tax by rate, the round-off,
/// what was paid and how. A customer who is sent this has their bill.
/// </para>
/// <para>
/// Nothing here sends anything. The till hands the text to WhatsApp on the same computer, or puts
/// it on the clipboard; the message leaves from there, after the sale and outside it.
/// </para>
/// </remarks>
public static class DigitalBill
{
    private static readonly CultureInfo Figures = CultureInfo.InvariantCulture;

    /// <summary>The bill as text, with WhatsApp's *bold* around the headings and the total.</summary>
    public static string Text(SettledInvoice invoice, StoreProfile store)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(store);

        var sale = invoice.Sale;
        var totals = sale.Totals;
        var composition = sale.TaxMode == TaxMode.Composition;
        var text = new StringBuilder();

        text.Append('*').Append(store.Name.Trim()).Append("*\n");

        var address = string.Join(", ", new[] { store.AddressLine1, store.AddressLine2 }.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!.Trim()));

        if (address.Length > 0)
            text.Append(address).Append('\n');

        if (!string.IsNullOrWhiteSpace(store.Gstin))
            text.Append("GSTIN ").Append(store.Gstin.Trim()).Append('\n');

        text.Append('*').Append(composition ? "BILL OF SUPPLY" : "TAX INVOICE").Append("* ").Append(invoice.InvoiceNo).Append('\n');
        text.Append(sale.CreatedAt.ToString("dd-MM-yyyy hh:mm tt", Figures)).Append('\n');

        if (sale.Buyer is { } buyer)
        {
            text.Append("Bill to: ").Append(buyer.Name).Append('\n');
            text.Append("Buyer GSTIN ").Append(buyer.Gstin).Append('\n');

            if (!string.IsNullOrWhiteSpace(buyer.Address))
                text.Append(buyer.Address.Trim()).Append('\n');

            text.Append("Place of supply: ").Append(GstStates.Label(buyer.StateCode)).Append('\n');
        }
        else if (sale.Customer is { } customer)
        {
            text.Append("Customer: ").Append(customer.Name is { } name ? $"{name} ({customer.MobileNo})" : customer.MobileNo).Append('\n');
        }

        text.Append("--------------------\n");

        foreach (var line in sale.Lines)
        {
            text.Append(line.NameSnapshot).Append('\n');
            text.Append("  ")
                .Append(Units.WithQuantity(line.Quantity, line.Unit, ReceiptLanguage.English))
                .Append(" x ").Append(Amount(line.Mrp));

            if (line.Discount > 0m)
                text.Append(" less ").Append(Amount(line.Discount));

            text.Append(" = ").Append(Amount(line.LineTotal)).Append('\n');

            text.Append("  HSN ").Append(line.HsnSnapshot);

            if (!composition)
                text.Append(", GST ").Append(Rate(line.GstRate)).Append('%');

            text.Append('\n');

            if (line.OfferName is { } offer)
                text.Append("  Offer: ").Append(offer).Append('\n');
        }

        text.Append("--------------------\n");

        if (!composition)
        {
            text.Append("Taxable value: ").Append(Amount(totals.SubtotalTaxable)).Append('\n');

            // The breakup by rate, as the paper carries it: what each slab was charged on, and the tax.
            foreach (var slab in sale.Lines
                         .GroupBy(l => l.GstRate)
                         .OrderBy(g => g.Key)
                         .Select(g => (Rate: g.Key, Taxable: Money.ToPresentation(g.Sum(l => l.Tax.TaxableValue)), Tax: Money.ToPresentation(g.Sum(l => l.Tax.SplitTax))))
                         .Where(s => s.Taxable > 0m || s.Tax > 0m))
            {
                text.Append("GST ").Append(Rate(slab.Rate)).Append("% on ").Append(Amount(slab.Taxable))
                    .Append(": ").Append(Amount(slab.Tax)).Append('\n');
            }

            if (totals.TotalCgst > 0m || totals.TotalSgst > 0m)
                text.Append("CGST ").Append(Amount(totals.TotalCgst)).Append(", SGST ").Append(Amount(totals.TotalSgst)).Append('\n');

            if (totals.TotalIgst > 0m)
                text.Append("IGST ").Append(Amount(totals.TotalIgst)).Append('\n');
        }

        if (totals.RoundOff != 0m)
            text.Append("Round off: ").Append(Amount(totals.RoundOff)).Append('\n');

        text.Append("*Total: Rs ").Append(Amount(totals.AmountPayable)).Append("*\n");

        var paid = sale.Payments
            .GroupBy(p => p.Type)
            .Select(g => $"{Tender(g.Key)} {Amount(g.Sum(p => p.Amount))}");

        text.Append("Paid: ").Append(string.Join(", ", paid));

        if (sale.ChangeDue > 0m)
            text.Append("; change ").Append(Amount(sale.ChangeDue));

        text.Append('\n');

        if (totals.TotalDiscount > 0m)
            text.Append("You saved Rs ").Append(Amount(totals.TotalDiscount)).Append('\n');

        if (sale.Customer is { } member && (sale.PointsEarned > 0 || sale.PointsRedeemed > 0))
        {
            text.Append("Points: ");

            if (sale.PointsRedeemed > 0)
                text.Append(sale.PointsRedeemed.ToString(Figures)).Append(" used, ");

            text.Append(sale.PointsEarned.ToString(Figures)).Append(" earned, ")
                .Append(member.LoyaltyBalance.ToString(Figures)).Append(" in all\n");
        }

        if (composition)
            text.Append(CompositionDeclaration.Text).Append('\n');

        text.Append(string.IsNullOrWhiteSpace(store.FooterMessage) ? "Thank you." : store.FooterMessage.Trim());

        return text.ToString();
    }

    /// <summary>
    /// The link that opens WhatsApp on this computer at the customer's chat with the bill typed in,
    /// for the cashier to send. An Indian mobile number of ten digits is given the country code.
    /// </summary>
    /// <returns>The link, or null for a number WhatsApp could not be opened at.</returns>
    public static string? WhatsAppLink(string mobile, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var digits = new string((mobile ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

        var phone = digits.Length switch
        {
            10 => "91" + digits,
            11 when digits[0] == '0' => "91" + digits[1..],
            12 when digits.StartsWith("91", StringComparison.Ordinal) => digits,
            _ => null,
        };

        return phone is null ? null : $"whatsapp://send?phone={phone}&text={Uri.EscapeDataString(text)}";
    }

    private static string Amount(decimal value) => value.ToString("N2", Figures);

    private static string Rate(decimal rate) => rate.ToString("0.##", Figures);

    private static string Tender(TenderType type) => type switch
    {
        TenderType.Cash => "Cash",
        TenderType.Card => "Card",
        TenderType.Upi => "UPI",
        TenderType.StoreCredit => "Khata",
        TenderType.LoyaltyPoints => "Points",
        _ => type.ToString(),
    };
}
