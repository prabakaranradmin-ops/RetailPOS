using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Core.Configuration;

/// <summary>
/// How the till and the owner's screen look: one of four looks for the light the shop is in, or
/// whichever suits the hour.
/// </summary>
/// <remarks>
/// The stored numbers are what a settings file may hold, so a look is added at the end and never
/// renumbered.
/// </remarks>
public enum ScreenTheme
{
    /// <summary>Dark, for a shop lit by its own lights. The look a lane has unless the owner chooses another.</summary>
    Night = 0,

    /// <summary>Dim, for dusk: neither glaring against a darkening shop nor sinking into it.</summary>
    Evening = 1,

    /// <summary>Light and soft, for a shop open to the daylight.</summary>
    Morning = 2,

    /// <summary>The brightest and firmest, for sunlight on the screen at midday.</summary>
    Noon = 3,

    /// <summary>Morning, Noon, Evening or Night by the clock, changing on its own through the day.</summary>
    ByTimeOfDay = 4,
}

/// <summary>Which look suits which hour.</summary>
/// <remarks>
/// Fixed hours rather than sunrise and sunset worked out for the shop's town: across Tamil Nadu the
/// sun rises near six and sets near half past six all year, closer together than any shop's opening
/// hours, and a look that moved by a few minutes a day would be one more thing for a shopkeeper to
/// wonder about.
/// </remarks>
public static class ScreenThemes
{
    /// <summary>When the morning look begins.</summary>
    public static readonly TimeOnly MorningFrom = new(6, 0);

    /// <summary>When the noon look begins: the hours the sun is strongest on a counter near the door.</summary>
    public static readonly TimeOnly NoonFrom = new(11, 0);

    /// <summary>When the evening look begins, as the daylight goes.</summary>
    public static readonly TimeOnly EveningFrom = new(16, 0);

    /// <summary>When the night look begins, and lasts until the morning.</summary>
    public static readonly TimeOnly NightFrom = new(19, 0);

    /// <summary>The look for a time of day.</summary>
    public static ScreenTheme At(TimeOnly time) =>
        time >= NightFrom || time < MorningFrom ? ScreenTheme.Night
        : time >= EveningFrom ? ScreenTheme.Evening
        : time >= NoonFrom ? ScreenTheme.Noon
        : ScreenTheme.Morning;

    /// <summary>The look to show: the one chosen, or the hour's when the choice is to follow the clock.</summary>
    public static ScreenTheme Resolve(ScreenTheme chosen, TimeOnly now) =>
        chosen == ScreenTheme.ByTimeOfDay ? At(now) : chosen;
}

/// <summary>
/// Reads a look from the settings file, and reads one this build does not know as Night.
/// </summary>
/// <remarks>
/// Forgiving on purpose, unlike the rest of the file. A misspelt tax mode or lane has to stop the
/// lane, because bills would come out wrong; a misspelt look only changes colours, and is never a
/// reason for a till not to bill.
/// </remarks>
internal sealed class ScreenThemeConverter : JsonConverter<ScreenTheme>
{
    public override ScreenTheme Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse<ScreenTheme>(reader.GetString(), ignoreCase: true, out var named)
            && Enum.IsDefined(named))
        {
            return named;
        }

        if (reader.TokenType == JsonTokenType.Number
            && reader.TryGetInt32(out var number)
            && Enum.IsDefined((ScreenTheme)number))
        {
            return (ScreenTheme)number;
        }

        reader.Skip();
        return ScreenTheme.Night;
    }

    public override void Write(Utf8JsonWriter writer, ScreenTheme value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
