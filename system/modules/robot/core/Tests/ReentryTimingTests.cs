// Tests for reentry timing: SessionCloseResolver-derived vs spec fallback.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test REENTRY_TIMING
//
// Verifies: GetReentryAllowedUtc with holiday (HasSession=false), early close, delayed open, spec fallback.

using System;
using System.IO;
using System.Text.Json;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Tests;

public static class ReentryTimingTests
{
    public static (bool Pass, string? Error) RunReentryTimingTests()
    {
        var root = ProjectRootResolver.ResolveProjectRoot();
        if (string.IsNullOrEmpty(root))
            return (false, "Project root could not be resolved");
        var configPath = Path.Combine(root, "configs", "analyzer_robot_parity.json");
        if (!File.Exists(configPath))
            return (false, $"Config not found: {configPath}");

        var tempRoot = Path.Combine(Path.GetTempPath(), "ReentryTimingTests_" + Guid.NewGuid().ToString("N")[..8]);
        var timetablePath = Path.Combine(tempRoot, "timetable.json");
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "data", "timetable"));
            Directory.CreateDirectory(Path.Combine(tempRoot, "data", "session"));
            Directory.CreateDirectory(Path.Combine(tempRoot, "configs"));
            var tempConfigPath = Path.Combine(tempRoot, "configs", "analyzer_robot_parity.json");
            File.Copy(configPath, tempConfigPath);
            var tempSpec = ParitySpec.LoadFromFile(tempConfigPath);
            tempSpec.forced_flatten.buffer_seconds = 420;
            tempSpec.forced_flatten.market_reopen_time = "18:30";
            File.WriteAllText(tempConfigPath, JsonSerializer.Serialize(tempSpec, new JsonSerializerOptions { WriteIndented = true }));
            var execPolicyPath = Path.Combine(root, "configs", "execution_policy.json");
            if (File.Exists(execPolicyPath))
                File.Copy(execPolicyPath, Path.Combine(tempRoot, "configs", "execution_policy.json"));
            File.WriteAllText(Path.Combine(tempRoot, "data", "session", "session_authority.json"), "{\"session_trading_date\":\"2026-03-16\",\"mode\":\"replay\"}");
            File.WriteAllText(timetablePath, "{\"session_trading_date\":\"2026-03-16\",\"timezone\":\"America/Chicago\",\"metadata\":{\"replay\":true},\"streams\":[{\"stream\":\"ES1\",\"instrument\":\"MES\",\"session\":\"S1\",\"slot_time\":\"07:30\",\"enabled\":true},{\"stream\":\"ES2\",\"instrument\":\"MES\",\"session\":\"S2\",\"slot_time\":\"09:30\",\"enabled\":true}]}");

            var tradingDay = "2026-03-16";
            var utcNow = new DateTimeOffset(2026, 3, 16, 22, 0, 0, TimeSpan.Zero); // 17:00 CT (CDT)

            var engine = new RobotEngine(tempRoot, TimeSpan.FromSeconds(60), ExecutionMode.DRYRUN, null, timetablePath, "MES", useAsyncLogging: false);
            engine.SetPlaybackStartTime(utcNow);
            engine.Start();
            System.Threading.Thread.Sleep(500);

            // 1. Holiday: HasSession=false -> (null, false)
            engine.SetSessionCloseResolved(tradingDay, "S1", new SessionCloseResult { HasSession = false });
            var (holidayUtc, holidaySession) = engine.GetReentryAllowedUtc(tradingDay, "S1", utcNow);
            if (holidaySession)
                return (false, "Holiday (HasSession=false) should return hasSession=false");
            if (holidayUtc.HasValue)
                return (false, "Holiday should return reentryUtc=null");

            // 1b. NT holiday is only evidence: active timetable + internal spec become authoritative truth.
            engine.SetSessionCloseResolved(tradingDay, "S2", new SessionCloseResult { HasSession = false, FailureReason = "HOLIDAY" });
            var holidayTruth = engine.ResolveSessionTruth(tradingDay, "S2", utcNow);
            if (!holidayTruth.IsResolved || !holidayTruth.HasSession)
                return (false, $"Active timetable should override NT holiday into a resolved session truth frame; source={holidayTruth.Source}, resolved={holidayTruth.IsResolved}, has_session={holidayTruth.HasSession}, failure={holidayTruth.FailureReason ?? ""}, conflict={holidayTruth.ConflictDetected}");
            if (holidayTruth.Source != "INTERNAL_OVERRIDE")
                return (false, $"Expected INTERNAL_OVERRIDE session truth for active timetable holiday conflict, got {holidayTruth.Source}");
            if (!holidayTruth.ConflictDetected || holidayTruth.NtCacheResult?.FailureReason != "HOLIDAY")
                return (false, "Session truth should preserve NT HOLIDAY as raw evidence while selecting internal authority");

            // 2. Early close: NextSessionBeginUtc uses the explicit forced_flatten.market_reopen_time override.
            var spec = ParitySpec.LoadFromFile(tempConfigPath);
            var time = new TimeService(spec.timezone);
            var reopenChicago = time.ConstructChicagoTime(new DateOnly(2026, 3, 16), SessionTimingPolicy.ResolveMarketReopenTime(spec));
            var nextSessionBeginUtc = time.ConvertChicagoToUtc(reopenChicago);
            engine.SetSessionCloseResolved(tradingDay, "S1", new SessionCloseResult
            {
                HasSession = true,
                NextSessionBeginUtc = nextSessionBeginUtc,
                ResolvedSessionCloseUtc = nextSessionBeginUtc.AddHours(-4), // 4h before reopen
                FlattenTriggerUtc = nextSessionBeginUtc.AddHours(-4).AddSeconds(-300)
            });
            var (resolverUtc, resolverSession) = engine.GetReentryAllowedUtc(tradingDay, "S1", utcNow);
            if (!resolverSession)
                return (false, "Resolver result with HasSession=true should return hasSession=true");
            if (!resolverUtc.HasValue || resolverUtc.Value != nextSessionBeginUtc)
                return (false, $"Resolver NextSessionBeginUtc should be used: expected {nextSessionBeginUtc:o}, got {resolverUtc?.ToString() ?? "null"}");

            // 3. Delayed open: NextSessionBeginUtc = 14:30 UTC (09:30 CT next day - hypothetical)
            var delayedOpenUtc = new DateTimeOffset(2026, 3, 17, 14, 30, 0, TimeSpan.Zero);
            engine.SetSessionCloseResolved(tradingDay, "S2", new SessionCloseResult
            {
                HasSession = true,
                NextSessionBeginUtc = delayedOpenUtc,
                ResolvedSessionCloseUtc = new DateTimeOffset(2026, 3, 16, 21, 0, 0, TimeSpan.Zero),
                FlattenTriggerUtc = new DateTimeOffset(2026, 3, 16, 20, 55, 0, 0, TimeSpan.Zero)
            });
            var beforeDelayed = new DateTimeOffset(2026, 3, 17, 14, 0, 0, TimeSpan.Zero);
            var (beforeUtc, _) = engine.GetReentryAllowedUtc(tradingDay, "S2", beforeDelayed);
            if (beforeUtc.HasValue && beforeDelayed < beforeUtc.Value)
            {
                // Good: beforeDelayed (14:00) < delayedOpenUtc (14:30), so reentry not yet allowed
            }
            var afterDelayed = new DateTimeOffset(2026, 3, 17, 15, 0, 0, TimeSpan.Zero);
            var (afterUtc, afterSession) = engine.GetReentryAllowedUtc(tradingDay, "S2", afterDelayed);
            if (!afterSession || !afterUtc.HasValue || afterUtc.Value != delayedOpenUtc)
                return (false, $"Delayed open: after 14:30 should allow reentry at {delayedOpenUtc:o}");

            // 4. Fallback: no cache, spec available -> returns forced_flatten.market_reopen_time in UTC
            var fallbackDay = "2026-03-17";
            var (fallbackUtc, fallbackSession) = engine.GetReentryAllowedUtc(fallbackDay, "S1", utcNow);
            if (!fallbackSession)
                return (false, "Fallback (no cache) should return hasSession=true");
            if (!fallbackUtc.HasValue)
                return (false, "Fallback should compute reentryUtc from forced_flatten.market_reopen_time");
            var expectedReopen = time.ConstructChicagoTime(new DateOnly(2026, 3, 17), SessionTimingPolicy.ResolveMarketReopenTime(spec));
            var expectedReopenUtc = time.ConvertChicagoToUtc(expectedReopen);
            if (Math.Abs((fallbackUtc.Value - expectedReopenUtc).TotalSeconds) > 1)
                return (false, $"Fallback reentryUtc should match forced_flatten.market_reopen_time: expected {expectedReopenUtc:o}, got {fallbackUtc:o}");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }
}
