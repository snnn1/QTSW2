// Session identity hard gate: wrong trading day vs engine blocks once, CRITICAL once, no retry loops.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test SESSION_IDENTITY_GATE

using System;
using System.IO;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class SessionIdentityGateTests
{
    public static (bool Pass, string? Error) RunSessionIdentityGateTests()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "QTSW2_SessionIdGate_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            var log = new RobotLogger(tempRoot, Path.Combine(tempRoot, "logs", "robot"));
            var journal = new ExecutionJournal(tempRoot, log);
            var adapter = new NinjaTraderSimAdapter(tempRoot, log, journal);

            const string activeDay = "2026-04-01";
            adapter.SetEngineCallbacks(
                standDownStreamCallback: null,
                getNotificationServiceCallback: null,
                isExecutionAllowedCallback: null,
                getActiveTradingDateString: () => activeDay);

            var utc = DateTimeOffset.UtcNow;
            var wrongDayIntent = new Intent(
                "2026-03-31",
                "S1",
                "MES",
                "MES",
                "S1",
                "07:30",
                "Long",
                null,
                5000m,
                6000m,
                5000m,
                utc,
                "TEST");
            var idWrong = wrongDayIntent.ComputeIntentId();
            adapter.RegisterIntent(wrongDayIntent);
            adapter.RegisterIntentPolicy(idWrong, 1, 5, "MES", "MES", "TEST");

            var r1 = adapter.SubmitEntryOrder(idWrong, "MES", "Long", null, 1, "MARKET", null, utc);
            if (r1.Success)
                return (false, "Expected failure for mismatched trading date, got success");

            if (!string.Equals(r1.ErrorMessage, "SESSION_IDENTITY_MISMATCH", StringComparison.Ordinal))
                return (false, $"First submit: expected SESSION_IDENTITY_MISMATCH, got {r1.ErrorMessage}");

            if (adapter.SessionIdentityMismatchCriticalEmitCount != 1)
                return (false, $"Expected one CRITICAL emission, got {adapter.SessionIdentityMismatchCriticalEmitCount}");

            if (!adapter.IsSessionIdentityLatched)
                return (false, "Expected latch after first mismatch");

            if (adapter.SessionIdentityBlockCount != 1)
                return (false, $"Expected session_identity_block_count 1 after first reject, got {adapter.SessionIdentityBlockCount}");

            var r2 = adapter.SubmitEntryOrder(idWrong, "MES", "Long", null, 1, "MARKET", null, utc);
            if (r2.Success)
                return (false, "Second submit should fail when latched");

            if (!string.Equals(r2.ErrorMessage, "SESSION_IDENTITY_LATCHED", StringComparison.Ordinal))
                return (false, $"Second submit: expected SESSION_IDENTITY_LATCHED, got {r2.ErrorMessage}");

            if (adapter.SessionIdentityMismatchCriticalEmitCount != 1)
                return (false, "CRITICAL must not repeat on latched submits");

            if (adapter.SessionIdentityBlockCount != 2)
                return (false, $"Expected session_identity_block_count 2 after latched reject, got {adapter.SessionIdentityBlockCount}");

            // Correct session: new adapter instance (latch is per-adapter; sim preconditions may still fail — we only assert gate passes).
            var adapter2 = new NinjaTraderSimAdapter(tempRoot, log, journal);
            adapter2.SetEngineCallbacks(null, null, null, getActiveTradingDateString: () => activeDay);
            var okIntent = new Intent(
                activeDay,
                "S1",
                "MES",
                "MES",
                "S1",
                "07:30",
                "Long",
                null,
                5000m,
                6000m,
                5000m,
                utc,
                "TEST");
            var idOk = okIntent.ComputeIntentId();
            adapter2.RegisterIntent(okIntent);
            adapter2.RegisterIntentPolicy(idOk, 1, 5, "MES", "MES", "TEST");
            var rOk = adapter2.SubmitEntryOrder(idOk, "MES", "Long", null, 1, "MARKET", null, utc);
            if (string.Equals(rOk.ErrorMessage, "SESSION_IDENTITY_MISMATCH", StringComparison.Ordinal) ||
                string.Equals(rOk.ErrorMessage, "SESSION_IDENTITY_LATCHED", StringComparison.Ordinal))
                return (false, "Correct-session intent should pass session gate (may fail later for SIM/NT)");

            // Reentry path uses SubmitEntryOrder — covered by same gate; recovery queue uses TrySessionIdentityGate in ProcessRecoveryQueue (no harness hook).

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
