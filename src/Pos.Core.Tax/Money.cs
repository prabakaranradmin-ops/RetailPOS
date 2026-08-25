namespace Pos.Core.Tax;

/// <summary>
/// Rounding primitives for all monetary maths.
/// </summary>
public static class Money
{
    /// <summary>Decimal places carried through intermediate calculations.</summary>
    public const int InternalScale = 4;

    /// <summary>Decimal places used on anything printed or displayed.</summary>
    public const int PresentationScale = 2;

    /// <summary>
    /// Banker's rounding (round-half-to-even). Used at every rounding step so that
    /// half-paisa values do not bias totals upward across a long run of invoices.
    /// </summary>
    public static decimal Round(decimal value, int decimals) =>
        Math.Round(value, decimals, MidpointRounding.ToEven);

    public static decimal ToInternal(decimal value) => Round(value, InternalScale);

    public static decimal ToPresentation(decimal value) => Round(value, PresentationScale);
}
