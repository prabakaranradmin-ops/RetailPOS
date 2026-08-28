using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pos.Core.Domain;

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
