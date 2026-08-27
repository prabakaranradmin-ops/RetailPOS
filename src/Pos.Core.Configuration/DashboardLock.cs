using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Pos.Core.Configuration;

/// <summary>
/// Settings that decide who may see what.
/// </summary>
public sealed class SecuritySettings
{
    /// <summary>
    /// Set to require a PIN before the dashboard will run. Absent means the dashboard is open to
    /// anyone who can use this computer, which is the right default for a single-owner shop.
    /// </summary>
    [JsonPropertyName("dashboardPin")]
    public PinCredential? DashboardPin { get; set; }

    /// <summary>Whether the dashboard is behind a PIN on this lane.</summary>
    [JsonIgnore]
    public bool DashboardIsLocked => DashboardPin?.IsUsable == true;
}

/// <summary>
/// A stored PIN — as a salted hash, never as the PIN.
/// </summary>
/// <remarks>
/// This sits in settings.json, which the person being kept out can read. That is not an oversight;
/// see <see cref="DashboardLock"/> for what this does and does not protect.
/// </remarks>
public sealed class PinCredential
{
    [JsonPropertyName("salt")]
    public string Salt { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("iterations")]
    public int Iterations { get; set; }

    [JsonIgnore]
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(Salt) && !string.IsNullOrWhiteSpace(Hash) && Iterations > 0;
}

/// <summary>
/// Putting a PIN in front of the dashboard.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this protects against, and what it does not.</b> This stops a cashier idly running
/// <c>pos dashboard</c> and reading the shop's turnover, margins and cost prices. That is the
/// realistic risk at a counter, and this removes it.
/// </para>
/// <para>
/// It is not a safe. On a lane where the cashier and the owner share one Windows login, the cashier
/// can open <c>pos.db</c> with any SQLite tool, or read a <c>dashboard.html</c> somebody left
/// behind, and this code never comes into it. A four-digit PIN is also only ten thousand guesses,
/// and the salt and hash are in a file the cashier can read — the iteration count below makes each
/// guess cost something, but somebody willing to write a script will still get there.
/// </para>
/// <para>
/// The only real separation is at the operating system: a second Windows account for the owner,
/// with the lane's data folder readable only by it. That is a deployment decision rather than a
/// code one, and it is written up in SETTINGS.md. This class is the proportionate measure for a
/// shop that is not going to do that.
/// </para>
/// <para>
/// Note this is a different thing from the shared password that <c>defaultCashierName</c>
/// deliberately avoids. That one would have been pretending to attribute a sale to a person; this
/// one only has to keep a door shut, and a door does not need to know who opened it.
/// </para>
/// </remarks>
public static class DashboardLock
{
    /// <summary>
    /// Short enough for a shopkeeper to type at a counter, long enough not to be guessed while
    /// somebody watches.
    /// </summary>
    public const int MinimumLength = 4;

    /// <summary>
    /// Deliberately expensive. The salt and hash are readable by the person being kept out, so the
    /// cost of a single guess is the only thing standing between them and an offline sweep of every
    /// four-digit PIN. At this count that sweep takes hours rather than seconds, on hardware a
    /// shop is likely to have. It costs the owner about a third of a second, once, per run.
    /// </summary>
    public const int Iterations = 600_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Turns a PIN into something safe to write down.</summary>
    /// <exception cref="ArgumentException">The PIN is too short or too obvious.</exception>
    public static PinCredential Create(string pin)
    {
        if (Rejection(pin) is { } reason)
            throw new ArgumentException(reason, nameof(pin));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(pin, salt, Iterations);

        return new PinCredential
        {
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            Iterations = Iterations,
        };
    }

    /// <summary>Whether this PIN matches the stored one.</summary>
    public static bool Verify(string? pin, PinCredential? credential)
    {
        if (credential is null || !credential.IsUsable || string.IsNullOrEmpty(pin))
            return false;

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(credential.Salt);
            expected = Convert.FromBase64String(credential.Hash);
        }
        catch (FormatException)
        {
            // A hand-edited or truncated credential. Refusing is the safe reading: the alternative
            // is a damaged file silently unlocking the thing it was meant to lock.
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
            return false;

        var actual = Derive(pin, salt, credential.Iterations, expected.Length);

        // Length-independent and content-independent comparison. The timing of a PIN check is not
        // much of a channel here, but there is no reason to hand it over either.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Why this PIN will not do, or null if it is fine.
    /// </summary>
    /// <remarks>
    /// The obvious-PIN rules earn their keep: a lock set to 0000 is worse than no lock, because the
    /// owner now believes the figures are private when anybody could open them first go.
    /// </remarks>
    public static string? Rejection(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
            return "The PIN is empty.";

        if (pin.Length != pin.Trim().Length)
            return "The PIN starts or ends with a space, which is too easy to mistype.";

        if (pin.Length < MinimumLength)
            return $"The PIN must be at least {MinimumLength} characters.";

        if (pin.Distinct().Count() == 1)
            return "Every character of that PIN is the same, which is among the first things anybody tries.";

        if (IsARun(pin))
            return "That PIN is a straight run of characters, which is among the first things anybody tries.";

        return null;
    }

    /// <summary>Whether the PIN is 1234, or 4321, or any other unbroken step in one direction.</summary>
    private static bool IsARun(string pin)
    {
        var step = pin[1] - pin[0];

        if (step is not (1 or -1))
            return false;

        for (var i = 2; i < pin.Length; i++)
        {
            if (pin[i] - pin[i - 1] != step)
                return false;
        }

        return true;
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations, int length = HashBytes) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin),
            salt,
            // A credential claiming zero or negative iterations would otherwise throw. It cannot
            // match anything anyway, so this only keeps the failure a refusal rather than a crash.
            Math.Max(1, iterations),
            HashAlgorithmName.SHA256,
            length);
}
