using System.Globalization;
using Pos.Core.Domain;
using Pos.Core.Hardware.Printing;

namespace Pos.App.ViewModels;

public sealed partial class BillingViewModel
{
    /// <summary>
    /// Ctrl+K: the customer's khata statement - everything since they last owed nothing - printed,
    /// with a message for them put on the clipboard to send.
    /// </summary>
    /// <remarks>
    /// For the customer picked in F8, or the one attached to the bill. Printing it changes nothing:
    /// it is read from the same books as what they owe.
    /// </remarks>
    public void PrintKhataStatement()
    {
        if (_credit is null)
        {
            StatusMessage = "The khata is not available on this lane.";
            return;
        }

        var customer = Mode switch
        {
            BillingMode.Collect => _collectCustomer,
            BillingMode.Billing => _bill.Customer,
            _ => null,
        };

        if (customer is null)
        {
            StatusMessage = Mode == BillingMode.Collect
                ? "Pick the customer first, then Ctrl+K prints their statement."
                : "Ctrl+K prints a customer's khata statement: F8 and pick them, or attach them to the bill with F7.";
            return;
        }

        var who = customer.Name ?? customer.MobileNo;
        KhataStatement statement;

        try
        {
            statement = _credit.Statement(customer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            StatusMessage = $"{who}'s khata could not be read: {ex.Message}";
            return;
        }

        if (statement.Lines.Count == 0 && statement.Closing == 0m)
        {
            StatusMessage = $"{who} has nothing on the khata: there is no statement to print.";
            return;
        }

        var owed = Show.Money(Math.Max(0m, statement.Closing));
        var since = statement.Ageing.OldestUnpaid is { } oldest
            ? $", the oldest bill from {Show.Date(oldest)}"
            : string.Empty;

        var printed = _credit.PrintStatement(statement, _upi);
        var copied = TryCopy(statement.Message(ShopName, _upi));

        StatusMessage = printed.Status switch
        {
            PrintStatus.Printed => $"{who}'s statement printed: {owed} owed{since}.",
            PrintStatus.NoPrinterConfigured => $"No printer on this lane. {who} owes {owed}{since}.",
            _ => $"THE STATEMENT DID NOT PRINT: {printed.Detail}. {who} owes {owed}.",
        } + (copied ? " A message for them is on the clipboard." : string.Empty);
    }

    /// <summary>Puts text on the clipboard, if the lane can; false when nothing was copied.</summary>
    private bool TryCopy(string text)
    {
        try
        {
            if (CopyText is not { } copy)
                return false;

            copy(text);
            return true;
        }
        catch (Exception)
        {
            // Another program holding the clipboard costs the message, not what was being done.
            return false;
        }
    }
}
