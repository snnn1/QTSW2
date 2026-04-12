// Session authority vs timetable gate (strict date + file presence). Run:
//   dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test SESSION_AUTHORITY_GATE

using System;

namespace QTSW2.Robot.Core.Tests;

public static class SessionAuthorityTimetableGateTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        string? e;
        e = Case_StrictIso_ValidAndInvalid();
        if (e != null) return (false, e);
        e = Case_FilePresence_ReplayVsLive();
        if (e != null) return (false, e);
        e = Case_MatchEquivalence();
        if (e != null) return (false, e);
        e = Case_Deserialize_ModeAndValidDate();
        if (e != null) return (false, e);
        e = Case_BadJson_DeserializeFails();
        if (e != null) return (false, e);
        return (true, null);
    }

    private static string? Case_StrictIso_ValidAndInvalid()
    {
        if (!SessionAuthorityTimetableGate.IsStrictIsoDate("2026-04-09")) return "expected 2026-04-09 ok";
        if (SessionAuthorityTimetableGate.IsStrictIsoDate("2026-4-9")) return "expected 2026-4-9 rejected";
        if (SessionAuthorityTimetableGate.IsStrictIsoDate("2026-02-31")) return "expected invalid calendar rejected";
        if (SessionAuthorityTimetableGate.IsStrictIsoDate("")) return "expected empty rejected";
        if (SessionAuthorityTimetableGate.IsStrictIsoDate("not-a-date")) return "expected garbage rejected";
        return null;
    }

    private static string? Case_FilePresence_ReplayVsLive()
    {
        if (!SessionAuthorityTimetableGate.IsAuthorityFilePresenceOk(timetableReplay: true, authorityFileExists: false))
            return "replay should not require file";
        if (!SessionAuthorityTimetableGate.IsAuthorityFilePresenceOk(timetableReplay: false, authorityFileExists: true))
            return "live + file should pass";
        if (SessionAuthorityTimetableGate.IsAuthorityFilePresenceOk(timetableReplay: false, authorityFileExists: false))
            return "live without file should fail presence check";
        return null;
    }

    /// <summary>Mirrors post-parse checks in RobotEngine (empty → fail; bad format → fail; ordinal match).</summary>
    private static bool AuthorityMatchesTimetable(string timetableSession, SessionAuthorityContract? authority)
    {
        var authSession = (authority?.session_trading_date ?? "").Trim();
        if (string.IsNullOrEmpty(authSession)) return false;
        if (!SessionAuthorityTimetableGate.IsStrictIsoDate(authSession)) return false;
        return string.Equals(timetableSession, authSession, StringComparison.Ordinal);
    }

    private static string? Case_MatchEquivalence()
    {
        var ok = new SessionAuthorityContract { session_trading_date = "2026-04-09", mode = "auto" };
        if (!AuthorityMatchesTimetable("2026-04-09", ok)) return "valid match expected";
        if (AuthorityMatchesTimetable("2026-04-10", ok)) return "mismatch should fail";
        if (AuthorityMatchesTimetable("2026-04-09", new SessionAuthorityContract { session_trading_date = "" }))
            return "empty session should fail";
        if (AuthorityMatchesTimetable("2026-04-09", new SessionAuthorityContract { session_trading_date = "2026-4-9" }))
            return "non-strict date should fail";
        return null;
    }

    private static string? Case_Deserialize_ModeAndValidDate()
    {
        const string json = "{\"mode\":\"manual\",\"session_trading_date\":\"2026-04-09\",\"source\":\"user\"}";
        SessionAuthorityContract? a;
        try
        {
            a = JsonUtil.Deserialize<SessionAuthorityContract>(json);
        }
        catch (Exception ex)
        {
            return "deserialize valid json: " + ex.Message;
        }

        if (a == null) return "expected non-null contract";
        if (a.mode != "manual") return "mode round-trip";
        if (!AuthorityMatchesTimetable("2026-04-09", a)) return "match after deserialize";
        return null;
    }

    private static string? Case_BadJson_DeserializeFails()
    {
        const string bad = "{ not json";
        try
        {
            var r = JsonUtil.Deserialize<SessionAuthorityContract>(bad);
            if (r != null) return "bad JSON should not deserialize to non-null contract";
        }
        catch
        {
            return null;
        }

        return null;
    }
}
