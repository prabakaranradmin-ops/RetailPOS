using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.Core.Logging;

namespace Pos.Core.Domain;

/// <param name="Payment">What was recorded.</param>
/// <param name="StillOwed">What the customer owes after it.</param>
/// <param name="Drawer">Whether the drawer opened for cash; <c>NoDrawerAttached</c> for card or UPI.</param>
/// <param name="Print">Whether the slip printed.</param>
public sealed record CollectionResult(CreditPayment Payment, decimal StillOwed, DrawerKickResult Drawer, PrintOutcome Print);

/// <summary>
/// Taking a customer's payment against what they owe on credit, at the counter.
/// </summary>
/// <remarks>
/// <para>
/// Shaped like <see cref="CheckoutService"/> on purpose: the money is recorded first, and the
/// drawer and the printer come after it. A drawer that will not open or a printer out of paper is
/// reported, never a reason to lose a payment that has been handed over - the customer is standing
/// there having paid, and the books must say so.
/// </para>
/// <para>
/// The log records the customer by id, not by name or number. The log is kept for years and is not
/// touched when a customer is forgotten, so it should not be where their details survive.
/// </para>
/// </remarks>
public sealed class CreditService(
    ICreditStore credit,
    IDrawerService drawer,
    TimeProvider? clock = null,
    IPrinterService? printer = null,
    ReceiptComposer? receipts = null,
    IPosLog? log = null,
    Func<string?>? cashier = null)
{
    private readonly ICreditStore _credit = credit ?? throw new ArgumentNullException(nameof(credit));
    private readonly IDrawerService _drawer = drawer ?? throw new ArgumentNullException(nameof(drawer));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly IPrinterService _printer = printer ?? new NoPrinterService();
    private readonly IPosLog _log = log ?? NullLog.Instance;

    /// <summary>What the customer owes now.</summary>
    public decimal Owed(Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        return _credit.Balance(customer.Id);
    }

    /// <summary>When the customer last paid anything back on the khata, or null if they never have.</summary>
    public DateTimeOffset? LastPaid(Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        return _credit.LastPaid(customer.Id);
    }

    /// <summary>
    /// Records the payment, then opens the drawer for cash and prints the customer's slip.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing owed, or more than is owed.</exception>
    /// <exception cref="ArgumentException">Not cash, card or UPI, or not a sensible amount.</exception>
    public CollectionResult Collect(Customer customer, decimal amount, TenderType tender, string laneId)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var payment = _credit.Collect(customer.Id, amount, tender, laneId, _clock.GetLocalNow(), cashier?.Invoke());
        var stillOwed = _credit.Balance(customer.Id);

        _log.Info("credit", $"customer {customer.Id} paid {amount:0.00} by {tender} on {laneId}; {stillOwed:0.00} still owed");

        var drawerResult = tender == TenderType.Cash && _drawer.IsConfigured
            ? _drawer.Kick()
            : DrawerKickResult.NoDrawerAttached;

        if (drawerResult == DrawerKickResult.Failed)
            _log.Warn("drawer", $"the drawer did not open for a credit payment ({_drawer.Name})");

        return new CollectionResult(payment, stillOwed, drawerResult, PrintSlip(customer, payment, stillOwed));
    }

    /// <summary>
    /// The statement a customer asks for at the counter: everything since they last owed nothing,
    /// up to today, closing on what they owe now.
    /// </summary>
    public KhataStatement Statement(Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        return KhataStatement.SinceLastClear(customer, _credit.Ledger(customer.Id), today);
    }

    /// <summary>Prints a statement, with a UPI code for what is owed when the shop has an ID.</summary>
    public PrintOutcome PrintStatement(KhataStatement statement, UpiPayee? upi)
    {
        ArgumentNullException.ThrowIfNull(statement);

        if (receipts is null || !_printer.IsConfigured)
            return PrintOutcome.NotConfigured();

        try
        {
            var outcome = _printer.Print(receipts.ComposeKhataStatement(statement, upi).ToEscPos(raster: _printer.Raster));
            _log.Info("credit", $"statement for customer {statement.Customer.Id} printed: {Plural.Of(statement.Lines.Count, "line")}, {statement.Closing:0.00} owed");
            return outcome;
        }
        catch (Exception ex)
        {
            _log.Error("printer", "a khata statement did not print", ex);
            return PrintOutcome.Failed(ex.Message);
        }
    }

    private PrintOutcome PrintSlip(Customer customer, CreditPayment payment, decimal stillOwed)
    {
        if (receipts is null || !_printer.IsConfigured)
            return PrintOutcome.NotConfigured();

        try
        {
            return _printer.Print(receipts.ComposeCollection(customer, payment, stillOwed).ToEscPos(raster: _printer.Raster));
        }
        catch (Exception ex)
        {
            // The payment is already in the books. A slip that did not print is a message on the
            // screen and a reprint later, not a lost payment.
            _log.Error("printer", "the credit payment slip did not print", ex);
            return PrintOutcome.Failed(ex.Message);
        }
    }
}
