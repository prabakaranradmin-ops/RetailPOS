using System.Globalization;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>Where taking an order has got to.</summary>
public enum OrderStage
{
    /// <summary>The bill is empty: the order is typed or pasted in, one item a line.</summary>
    Paste = 0,

    /// <summary>The bill is the order: how it came in, and where it is going or when.</summary>
    Save = 1,
}

public sealed partial class BillingViewModel
{
    private OrderStage _orderStage;
    private int _selectedOrderKindIndex;

    /// <summary>The order a recalled bill was parked as, so parking it again keeps it an order.</summary>
    private OrderInfo? _recalledOrder;

    public bool IsTakingOrder => _mode == BillingMode.Order;

    public OrderStage OrderStage
    {
        get => _orderStage;
        private set
        {
            if (!Set(ref _orderStage, value))
                return;

            Raise(nameof(IsPastingOrder));
            Raise(nameof(IsSavingOrder));
            Raise(nameof(OrderPrompt));
        }
    }

    public bool IsPastingOrder => _orderStage == OrderStage.Paste;

    public bool IsSavingOrder => _orderStage == OrderStage.Save;

    public IReadOnlyList<string> OrderKindLabels { get; } = ["Phone", "WhatsApp"];

    public int SelectedOrderKindIndex
    {
        get => _selectedOrderKindIndex;
        private set => Set(ref _selectedOrderKindIndex, value);
    }

    public string OrderPrompt => _orderStage == OrderStage.Paste
        ? "Type or paste the order, one item a line - \"2 kg sugar\", \"toor dal 1kg x 2\". Shift+Enter starts a new line; Enter reads the order."
        : "Where it is going, or when it will be collected (optional). Up and down: phone or WhatsApp. Enter to save the order.";

    /// <summary>The shop's name, for the message that confirms an order to the customer.</summary>
    public string ShopName { get; set; } = string.Empty;

    /// <summary>Puts text on the clipboard. Set by the composition root; null leaves nothing copied.</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>
    /// Orders over the phone or on WhatsApp. With the bill empty: type or paste the order and it goes
    /// on the bill. With a bill on screen: save it as an order, parked until it is paid for.
    /// </summary>
    public void TakeOrder()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (Mode != BillingMode.Billing)
        {
            StatusMessage = "Finish what is open first.";
            return;
        }

        if (_bill.IsEmpty)
        {
            OrderStage = OrderStage.Paste;
            Mode = BillingMode.Order;
            EditBuffer = string.Empty;
            StatusMessage = "Paste the customer's message, or type what they asked for.";
            return;
        }

        // Somebody has to be told when it is ready, and somebody has to be asked for the money.
        if (_bill.Customer is null)
        {
            StatusMessage = "Attach the customer first with F7 - an order needs somebody to tell when it is ready.";
            return;
        }

