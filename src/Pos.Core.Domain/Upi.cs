using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pos.Core.Domain;

/// <summary>Where a UPI payment goes: the shop's UPI ID, and the name the customer's app shows.</summary>
/// <param name="Id">The UPI ID (VPA), such as <c>murugan.stores@okaxis</c>.</param>
/// <param name="Name">The payee name shown in the customer's app before they pay.</param>
/// <param name="MerchantCode">
/// The four-digit merchant category code, for a merchant UPI ID from the bank or a payments company
/// (5411 for a grocery). Null for a personal UPI ID.
/// </param>
public sealed partial record UpiPayee(string Id, string Name, string? MerchantCode = null)
{
    /// <summary>What is wrong with a UPI ID as typed, or null when it will do.</summary>
    public static string? Problem(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "A UPI ID is needed: the shop's, as the bank or the UPI app gives it, such as murugan.stores@okaxis.";

        return UpiIdShape().IsMatch(id.Trim())
            ? null
            : $"'{id.Trim()}' is not a UPI ID. It is a name or number, then @, then the bank or app, such as murugan.stores@okaxis.";
    }

    /// <summary>What is wrong with a merchant category code, or null when it will do or is not given.</summary>
    public static string? MerchantCodeProblem(string? code) =>
        string.IsNullOrWhiteSpace(code) || MerchantCodeShape().IsMatch(code.Trim())
            ? null
            : $"'{code.Trim()}' is not a merchant category code, which is four digits (5411 for a grocery).";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_]{1,255}@[A-Za-z][A-Za-z0-9]{1,63}$")]
    private static partial Regex UpiIdShape();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex MerchantCodeShape();
}

/// <summary>
/// A UPI payment request for an exact amount: the <c>upi://pay</c> link any UPI app opens when its
/// QR code is scanned, with the payee and the amount already filled in.
/// </summary>
/// <remarks>
/// <para>
/// A shop's printed QR stand asks the customer to type the amount, and a customer who types 40 for
/// 400 - or 4000 - is a problem at the counter either way. This code carries the amount; the
/// customer checks it and approves.
/// </para>
/// <para>
/// Nothing here touches the network. The link is text, the code is drawn on the till, and the
/// payment happens between the customer's phone and the bank. Whether it arrived is for the cashier
/// to see - on the customer's screen, or the shop's own app or sound box - before taking it.
/// </para>
/// </remarks>
public static class UpiLink
{
    /// <summary>The longest note (<c>tn</c>) sent. Apps truncate or refuse longer ones.</summary>
    public const int MostNote = 50;

    /// <summary>The link for this amount to this payee.</summary>
    /// <param name="note">A line the customer's app shows with the payment, such as a bill or order.</param>
    /// <param name="reference">
    /// A transaction reference, sent only for a merchant UPI ID - it is what the bank statement
    /// matches the payment by. Personal UPI IDs are sent none: some apps refuse a reference to one.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The amount is not more than nothing.</exception>
    public static string For(UpiPayee payee, decimal amount, string? note = null, string? reference = null)
    {
        ArgumentNullException.ThrowIfNull(payee);

        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A UPI request is for more than nothing.");

        var link = new StringBuilder("upi://pay?pa=")
            .Append(payee.Id.Trim())
            .Append("&pn=").Append(Escape(string.IsNullOrWhiteSpace(payee.Name) ? payee.Id.Trim() : payee.Name.Trim()));

        if (payee.MerchantCode is { } code && !string.IsNullOrWhiteSpace(code))
        {
            link.Append("&mc=").Append(code.Trim());

            if (!string.IsNullOrWhiteSpace(reference))
                link.Append("&tr=").Append(Escape(reference.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            var trimmed = note.Trim();
            link.Append("&tn=").Append(Escape(trimmed.Length > MostNote ? trimmed[..MostNote] : trimmed));
        }

        // Rupees to the paisa, with a point whatever the machine's language: 400.50, never 400,50.
        link.Append("&am=").Append(decimal.Round(amount, 2, MidpointRounding.ToEven).ToString("0.00", CultureInfo.InvariantCulture))
            .Append("&cu=INR");

        return link.ToString();
    }

    private static string Escape(string text) => Uri.EscapeDataString(text);
}
