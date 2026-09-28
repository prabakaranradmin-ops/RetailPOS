namespace Pos.Core.Domain;

/// <summary>
/// The two-digit state codes GST uses, and the names a return prints beside them.
/// </summary>
/// <remarks>
/// A place of supply is written "33-Tamil Nadu" in a return, code and name together, so the list is
/// kept in that form. Codes that no longer name anything (25, the old Daman and Diu; 28, the old
/// Andhra Pradesh) are still here, because a customer record written years ago may carry one.
/// </remarks>
public static class GstStates
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["01"] = "Jammu and Kashmir",
        ["02"] = "Himachal Pradesh",
        ["03"] = "Punjab",
        ["04"] = "Chandigarh",
        ["05"] = "Uttarakhand",
        ["06"] = "Haryana",
        ["07"] = "Delhi",
        ["08"] = "Rajasthan",
        ["09"] = "Uttar Pradesh",
        ["10"] = "Bihar",
        ["11"] = "Sikkim",
        ["12"] = "Arunachal Pradesh",
        ["13"] = "Nagaland",
        ["14"] = "Manipur",
        ["15"] = "Mizoram",
        ["16"] = "Tripura",
        ["17"] = "Meghalaya",
        ["18"] = "Assam",
        ["19"] = "West Bengal",
        ["20"] = "Jharkhand",
        ["21"] = "Odisha",
        ["22"] = "Chhattisgarh",
        ["23"] = "Madhya Pradesh",
        ["24"] = "Gujarat",
        ["25"] = "Daman and Diu",
        ["26"] = "Dadra and Nagar Haveli and Daman and Diu",
        ["27"] = "Maharashtra",
        ["28"] = "Andhra Pradesh (Before Division)",
        ["29"] = "Karnataka",
        ["30"] = "Goa",
        ["31"] = "Lakshadweep",
        ["32"] = "Kerala",
        ["33"] = "Tamil Nadu",
        ["34"] = "Puducherry",
        ["35"] = "Andaman and Nicobar Islands",
        ["36"] = "Telangana",
        ["37"] = "Andhra Pradesh",
        ["38"] = "Ladakh",
        ["97"] = "Other Territory",
    };

    /// <summary>The state's name, or null for a code GST does not use.</summary>
    public static string? Name(string? code) =>
        code is not null && Names.TryGetValue(Normalise(code), out var name) ? name : null;

    /// <summary>"33-Tamil Nadu", as a return writes a place of supply. The bare code when it is unknown.</summary>
    public static string Label(string code) =>
        Name(code) is { } name ? $"{Normalise(code)}-{name}" : code;

    /// <summary>"3" and "33 " are how a hand-edited settings file says 03 and 33.</summary>
    private static string Normalise(string code) => code.Trim().PadLeft(2, '0');
}
