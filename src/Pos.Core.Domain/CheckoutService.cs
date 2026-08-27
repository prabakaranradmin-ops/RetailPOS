using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.Core.Logging;
using Pos.Core.Loyalty;

namespace Pos.Core.Domain;

/// <summary>
/// What happened when a sale was completed.
/// </summary>
/// <param name="Invoice">The saved invoice, with the number it was given.</param>
/// <param name="ChangeDue">Cash to hand back.</param>
/// <param name="PointsRedeemed">Points spent on this sale.</param>
/// <param name="PointsEarned">Points accrued on the net bill.</param>
/// <param name="NewLoyaltyBalance">The customer's balance afterwards, or null for a walk-in.</param>
/// <param name="Drawer">
/// Whether the drawer opened. Reported, never fatal — see <see cref="CheckoutService"/>.
/// </param>
/// <param name="Print">Whether the receipt printed. Reported on the same terms as the drawer.</param>
public sealed record CheckoutResult(
    SettledInvoice Invoice,
    decimal ChangeDue,
    int PointsRedeemed,
    int PointsEarned,
    int? NewLoyaltyBalance,
    DrawerKickResult Drawer,
    PrintOutcome Print);

/// <summary>
/// Completes a sale: takes the tendered payments, applies loyalty, writes the invoice down and
/// kicks the drawer.
/// </summary>
/// <remarks>
/// Order matters here. The invoice is saved first and everything else follows, because a sale that
/// has been paid for must not be lost. A drawer that will not open, or a loyalty balance that
/// fails to write back, is a problem to report — not a reason to discard an invoice the customer
/// has already settled.
/// </remarks>
public sealed class CheckoutService(
    IInvoiceStore invoices,
    ICustomerStore customers,
    IDrawerService drawer,
    LoyaltyRules? loyaltyRules = null,
    TimeProvider? clock = null,
    IPrinterService? printer = null,
    ReceiptComposer? receipts = null,
    IPosLog? log = null,
    Func<string?>? cashier = null)
{
    private readonly IInvoiceStore _invoices = invoices ?? throw new ArgumentNullException(nameof(invoices));
    private readonly ICustomerStore _customers = customers ?? throw new ArgumentNullException(nameof(customers));
    private readonly IDrawerService _drawer = drawer ?? throw new ArgumentNullException(nameof(drawer));
    private readonly LoyaltyRules _loyaltyRules = loyaltyRules ?? LoyaltyRules.Default;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly IPrinterService _printer = printer ?? new NoPrinterService();
    private readonly ReceiptComposer? _receipts = receipts;
    private readonly IPosLog _log = log ?? NullLog.Instance;

    /// <summary>Who is on the till, read at the moment a sale completes rather than at startup.</summary>
    private readonly Func<string?> _cashier = cashier ?? (() => null);

    public LoyaltyRules LoyaltyRules => _loyaltyRules;

    /// <summary>
    /// Settles the bill. The basket must already cover the total; loyalty points, if any, are
    /// expected to be sitting in the basket as a <see cref="TenderType.LoyaltyPoints"/> payment.
    /// </summary>
    public CheckoutResult Complete(
        string laneId,
        InvoiceEngine bill,
        TenderBasket basket,
        int pointsRedeemed = 0,
        string? recalledFromToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(basket);

        if (bill.IsEmpty)
            throw new InvalidOperationException("There is nothing to settle — the bill has no lines.");

        var totals = bill.Totals;

        if (basket.AmountDue != totals.GrandTotal)
        {
            throw new InvalidOperationException(
                $"The payments were taken against {basket.AmountDue:0.00} but the bill comes to {totals.GrandTotal:0.00}.");
        }

        if (!basket.IsSettled)
            throw new InvalidOperationException($"{basket.Remaining:0.00} is still owed on this bill.");

        if (pointsRedeemed < 0)
            throw new ArgumentOutOfRangeException(nameof(pointsRedeemed), pointsRedeemed, "Cannot redeem a negative number of points.");

        var customer = bill.Customer;

        if (pointsRedeemed > 0 && customer is null)
            throw new InvalidOperationException("Points cannot be redeemed without a customer on the bill.");

        // Accrual is on the net bill — what the customer actually paid for after points came off —
        // so points spent on an invoice never earn points back (SRS section 4).
        var redemptionValue = basket.TotalOf(TenderType.LoyaltyPoints);
        var netBill = Math.Max(0m, totals.GrandTotal - redemptionValue);
        var pointsEarned = customer is null ? 0 : LoyaltyEngine.PointsEarned(netBill, _loyaltyRules);

        var sale = new SaleDraft(
            laneId,
            _clock.GetLocalNow(),
            customer,
            bill.SnapshotLines(),
            totals,
            basket.Tenders.ToList(),
            basket.ChangeDue,
            pointsRedeemed,
            pointsEarned,
            recalledFromToken,
            _cashier(),

            // Taken from the bill rather than from settings, so the mode recorded is the one the
            // lines were actually priced under. A bill parked before a mode change and settled
            // after it keeps what it was rung up as, which is the rule its prices already follow.
            bill.TaxMode);

        // The sale is durable from here on. Nothing below may throw the invoice away.
        var invoice = _invoices.Save(sale);

        _log.Info("sale", string.Join(
            "  ",
            invoice.InvoiceNo,
            $"total {totals.GrandTotal:0.00}",
            $"lines {totals.LineCount}",
            $"tenders [{string.Join(", ", basket.Tenders.Select(t => $"{t.Type} {t.Amount:0.00}"))}]",
            basket.ChangeDue > 0m ? $"change {basket.ChangeDue:0.00}" : "no change",
            sale.CashierName is { Length: > 0 } who ? $"by {who}" : "cashier not recorded"));

        int? newBalance = null;

        if (customer is not null)
        {
            newBalance = LoyaltyEngine.NewBalance(customer.LoyaltyBalance, pointsRedeemed, pointsEarned);
            _customers.UpdateLoyaltyBalance(customer.Id, newBalance.Value);
            customer.LoyaltyBalance = newBalance.Value;
        }

        var printResult = PrintReceipt(invoice);

        if (printResult.Status == PrintStatus.Failed)
            _log.Warn("printer", $"{invoice.InvoiceNo} did not print: {printResult.Detail}");

        var drawerResult = ShouldOpenDrawer(basket) ? _drawer.Kick() : DrawerKickResult.NoDrawerAttached;

        if (drawerResult == DrawerKickResult.Failed)
            _log.Warn("drawer", $"the drawer did not open on {invoice.InvoiceNo} ({_drawer.Name})");

        return new CheckoutResult(invoice, basket.ChangeDue, pointsRedeemed, pointsEarned, newBalance, drawerResult, printResult);
    }

    /// <summary>
    /// Prints the receipt for an invoice already on disk. Used for the reprint the cashier asks for
    /// when the paper jams, which is why it is public and why it marks the copy as a reprint.
    /// </summary>
    public PrintOutcome Reprint(SettledInvoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return PrintReceipt(invoice, isReprint: true);
    }

    /// <summary>
    /// Cancels a sale: marks the invoice void, puts any loyalty points back, and opens the drawer
    /// if there is cash to return.
    /// </summary>
    /// <remarks>
    /// The order is the same as everywhere else — the record changes first, then the peripherals
    /// and the balance follow. Putting the points back is not optional: a sale that no longer
    /// exists must not have spent or earned anything, and a customer whose points were taken by a
    /// mis-keyed bill will notice.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// If the invoice is already void, or has already been reported on a Z-report — a closed day is
    /// corrected with a credit note, not by changing a figure that has been filed.
    /// </exception>
    public VoidResult VoidSale(string invoiceNo, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNo);

        var voided = _invoices.Void(invoiceNo, _clock.GetLocalNow(), reason)
            ?? throw new InvalidOperationException($"There is no invoice numbered {invoiceNo}.");

        var reversed = false;
        int? newBalance = null;

        if (voided.Sale.Customer is { } customer && (voided.Sale.PointsRedeemed > 0 || voided.Sale.PointsEarned > 0))
        {
            // Undo the movement this sale caused: give back what it spent, take back what it
            // earned. Clamped at zero, because a balance can never go negative however the points
            // have moved since.
            newBalance = Math.Max(0, customer.LoyaltyBalance + voided.Sale.PointsRedeemed - voided.Sale.PointsEarned);
            _customers.UpdateLoyaltyBalance(customer.Id, newBalance.Value);
            customer.LoyaltyBalance = newBalance.Value;
            reversed = true;
        }

        // Cash taken on the original sale has to come back out of the drawer.
        if (_drawer.IsConfigured && voided.Sale.Payments.Any(p => p.Type == TenderType.Cash))
            _drawer.Kick();

        _log.Info("void", $"{voided.InvoiceNo} voided for {voided.GrandTotal:0.00}" +
            (reason is { Length: > 0 } ? $", reason: {reason}" : string.Empty) +
            (reversed ? $", loyalty back to {newBalance}" : string.Empty));

        return new VoidResult(voided, reversed, newBalance);
    }

    private PrintOutcome PrintReceipt(SettledInvoice invoice, bool isReprint = false)
    {
        if (_receipts is null || !_printer.IsConfigured)
            return PrintOutcome.NotConfigured();

        try
        {
            return _printer.Print(_receipts.Compose(invoice, isReprint).ToEscPos(raster: _printer.Raster));
        }
        catch (Exception ex)
        {
            // Composing a receipt should not be able to fail, but the invoice is already saved and
            // paid for. Whatever went wrong here, it is a message to the cashier and not an
            // exception thrown out of a completed sale.
            return PrintOutcome.Failed($"The receipt could not be produced: {ex.Message}");
        }
    }

    /// <summary>
    /// The drawer opens when cash changed hands (SRS 2.4) — including when cash was only part of a
    /// split tender, since the cashier still has notes to put away and change to take out.
    /// </summary>
    private bool ShouldOpenDrawer(TenderBasket basket) =>
        _drawer.IsConfigured && basket.Contains(TenderType.Cash);

    /// <summary>
    /// The largest redemption this customer may make against this bill, honouring both the
    /// scheme's cap and their balance.
    /// </summary>
    public LoyaltyRedemption QuoteRedemption(decimal grandTotal, Customer? customer) =>
        customer is null
            ? LoyaltyRedemption.None
            : LoyaltyEngine.Quote(grandTotal, customer.LoyaltyBalance, _loyaltyRules);

    /// <summary>Clamps a requested redemption to what the rules and the balance allow.</summary>
    public LoyaltyRedemption Redeem(decimal grandTotal, Customer? customer, int requestedPoints) =>
        customer is null
            ? LoyaltyRedemption.None
            : LoyaltyEngine.Redeem(grandTotal, customer.LoyaltyBalance, requestedPoints, _loyaltyRules);
}
