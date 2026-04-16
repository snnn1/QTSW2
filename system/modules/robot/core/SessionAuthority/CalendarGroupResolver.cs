using System;
using System.Globalization;

namespace QTSW2.Robot.Core.SessionAuthority;

/// <summary>
/// Maps execution / master instrument roots to a canonical calendar group key used by <c>session_calendar.json</c>.
/// Single place for this mapping (avoid duplicate trees).
/// </summary>
public static class CalendarGroupResolver
{
    public const string CmeEquityIndex = "CME_EQUITY_INDEX";
    public const string NymexEnergy = "NYMEX_ENERGY";
    public const string ComexMetals = "COMEX_METALS";

    /// <summary>
    /// Resolve calendar group from NinjaTrader-style instrument names (e.g. <c>MES 06-26</c>, <c>ES</c>).
    /// </summary>
    public static string Resolve(string? executionInstrument, string? masterInstrumentName)
    {
        var root = ExtractRootSymbol(executionInstrument) ?? ExtractRootSymbol(masterInstrumentName);
        if (string.IsNullOrEmpty(root))
            return CmeEquityIndex;

        // Micro contracts share the same holiday calendar as their full-size roots for CME equity index suite.
        switch (root)
        {
            case "ES":
            case "MES":
            case "NQ":
            case "MNQ":
            case "YM":
            case "MYM":
            case "RTY":
            case "M2K":
                return CmeEquityIndex;
            case "CL":
            case "NG":
            case "HO":
            case "RB":
                return NymexEnergy;
            case "GC":
            case "MGC":
            case "SI":
            case "HG":
                return ComexMetals;
            default:
                return CmeEquityIndex;
        }
    }

    /// <summary>First token before space, uppercased (e.g. MES from MES 06-26).</summary>
    internal static string? ExtractRootSymbol(string? instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument))
            return null;
        var trimmed = instrument.Trim();
        var space = trimmed.IndexOf(' ');
        var token = space >= 0 ? trimmed.Substring(0, space) : trimmed;
        if (string.IsNullOrEmpty(token))
            return null;
        return token.ToUpperInvariant();
    }
}
