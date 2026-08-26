namespace Pos.Core.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Where a lane writes down what happened.
/// </summary>
/// <remarks>
/// The purpose is a pilot that can be diagnosed. A cashier saying "it did something strange this
/// morning" is worth nothing without a trail, because the status line on screen is gone the moment
/// the next message replaces it.
/// <para>
/// Deliberately not a general-purpose logging framework. A till writes a few hundred lines a day,
/// needs them on disk before the power goes out, and must never fail a sale because a log file
/// could not be written — so every implementation here swallows its own errors.
/// </para>
/// </remarks>
public interface IPosLog
{
    void Write(LogLevel level, string category, string message, Exception? exception = null);
}

public static class PosLogExtensions
{
    public static void Debug(this IPosLog log, string category, string message) =>
        log.Write(LogLevel.Debug, category, message);

    public static void Info(this IPosLog log, string category, string message) =>
        log.Write(LogLevel.Info, category, message);

    public static void Warn(this IPosLog log, string category, string message, Exception? exception = null) =>
        log.Write(LogLevel.Warning, category, message, exception);

    public static void Error(this IPosLog log, string category, string message, Exception? exception = null) =>
        log.Write(LogLevel.Error, category, message, exception);
}

/// <summary>Writes nothing. The default, so nothing has to null-check a log.</summary>
public sealed class NullLog : IPosLog
{
    public static NullLog Instance { get; } = new();

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
    }
}
