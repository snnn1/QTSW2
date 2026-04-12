using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace QTSW2.Robot.Core;

/// <summary>
/// Shared rules for SessionAuthority vs timetable (live gate). Tested via harness.
/// </summary>
public static class SessionAuthorityTimetableGate
{
    private static readonly Regex IsoYmd = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    /// <summary>
    /// Strict calendar day: zero-padded YYYY-MM-DD plus <see cref="DateTime.TryParseExact"/> (rejects e.g. 2026-4-9 and 2026-02-31).
    /// </summary>
    public static bool IsStrictIsoDate(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!IsoYmd.IsMatch(s)) return false;
        return DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    /// <summary>
    /// Live timetables require a persisted authority file; replay skips the requirement.
    /// </summary>
    public static bool IsAuthorityFilePresenceOk(bool timetableReplay, bool authorityFileExists)
    {
        return timetableReplay || authorityFileExists;
    }
}
