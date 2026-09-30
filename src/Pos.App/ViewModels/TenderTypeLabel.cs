using System.Globalization;
using System.Windows.Data;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

public static class TenderTypeLabel
{
    /// <summary>
    /// How a tender is named on screen. The enum's own names ("Upi", "StoreCredit") are C#
    /// identifiers, not something to show a cashier.
    /// </summary>
    /// <remarks>
    /// Money a customer owes the shop is "khata" everywhere, the word the shop already uses. It
    /// was "Store credit" here, which in Indian retail usually means the opposite - money the
    /// shop owes a customer after a return - and "Credit", "khata" and "Payment on credit"
    /// elsewhere: four names for one thing, and a new cashier could have picked it to refund
    /// somebody.
    /// </remarks>
    public static string DisplayName(this TenderType type) => type switch
    {
        TenderType.Cash => "Cash",
        TenderType.Card => "Card",
        TenderType.Upi => "UPI",
        TenderType.StoreCredit => "Khata (pay later)",
        TenderType.LoyaltyPoints => "Loyalty points",
        _ => type.ToString(),
    };
}

/// <summary>Binds <see cref="TenderTypeLabel.DisplayName"/> into the tender pane.</summary>
public sealed class TenderTypeLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TenderType type ? type.DisplayName() : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
