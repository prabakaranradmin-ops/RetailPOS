using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;

namespace Pos.Core.Configuration;

/// <summary>
/// Changing one thing in settings.json without disturbing the rest of it.
/// </summary>
/// <remarks>
/// Round-tripping through <see cref="PosSettings"/> and saving would work, but it rewrites the
/// whole file: every default becomes explicit, the order changes, and anything the shopkeeper put
/// there that this build does not know about disappears. A command that was asked to set a PIN
/// should set a PIN.
/// </remarks>
public static class SettingsFile
{
    /// <summary>
    /// Writes what kind of bill this lane issues into the settings file.
    /// </summary>
    /// <remarks>
    /// Written by name, not by number: a shopkeeper opening this file should see
    /// <c>"taxMode": "Composition"</c>, not a 1 they have to look up.
    /// </remarks>
    public static void SetTaxMode(string path, TaxMode mode) =>
        Patch(path, root => root["taxMode"] = mode.ToString(), fresh => fresh.TaxMode = mode);

    /// <summary>Writes the share of full at which an item counts as low.</summary>
    public static void SetLowStockPercent(string path, decimal percent)
    {
        if (!LowStock.IsValidPercent(percent))
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Use 0 to switch it off, or a share below 100.");

        Patch(path, root => root["lowStockPercent"] = percent, fresh => fresh.LowStockPercent = percent);
    }

    /// <summary>Writes how many days an order is meant to last.</summary>
    public static void SetOrderCoverDays(string path, int days)
    {
        if (!Reorder.IsValidCoverDays(days))
            throw new ArgumentOutOfRangeException(nameof(days), days, "An order covers between 1 and 120 days.");

        Patch(path, root => root["orderCoverDays"] = days, fresh => fresh.OrderCoverDays = days);
    }

    /// <summary>
    /// Writes the shop's UPI ID, or takes it out when given nothing - which turns the code with the
    /// amount off. The payee name and merchant code, set in the file by hand if at all, are left.
    /// </summary>
    public static void SetUpiId(string path, string? id)
    {
        var value = string.IsNullOrWhiteSpace(id) ? null : id.Trim();

        if (value is not null && UpiPayee.Problem(value) is { } problem)
            throw new ArgumentException(problem, nameof(id));

        Patch(
            path,
            root =>
            {
                if (root["upi"] is not JsonObject upi)
                {
                    if (value is null)
                        return;

                    upi = new JsonObject();
                    root["upi"] = upi;
                }

                if (value is null)
                {
                    upi.Remove("id");

                    if (upi.Count == 0)
                        root.Remove("upi");

                    return;
                }

                upi["id"] = value;
            },
            fresh => fresh.Upi.Id = value);
    }

    /// <summary>
    /// Writes the list of cashiers and their PINs - as salted hashes, never the PINs - or takes the
    /// list out when it is empty.
    /// </summary>
    public static void SetCashiers(string path, IReadOnlyList<CashierSettings> cashiers)
    {
        ArgumentNullException.ThrowIfNull(cashiers);

        if (CashierRules.Problem(cashiers) is { } problem)
            throw new ArgumentException(problem, nameof(cashiers));

        Patch(
            path,
            root =>
            {
                if (cashiers.Count == 0)
                {
                    root.Remove("cashiers");
                    return;
                }

                var list = new JsonArray();

                foreach (var cashier in cashiers)
                {
                    list.Add(new JsonObject
                    {
                        ["name"] = cashier.Name.Trim(),
                        ["pin"] = new JsonObject
                        {
                            ["salt"] = cashier.Pin!.Salt,
                            ["hash"] = cashier.Pin.Hash,
                            ["iterations"] = cashier.Pin.Iterations,
                        },
                    });
                }

                root["cashiers"] = list;
            },
            fresh => fresh.Cashiers = [.. cashiers]);
    }

    /// <summary>Writes which of the till's actions wait for the owner's PIN.</summary>
    public static void SetApprovals(string path, ApprovalSettings approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);

        if (approvals.Problem() is { } problem)
            throw new ArgumentException(problem, nameof(approvals));

        Patch(
            path,
            root => root["approvals"] = new JsonObject
            {
                ["voids"] = approvals.Voids,
                ["discountAbovePercent"] = approvals.DiscountAbovePercent,
                ["cashRefunds"] = approvals.CashRefunds,
                ["cashOut"] = approvals.CashOut,
                ["closeDay"] = approvals.CloseDay,
            },
            fresh => fresh.Approvals = approvals.Copy());
    }

    /// <summary>Writes which bill layout this lane prints, by name.</summary>
    public static void SetReceiptLayout(string path, ReceiptLayout layout) =>
        Patch(path, root => root["receiptLayout"] = layout.ToString(), fresh => fresh.ReceiptLayout = layout);

    /// <summary>Writes how this lane's screens look, by name.</summary>
    public static void SetScreenTheme(string path, ScreenTheme theme)
    {
        if (!Enum.IsDefined(theme))
            throw new ArgumentOutOfRangeException(nameof(theme), theme, "Not a look this build has.");

        Patch(path, root => root["screenTheme"] = theme.ToString(), fresh => fresh.ScreenTheme = theme);
    }

    /// <summary>
    /// Writes the dashboard PIN into the settings file, or removes it when given null.
    /// </summary>
    /// <remarks>
    /// When the file does not exist yet it is created in full from defaults, because a lane needs
    /// a lane id and a state code far more than it needs a PIN.
    /// </remarks>
    public static void SetDashboardPin(string path, PinCredential? credential) =>
        Patch(
            path,
            root =>
            {
                if (credential is null)
                {
                    // Take the whole section out when it is left empty, rather than leaving a
                    // hollow "security": {} behind for somebody to wonder about.
                    if (root["security"] is JsonObject existing)
                    {
                        existing.Remove("dashboardPin");

                        if (existing.Count == 0)
                            root.Remove("security");
                    }

                    return;
                }

                if (root["security"] is not JsonObject security)
                {
                    security = new JsonObject();
                    root["security"] = security;
                }

                security["dashboardPin"] = new JsonObject
                {
                    ["salt"] = credential.Salt,
                    ["hash"] = credential.Hash,
                    ["iterations"] = credential.Iterations,
                };
            },
            fresh => fresh.Security.DashboardPin = credential);

    /// <summary>
    /// Applies one change to the settings file, leaving everything else exactly as written.
    /// </summary>
    /// <param name="edit">The change, against the file's own JSON.</param>
    /// <param name="onFresh">
    /// The same change against a defaults object, for a lane that has no settings file yet. It is
    /// then written in full, because a lane needs a lane id and a state code far more than it needs
    /// whichever setting was being changed.
    /// </param>
    private static void Patch(string path, Action<JsonObject> edit, Action<PosSettings> onFresh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            var fresh = new PosSettings();
            onFresh(fresh);
            fresh.Save(path);
            return;
        }

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException($"The settings file at '{path}' is not a JSON object.");

        edit(root);

        // With the byte-order mark, for the same reason PosSettings.Save writes one: a shopkeeper
        // opens this file in Notepad, and without the mark a Tamil store name comes back mangled.
        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
