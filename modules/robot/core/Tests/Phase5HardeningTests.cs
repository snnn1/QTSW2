// Unit tests for Phase 5 hardening: kill switch, IsExecutionAllowed, RiskGate, hysteresis.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test PHASE5_HARDENING
//
// Verifies: Kill switch blocks IsExecutionAllowed; RiskGate blocks when kill switch on;
// SupervisoryPolicy hysteresis (dwell suppression).

using System;
using System.IO;
using System.Text.Json;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class Phase5HardeningTests
{
    public static (bool Pass, string? Error) RunPhase5HardeningTests()
    {
        var err = RunIsExecutionAllowedKillSwitchTests();
        if (err != null) return (false, err);

        err = RunRiskGateKillSwitchTests();
        if (err != null) return (false, err);

        err = RunSupervisoryPolicyHysteresisTests();
        if (err != null) return (false, err);

        return (true, null);
    }

    /// <summary>Test: IsExecutionAllowed returns false when global kill switch is enabled.</summary>
    private static string? RunIsExecutionAllowedKillSwitchTests()
    {
        var root = ProjectRootResolver.ResolveProjectRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "QTSW2_Phase5Hardening_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            var configsRobot = Path.Combine(tempRoot, "configs", "robot");
            var configsDir = Path.Combine(tempRoot, "configs");
            var dataTimetable = Path.Combine(tempRoot, "data", "timetable");
            var logsRobot = Path.Combine(tempRoot, "logs", "robot");
            Directory.CreateDirectory(configsRobot);
            Directory.CreateDirectory(configsDir);
            Directory.CreateDirectory(dataTimetable);
            Directory.CreateDirectory(logsRobot);

            // Copy spec and execution policy (required for Start)
            File.Copy(Path.Combine(root, "configs", "analyzer_robot_parity.json"),
                Path.Combine(tempRoot, "configs", "analyzer_robot_parity.json"));
            var policySrc = Path.Combine(root, "configs", "execution_policy.json");
            if (File.Exists(policySrc))
                File.Copy(policySrc, Path.Combine(tempRoot, "configs", "execution_policy.json"));

            // Timetable (required for Start)
            var timetableJson = JsonSerializer.Serialize(new
            {
                as_of = DateTimeOffset.UtcNow.ToString("o"),
                trading_date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"),
                timezone = "America/Chicago",
                source = "phase5_hardening_test",
                streams = new[]
                {
                    new { stream = "ES1", instrument = "MES", session = "S1", slot_time = "07:30", enabled = true }
                }
            });
            File.WriteAllText(Path.Combine(dataTimetable, "timetable_current.json"), timetableJson);

            var timetablePath = Path.Combine(dataTimetable, "timetable_current.json");

            // Test 1: Kill switch ENABLED -> IsExecutionAllowed false
            File.WriteAllText(Path.Combine(configsRobot, "kill_switch.json"),
                JsonSerializer.Serialize(new { Enabled = true, Message = "Phase5 hardening test" }));

            var engine1 = new RobotEngine(tempRoot, TimeSpan.FromSeconds(60), ExecutionMode.DRYRUN, null, timetablePath, "MES", null, false);
            engine1.Start();

            if (engine1.IsExecutionAllowed())
            {
                engine1.Stop();
                return "Kill switch ENABLED: expected IsExecutionAllowed false, got true";
            }
            engine1.Stop();

            // Test 2: Kill switch DISABLED -> IsExecutionAllowed true (when CONNECTED_OK)
            File.WriteAllText(Path.Combine(configsRobot, "kill_switch.json"),
                JsonSerializer.Serialize(new { Enabled = false, Message = "Phase5 hardening test - disabled" }));

            var engine2 = new RobotEngine(tempRoot, TimeSpan.FromSeconds(60), ExecutionMode.DRYRUN, null, timetablePath, "MES", null, false);
            engine2.Start();

            if (!engine2.IsExecutionAllowed())
            {
                engine2.Stop();
                return "Kill switch DISABLED: expected IsExecutionAllowed true (CONNECTED_OK), got false";
            }
            engine2.Stop();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch { /* best effort */ }
        }

        return null;
    }

    /// <summary>Test: RiskGate.CheckGates returns Allowed=false when kill switch is enabled.</summary>
    private static string? RunRiskGateKillSwitchTests()
    {
        var root = ProjectRootResolver.ResolveProjectRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "QTSW2_RiskGate_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            var configsRobot = Path.Combine(tempRoot, "configs", "robot");
            Directory.CreateDirectory(configsRobot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "logs", "robot"));

            // Kill switch ENABLED
            File.WriteAllText(Path.Combine(configsRobot, "kill_switch.json"),
                JsonSerializer.Serialize(new { Enabled = true, Message = "RiskGate test" }));

            var spec = ParitySpec.LoadFromFile(Path.Combine(root, "configs", "analyzer_robot_parity.json"));
            var time = new TimeService(spec.timezone);
            var log = new RobotLogger(tempRoot, Path.Combine(tempRoot, "logs", "robot"), null, null);
            var killSwitch = new KillSwitch(tempRoot, log);
            var riskGate = new RiskGate(spec, time, log, killSwitch, guard: null);

            var (allowed, reason, failedGates) = riskGate.CheckGates(
                ExecutionMode.SIM,
                "2026-03-12",
                "ES1",
                "MES",
                "S1",
                "07:30",
                timetableValidated: true,
                streamArmed: true,
                DateTimeOffset.UtcNow);

            if (allowed)
                return $"RiskGate with kill switch ON: expected Allowed=false, got true. Reason={reason}";
            if (reason != "KILL_SWITCH_ACTIVE")
                return $"RiskGate with kill switch ON: expected Reason=KILL_SWITCH_ACTIVE, got {reason}";
            if (!failedGates.Contains("KILL_SWITCH"))
                return $"RiskGate with kill switch ON: expected FailedGates to contain KILL_SWITCH, got [{string.Join(", ", failedGates)}]";
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch { /* best effort */ }
        }

        return null;
    }

    /// <summary>Test: SupervisoryPolicy.ShouldSuppressCooldownEscalation (hysteresis).</summary>
    private static string? RunSupervisoryPolicyHysteresisTests()
    {
        var utc = new DateTimeOffset(2026, 3, 12, 14, 0, 0, TimeSpan.Zero);
        const int minDwell = 120;

        // No lastResume -> do not suppress (allow escalation)
        if (SupervisoryPolicy.ShouldSuppressCooldownEscalation(null, utc, minDwell))
            return "lastResume=null: should NOT suppress, got true";

        // Just resumed (0s ago) -> suppress
        if (!SupervisoryPolicy.ShouldSuppressCooldownEscalation(utc, utc, minDwell))
            return "lastResume=now (0s ago): should suppress, got false";

        // Resumed 60s ago -> suppress
        if (!SupervisoryPolicy.ShouldSuppressCooldownEscalation(utc.AddSeconds(-60), utc, minDwell))
            return "lastResume=60s ago: should suppress, got false";

        // Resumed 119s ago -> suppress
        if (!SupervisoryPolicy.ShouldSuppressCooldownEscalation(utc.AddSeconds(-119), utc, minDwell))
            return "lastResume=119s ago: should suppress, got false";

        // Resumed 120s ago -> do not suppress
        if (SupervisoryPolicy.ShouldSuppressCooldownEscalation(utc.AddSeconds(-120), utc, minDwell))
            return "lastResume=120s ago: should NOT suppress, got true";

        // Resumed 121s ago -> do not suppress
        if (SupervisoryPolicy.ShouldSuppressCooldownEscalation(utc.AddSeconds(-121), utc, minDwell))
            return "lastResume=121s ago: should NOT suppress, got true";

        return null;
    }
}