        SelectedOrderKindIndex = _recalledOrder?.Kind == OrderKind.WhatsApp ? 1 : 0;
        OrderStage = OrderStage.Save;
        Mode = BillingMode.Order;
        EditBuffer = _recalledOrder?.Note ?? string.Empty;
        StatusMessage = $"Save this bill as an order for {CustomerLabel}.";
    }

    private void CommitOrder()
    {
        if (_orderStage == OrderStage.Paste)
            PutOrderOnBill();
        else
            SaveOrder();
    }

    /// <summary>Reads what was typed or pasted and puts each line it finds on the bill.</summary>
    private void PutOrderOnBill()
    {
        var lines = OrderText.Parse(EditBuffer);

        if (lines.Count == 0)
        {
            StatusMessage = "Nothing to read. Type or paste the order, one item a line.";
            return;
        }

        var found = new List<(Item Item, decimal Quantity)>();
        var missing = new List<string>();

        foreach (var line in lines)
        {
            try
            {
                if (OrderText.Resolve(line, text => _items.Search(text)) is { } match)
                    found.Add(match);
                else
                    missing.Add(line.Asked);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                missing.Add(line.Asked);
            }
        }

        CloseOrder();

        foreach (var (item, quantity) in found)
        {
            try
            {
                AddItem(item, quantity);
            }
            catch (ArgumentOutOfRangeException)
            {
                missing.Add(item.Name);
            }
        }

        var said = $"{found.Count} of {Plural.Of(lines.Count, "line")} of the order put on the bill - check each against the message.";

        if (missing.Count > 0)
            said += $" Not found: {string.Join("; ", missing.Take(5))}{(missing.Count > 5 ? $" and {missing.Count - 5} more" : "")}.";

        // The misses stay under the bill until the order is saved or put aside. In the message bar
        // alone, "mangoes 2 kg" was gone at the next keystroke - usually F7 for the customer.
        OrderMisses = missing;

        StatusMessage = said + " Then F7 for the customer, and Ctrl+O to save it as an order.";
    }

    /// <summary>What the last order asked for that is not on the bill: nothing matched it.</summary>
    public IReadOnlyList<string> OrderMisses
    {
        get => _orderMisses;
        private set
        {
            _orderMisses = value;
            Raise(nameof(OrderMisses));
            Raise(nameof(HasOrderMisses));
            Raise(nameof(OrderMissesLine));
        }
    }

    private IReadOnlyList<string> _orderMisses = [];

    public bool HasOrderMisses => _orderMisses.Count > 0;

    /// <summary>The misses as one line, for the panel under the bill.</summary>
    public string OrderMissesLine => string.Join("  ·  ", _orderMisses);

    private void ForgetOrderMisses()
    {
        if (_orderMisses.Count > 0)
            OrderMisses = [];
    }

    private void SaveOrder()
    {
        var order = new OrderInfo(_selectedOrderKindIndex == 1 ? OrderKind.WhatsApp : OrderKind.Phone, EditBuffer.Trim() is { Length: > 0 } note ? note : null);
        var customer = _bill.Customer!;
        var items = _bill.Lines.Count;
        var total = GrandTotal;

        string token;

        try
        {
            token = _heldBills.NextToken(_laneId);
            _heldBills.Park(_laneId, token, _now(), customer, _bill.SnapshotLines(), order);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            StatusMessage = $"The order could not be saved: {ex.Message}";
            return;
        }

        CloseOrder();
        _recalledOrder = null;
        ForgetOrderMisses();
        ClearBill();
        RefreshHeldBills();

        var message = OrderMessage(token, items, total, order);
        var copied = false;

        try
        {
            if (CopyText is { } copy)
            {
                copy(message);
                copied = true;
            }
        }
        catch (Exception)
        {
            // Another program holding the clipboard costs the message, not the order.
        }

        StatusMessage = $"Order {token} saved for {customer.Name ?? customer.MobileNo}, {Show.Money(total)}. It waits in F6 until it is paid for."
                        + (copied ? " A message for them is on the clipboard." : string.Empty);
    }

    /// <summary>The message confirming the order, to paste into a reply to the customer.</summary>
    public string OrderMessage(string token, int items, decimal total, OrderInfo order)
    {
        var shop = string.IsNullOrWhiteSpace(ShopName) ? "the shop" : ShopName.Trim();
        var message = $"Your order at {shop}: {Plural.Of(items, "item")}, Rs {total.ToString("N2", CultureInfo.InvariantCulture)}.";

        if (order.Note?.TrimEnd('.', ' ') is { Length: > 0 } note)
            message += $" {note}.";

        return message + $" We will let you know when it is ready. Order {token}.";
    }

    private void MoveInOrder(int delta)
    {
        if (_orderStage == OrderStage.Save)
            SelectedOrderKindIndex = Math.Clamp(_selectedOrderKindIndex + delta, 0, OrderKindLabels.Count - 1);
    }

    private void BackOutOfOrder()
    {
        CloseOrder();
        StatusMessage = "No order taken.";
    }

    private void CloseOrder()
    {
        Mode = BillingMode.Billing;
        EditBuffer = string.Empty;
        OrderStage = OrderStage.Paste;
    }
}
