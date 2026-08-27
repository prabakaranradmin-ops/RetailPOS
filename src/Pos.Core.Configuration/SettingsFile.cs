using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    /// Writes the dashboard PIN into the settings file, or removes it when given null.
    /// </summary>
    /// <remarks>
    /// When the file does not exist yet it is created in full from defaults, because a lane needs
    /// a lane id and a state code far more than it needs a PIN.
    /// </remarks>
    public static void SetDashboardPin(string path, PinCredential? credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            var fresh = new PosSettings();
            fresh.Security.DashboardPin = credential;
            fresh.Save(path);
            return;
        }

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException($"The settings file at '{path}' is not a JSON object.");

        if (credential is null)
        {
            // Take the whole section out when it is left empty, rather than leaving a hollow
            // "security": {} behind for somebody to wonder about.
            if (root["security"] is JsonObject existing)
            {
                existing.Remove("dashboardPin");

                if (existing.Count == 0)
                    root.Remove("security");
            }
        }
        else
        {
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
        }

        // With the byte-order mark, for the same reason PosSettings.Save writes one: a shopkeeper
        // opens this file in Notepad, and without the mark a Tamil store name comes back mangled.
        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
