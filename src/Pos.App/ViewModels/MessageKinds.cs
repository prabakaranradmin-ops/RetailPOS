namespace Pos.App.ViewModels;

/// <summary>What a message on the till is: which decides its colour and its icon.</summary>
public enum MessageKind
{
    /// <summary>What to do next, or what just happened that needs nothing: the accent.</summary>
    Info,

    /// <summary>Done: a sale settled, a float recorded, a bill parked. Green.</summary>
    Done,

    /// <summary>Stopped until something else happens first, or done with a part that needs seeing to. Amber.</summary>
    Warning,

    /// <summary>Refused: the code matched nothing, the amount is not a number, something could not be read. Red.</summary>
    Error,
}

/// <summary>
/// Sorts the till's messages into kinds by what they say.
/// </summary>
/// <remarks>
/// <para>
/// The till used to say everything in one blue: "No item matches '999'" looked exactly like
/// "Toor Dal added", and "the receipt did not print" exactly like "bill parked as H001". The
/// messages already say plainly what happened, in a handful of set phrasings, so the kind is read
/// from those rather than threaded through a hundred and seventy places that set a message.
/// </para>
/// <para>
/// A problem outranks a success. "Settled for 189.00 ... WhatsApp did not open" is amber, not green:
/// the sale is done, but somebody has to paste the bill into a message by hand, and the colour is
/// what makes them read to the end.
/// </para>
/// </remarks>
public static class MessageKinds
{
    /// <summary>Something asked for could not be done at all.</summary>
    private static readonly string[] Refused =
    [
        "No item matches", "could not", "is not an amount", "is not a number", "is not a quantity",
        "No bill '", "No invoice found", "No quick key", "but no item has", "cannot be right",
        "cannot be for", "is not the PIN", "BACKUP FAILED",
    ];

    /// <summary>Held up until something else is done first, or done with a part to see to.</summary>
    private static readonly string[] Held =
    [
        // Upper case as well: the failures that must not be missed are written in capitals - "THE
        // RECEIPT DID NOT PRINT" - and matching only the lower case turned exactly those green.
        "did not print", "DID NOT PRINT", "did not open", "DID NOT OPEN", "not available on this lane", "no printer", "Finish ",
        "first", "needs ", "Only ", "already", "again to add", "again to confirm", "again to discard",
        "Press again", "again to close the day", "Not found:", "Nothing to ", "No parked bills", "No payment to remove", "No UPI ID",
        "has not billed", "cannot be voided", "was voided", "is no longer parked", "Park or discard",
        "Select a line", "owes nothing", "no khata to print", "Void ", "near its date", "past its date",
    ];

    /// <summary>What went through.</summary>
    private static readonly string[] Succeeded =
    [
        " settled for", " added.", " removed.", "parked as", "parked again", " saved", "recorded",
        "printed", "reprinted", " attached", " paid ", "Day closed", "hand back", "taken off",
        "redeemed", "is on the clipboard", "Float of", "discarded", "off the scale label",
        "put on the bill", " sent", "is a business", "Paid in full", "again to finish",
    ];

    public static MessageKind Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return MessageKind.Info;

        if (Refused.Any(phrase => message.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            return MessageKind.Error;

        if (Held.Any(phrase => message.Contains(phrase, StringComparison.Ordinal)))
            return MessageKind.Warning;

        if (Succeeded.Any(phrase => message.Contains(phrase, StringComparison.Ordinal)))
            return MessageKind.Done;

        return MessageKind.Info;
    }
}
