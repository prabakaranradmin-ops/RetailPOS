using Pos.Core.Domain;
using Pos.Core.Domain.Printing;

namespace Pos.App.ViewModels;

public sealed partial class BillingViewModel
{
    private bool _paperless;

    /// <summary>
    /// The shop as its bills name it, for the digital bill. Set by the composition root; without it
    /// there is no digital bill.
    /// </summary>
    public StoreProfile? Store { get; set; }

    /// <summary>
    /// Opens a link with whatever this computer has for it - WhatsApp, for a <c>whatsapp:</c> link.
    /// Set by the composition root; false when nothing opened it.
    /// </summary>
    public Func<string, bool>? OpenLink { get; set; }

    /// <summary>In the payment pane: this bill goes to the customer's WhatsApp instead of the printer.</summary>
    public bool IsPaperless
    {
        get => _paperless;
        private set
        {
            if (Set(ref _paperless, value))
                Raise(nameof(PaperlessLine));
        }
    }

    /// <summary>What the payment pane says while the bill is going digitally.</summary>
    public string PaperlessLine => _paperless && _bill.Customer is { } who
        ? $"No paper: the bill goes to {who.Name ?? who.MobileNo}'s WhatsApp. Ctrl+W again to print it after all."
        : string.Empty;

    /// <summary>
    /// Ctrl+W: a bill on the customer's phone. In the payment pane, this bill goes digitally instead
    /// of on paper; in Ctrl+P, the bill found is sent; with the bill empty, the last one is.
    /// </summary>
    public void SendDigitalBill()
    {
        switch (Mode)
        {
            case BillingMode.Tender:
                TogglePaperless();
                return;

            case BillingMode.Reprint:
                SendFoundBill();
                return;

            case BillingMode.Billing when _bill.IsEmpty && _lastSale is { } last:
                StatusMessage = SendDigitally(last.Invoice);
                return;

            default:
                StatusMessage = "Ctrl+W sends a bill to the customer's WhatsApp: after the sale, in F12 instead of paper, or in Ctrl+P for an older bill.";
                return;
        }
    }

    private void TogglePaperless()
    {
        if (_paperless)
        {
            IsPaperless = false;
            StatusMessage = "The bill will print as usual.";
            return;
        }

        if (_bill.Customer is null)
        {
            StatusMessage = "A bill on WhatsApp needs the customer's number: Esc, F7 to attach them, then F12 again.";
            return;
        }

        if (Store is null)
        {
            StatusMessage = "Digital bills are not available on this lane.";
            return;
        }

        IsPaperless = true;
        StatusMessage = PaperlessLine;
    }

    /// <summary>Ctrl+W in Ctrl+P: the bill typed - or the last one - to the customer's phone.</summary>
    private void SendFoundBill()
    {
        if (_invoices is null)
            return;

        var typed = EditBuffer.Trim();
        var invoice = typed.Length == 0
            ? _invoices.FindLatest(_laneId)
            : _invoices.FindByInvoiceNo(typed) ?? _invoices.FindLatestForMobile(typed);

        if (invoice is null)
        {
            StatusMessage = typed.Length == 0 ? "This lane has not billed anything yet." : $"No invoice found for '{typed}'.";
            return;
        }

        EditBuffer = string.Empty;
        Mode = BillingMode.Billing;
        StatusMessage = SendDigitally(invoice);
    }

    /// <summary>
    /// Opens WhatsApp at the customer's chat with the bill typed in, for the cashier to send; or, with
    /// no number or no WhatsApp on this computer, puts the bill on the clipboard.
    /// </summary>
    /// <returns>What happened, for the status line.</returns>
    private string SendDigitally(SettledInvoice invoice)
    {
        if (Store is not { } store)
            return "Digital bills are not available on this lane.";

        var text = DigitalBill.Text(invoice, store);
        var customer = invoice.Sale.Customer;

        if (customer is not null
            && DigitalBill.WhatsAppLink(customer.MobileNo, text) is { } link
            && OpenLink is { } open
            && TryOpen(open, link))
        {
            return $"{invoice.InvoiceNo} is in WhatsApp for {customer.Name ?? customer.MobileNo}: press Enter there to send it, then come back to the till.";
        }

        if (TryCopy(text))
        {
            return customer is null
                ? $"{invoice.InvoiceNo} is on the clipboard. There is no number on the bill: paste it wherever the customer wants it."
                : $"{invoice.InvoiceNo} is on the clipboard: WhatsApp did not open on this computer, so paste it into a message to {customer.MobileNo}.";
        }

        return $"{invoice.InvoiceNo} COULD NOT BE SENT: WhatsApp did not open and the clipboard was busy. Ctrl+P prints it.";
    }

    private static bool TryOpen(Func<string, bool> open, string link)
    {
        try
        {
            return open(link);
        }
        catch (Exception)
        {
            // No WhatsApp, or it refused the link: the clipboard is next.
            return false;
        }
    }
}
