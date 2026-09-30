using System.Text.Json.Serialization;
using Pos.Core.Domain;

namespace Pos.Core.Configuration;

/// <summary>
/// Where UPI payments go, for the QR code with the exact amount shown and printed at the till.
/// </summary>
/// <remarks>
/// All three are optional. With no <see cref="Id"/> there is no code, and UPI is taken as it always
/// was. The name defaults to the store's; the merchant code is for a merchant UPI ID from the bank or
/// a payments company, and makes the link carry a reference the bank statement shows.
/// </remarks>
public sealed class UpiSettings
{
    /// <summary>The shop's UPI ID (VPA), such as <c>murugan.stores@okaxis</c>.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>The name the customer's app shows. The store's name when not given.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The four-digit merchant category code, for a merchant UPI ID; 5411 for a grocery.</summary>
    [JsonPropertyName("merchantCode")]
    public string? MerchantCode { get; set; }

    [JsonIgnore]
    public bool IsSet => !string.IsNullOrWhiteSpace(Id);

    /// <summary>What is wrong with the section, or null when it is usable or not set.</summary>
    /// <remarks>
    /// A name or merchant code with no ID is not a problem: it is what is left when the owner takes
    /// the ID out from Settings to turn the code off, and it waits there for the ID to come back.
    /// </remarks>
    public string? Problem() => IsSet ? UpiPayee.Problem(Id) ?? UpiPayee.MerchantCodeProblem(MerchantCode) : null;

    /// <summary>The payee the till's codes are made out to, or null when no UPI ID is set.</summary>
    public UpiPayee? ToPayee(string? storeName)
    {
        if (!IsSet || Problem() is not null)
            return null;

        var name = !string.IsNullOrWhiteSpace(Name)
            ? Name.Trim()
            : string.IsNullOrWhiteSpace(storeName) ? Id!.Trim() : storeName.Trim();

        return new UpiPayee(Id!.Trim(), name, string.IsNullOrWhiteSpace(MerchantCode) ? null : MerchantCode.Trim());
    }
}
