using System;

namespace QTSW2.Robot.Core;

/// <summary>
/// Tick rounding utility matching Analyzer's UtilityManager.round_to_tick exactly.
/// Analyzer implementation: np.round(value / tick_size) * tick_size
/// This must match exactly for parity.
/// </summary>
public static class UtilityRoundToTick
{
    /// <summary>
    /// Round a price to the nearest tick, matching Analyzer's UtilityManager.round_to_tick exactly.
    /// Analyzer uses: np.round(value / tick_size) * tick_size
    /// </summary>
    /// <param name="value">Price value to round</param>
    /// <param name="tickSize">Tick size for the instrument</param>
    /// <returns>Price rounded to nearest tick</returns>
    public static decimal RoundToTick(decimal value, decimal tickSize)
    {
        if (tickSize <= 0)
            throw new ArgumentException("Tick size must be positive", nameof(tickSize));

        // Match Analyzer: np.round(value / tick_size) * tick_size
        // C# Math.Round uses "round half to even" (banker's rounding) by default for MidpointRounding.ToEven
        // This matches numpy's default rounding behavior
        return Math.Round(value / tickSize, MidpointRounding.ToEven) * tickSize;
    }

    /// <summary>
    /// Round a price to the nearest tick (double overload for compatibility)
    /// </summary>
    public static double RoundToTick(double value, double tickSize)
    {
        if (tickSize <= 0)
            throw new ArgumentException("Tick size must be positive", nameof(tickSize));

        return Math.Round(value / tickSize, MidpointRounding.ToEven) * tickSize;
    }
}
