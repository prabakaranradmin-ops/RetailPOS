using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>Where giving a customer a GSTIN has got to.</summary>
public enum BusinessStage
{
    /// <summary>Their GSTIN: fifteen characters, checked as they are typed in.</summary>
    Gstin = 0,

    /// <summary>Their address, for the bill.</summary>
    Address = 1,
}

public sealed partial class BillingViewModel
{
    private BusinessStage _businessStage;
    private string? _pendingGstin;

    public bool IsSettingBusiness => _mode == BillingMode.Business;

    public BusinessStage BusinessStage
    {
        get => _businessStage;
        private set
        {
            if (Set(ref _businessStage, value))
                Raise(nameof(BusinessPrompt));
        }
    }

    public string BusinessPrompt
    {
        get
        {
            var who = _bill.Customer is { } customer ? customer.Name ?? customer.MobileNo : "The customer";

            return _businessStage == BusinessStage.Gstin
                ? $"{who}'s GSTIN, the fifteen characters on their certificate. Empty it and Enter to take a GSTIN off."
                : $"{who}'s address, as it should print on their bills (optional). Enter to save.";
        }
    }

    /// <summary>
    /// Ctrl+G: the customer on the bill is a business - their GSTIN, then their address. From then on
    /// their bills are tax invoices to a registered buyer, taxed by the state the GSTIN is in, and
    /// filed bill by bill in the return.
    /// </summary>
    public void SetBusiness()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (Mode != BillingMode.Billing)
        {
            StatusMessage = "Finish what is open first.";
            return;
        }

        if (_bill.Customer is not { } customer)
        {
            StatusMessage = "Attach the customer first with F7: a GSTIN goes on a customer, and every bill to them carries it.";
            return;
        }

        _pendingGstin = null;
        BusinessStage = BusinessStage.Gstin;
        Mode = BillingMode.Business;
        EditBuffer = customer.Gstin ?? string.Empty;
        Raise(nameof(BusinessPrompt));

        StatusMessage = customer.Gstin is { } gstin
            ? $"{customer.Name ?? customer.MobileNo} is a business, GSTIN {gstin}. Change it, or Enter to go on to the address."
            : $"{customer.Name ?? customer.MobileNo}'s GSTIN, to make this a bill to a business.";
    }

    private void CommitBusiness()
    {
        if (_bill.Customer is not { } customer)
        {
            BackOutOfBusiness();
            return;
        }

        if (_businessStage == BusinessStage.Gstin)
        {
            var typed = EditBuffer.Trim();

            if (typed.Length == 0)
            {
                if (customer.Gstin is null)
                {
                    StatusMessage = "Type their GSTIN, or Esc.";
                    return;
                }

                Save(customer, gstin: null, customer.Address);
                return;
            }

            if (Gstin.Problem(typed) is { } problem)
            {
                StatusMessage = problem;
                return;
            }

            _pendingGstin = Gstin.Normalise(typed);
            BusinessStage = BusinessStage.Address;
            EditBuffer = customer.Address ?? string.Empty;

            StatusMessage = $"GSTIN {_pendingGstin}, registered in {GstStates.Label(Gstin.StateCode(_pendingGstin))}. Now their address for the bill, or Enter to leave it.";
            return;
        }

        Save(customer, _pendingGstin, EditBuffer);
    }

    private void Save(Customer customer, string? gstin, string? address)
    {
        Customer updated;

        try
        {
            updated = _customers.SetBusiness(customer.Id, gstin, address);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            StatusMessage = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
            return;
        }

        CloseBusiness();

        // Attached again as they now are: their GSTIN's state decides how every line is taxed.
        _bill.SetCustomer(updated);

        foreach (var line in Lines)
            line.Refresh();

        RefreshTotals();
        RefreshCustomer();

        var who = updated.Name ?? updated.MobileNo;

        StatusMessage = updated.Gstin is { } registered
            ? $"{who} is a business: GSTIN {registered}, {GstStates.Label(Gstin.StateCode(registered))}. This bill is a tax invoice to them"
              + (_bill.Lines.Any(l => l.IsInterState) ? ", taxed as inter-state: IGST." : ".")
            : $"{who}'s GSTIN is taken off. Their bills are ordinary bills again.";
    }

    private void BackOutOfBusiness()
    {
        CloseBusiness();
        StatusMessage = "No GSTIN saved.";
    }

    private void CloseBusiness()
    {
        _pendingGstin = null;
        Mode = BillingMode.Billing;
        EditBuffer = string.Empty;
        BusinessStage = BusinessStage.Gstin;
    }
}
