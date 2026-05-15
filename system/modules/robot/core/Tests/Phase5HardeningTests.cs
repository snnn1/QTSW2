// Unit tests for Phase 5 hardening: kill switch, IsExecutionAllowed, RiskGate, hysteresis.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test PHASE5_HARDENING
//
// Verifies: Kill switch blocks IsExecutionAllowed; RiskGate blocks when kill switch on;
// SupervisoryPolicy hysteresis (dwell suppression).

using System;
using System.IO;
using System.Reflection;
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

        err = RunLocalPlaybackStopDoesNotBroadcastRunWideShutdownSignal();
        if (err != null) return (false, err);

        err = RunFinalPlaybackStopStopsAllProcessEnginesWithoutCrashSignal();
        if (err != null) return (false, err);

        err = RunPlaybackStallLiveExposureFinalizeStopsAllProcessEngines();
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
            // Isolated filename — live timetable_current.json is published only via TimetableEngine (Python).
            File.WriteAllText(Path.Combine(dataTimetable, "timetable_phase5_test.json"), timetableJson);

            var timetablePath = Path.Combine(dataTimetable, "timetable_phase5_test.json");

            // Test 1: Kill switch ENABLED -> IsExecutionAllowed false
            File.WriteAllText(Path.Combine(configsRobot, "kill_switch.json"),
                JsonSerializer.Serialize(new { Enabled = true, Message = "Phase5 hardening test" }));

            var engine1 = new RobotEngine(tempRoot, TimeSpan.FromSeconds(60), ExecutionMode.DRYRUN, null, timetablePath, "MES", useAsyncLogging: false);
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

            var engine2 = new RobotEngine(tempRoot, TimeSpan.FromSeconds(60), ExecutionMode.DRYRUN, null, timetablePath, "MES", useAsyncLogging: false);
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

    /// <summary>Test: a normal local playback engine stop must not broadcast a run-wide shutdown signal.</summary>
    private static string? RunLocalPlaybackStopDoesNotBroadcastRunWideShutdownSignal()
    {
        var root = ProjectRootResolver.ResolveProjectRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "QTSW2_LocalStopNoBroadcast_" + Guid.NewGuid().ToString("N")[..8]);
        var priorRunId = Environment.GetEnvironmentVariable("QTSW2_RUN_ID");
        var runId = "local_stop_no_broadcast_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            Environment.SetEnvironmentVariable("QTSW2_RUN_ID", runId);

            var configsRobot = Path.Combine(tempRoot, "configs", "robot");
            var configsDir = Path.Combine(tempRoot, "configs");
            var dataTimetable = Path.Combine(tempRoot, "data", "timetable");
            var logsRobot = Path.Combine(tempRoot, "logs", "robot");
            Directory.CreateDirectory(configsRobot);
            Directory.CreateDirectory(configsDir);
            Directory.CreateDirectory(dataTimetable);
            Directory.CreateDirectory(logsRobot);

            File.Copy(Path.Combine(root, "configs", "analyzer_robot_parity.json"),
                Path.Combine(configsDir, "analyzer_robot_parity.json"));

            var policySrc = Path.Combine(root, "configs", "execution_policy.json");
            if (File.Exists(policySrc))
                File.Copy(policySrc, Path.Combine(configsDir, "execution_policy.json"));

            var loggingSrc = Path.Combine(root, "configs", "robot", "logging.json");
            if (File.Exists(loggingSrc))
                File.Copy(loggingSrc, Path.Combine(configsRobot, "logging.json"));

            var sessionCalendarSrc = Path.Combine(root, "configs", "robot", "session_calendar.json");
            if (File.Exists(sessionCalendarSrc))
                File.Copy(sessionCalendarSrc, Path.Combine(configsRobot, "session_calendar.json"));

            var timetableJson = JsonSerializer.Serialize(new
            {
                as_of = DateTimeOffset.UtcNow.ToString("o"),
                trading_date = "2026-04-14",
                timezone = "America/Chicago",
                source = "local_stop_no_broadcast_test",
                is_replay = true,
                streams = new[]
                {
                    new { stream = "ES1", instrument = "MES", session = "S1", slot_time = "07:30", enabled = true }
                }
            });

            var timetablePath = Path.Combine(dataTimetable, "timetable_replay_current.json");
            File.WriteAllText(timetablePath, timetableJson);

            var engine = new RobotEngine(
                tempRoot,
                TimeSpan.FromSeconds(60),
                ExecutionMode.SIM,
                customTimetablePath: timetablePath,
                instrument: "MES",
                masterInstrumentName: "MES",
                ignoreExistingStreamJournals: true,
                playbackAccountDetected: true,
                useAsyncLogging: false);

            engine.Start();
            engine.Stop();

            var shutdownSignalPath = Path.Combine(tempRoot, "runs", runId, RunRootArtifacts.RunShutdownSignalFileName);
            if (File.Exists(shutdownSignalPath))
                return "Local playback Stop() wrote RUN_SHUTDOWN.json; expected only explicit global shutdown paths to broadcast.";
        }
        finally
        {
            try { Environment.SetEnvironmentVariable("QTSW2_RUN_ID", priorRunId); } catch { /* best effort */ }
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch { /* best effort */ }
        }

        return null;
    }

    /// <summary>
    /// Test: final strategy termination uses process-local stop broadcast to quiesce sibling playback engines
    /// without writing RUN_SHUTDOWN.json, which is reserved for crash/freeze evidence.
    /// </summary>
    private static string? RunFinalPlaybackStopStopsAllProcessEnginesWithoutCrashSignal()
    {
        var root = ProjectRootResolver.ResolveProjectRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "QTSW2_ProcessStop_" + Guid.NewGuid().ToString("N")[..8]);
        var priorRunId = Environment.GetEnvironmentVariable("QTSW2_RUN_ID");
        var runId = "process_stop_" + Guid.NewGuid().ToString("N")[..8];
        RobotEngine? engine1 = null;
        RobotEngine? engine2 = null;

        try
        {
            Environment.SetEnvironmentVariable("QTSW2_RUN_ID", runId);

            var configsRobot = Path.Combine(tempRoot, "configs", "robot");
            var configsDir = Path.Combine(tempRoot, "configs");
            var dataTimetable = Path.Combine(tempRoot, "data", "timetable");
            Directory.CreateDirectory(configsRobot);
            Directory.CreateDirectory(configsDir);
            Directory.CreateDirectory(dataTimetable);

            File.Copy(Path.Combine(root, "configs", "analyzer_robot_parity.json"),
                Path.Combine(configsDir, "analyzer_robot_parity.json"));

            var policySrc = Path.Combine(root, "configs", "execution_policy.json");
            if (File.Exists(policySrc))
                File.Copy(policySrc, Path.Combine(configsDir, "execution_policy.json"));

            var loggingSrc = Path.Combine(root, "configs", "robot", "logging.json");
            if (File.Exists(loggingSrc))
                File.Copy(loggingSrc, Path.Combine(configsRobot, "logging.json"));

            var sessionCalendarSrc = Path.Combine(root, "configs", "robot", "session_calendar.json");
            if (File.Exists(sessionCalendarSrc))
                File.Copy(sessionCalendarSrc, Path.Combine(configsRobot, "session_calendar.json"));

            var timetableJson = JsonSerializer.Serialize(new
            {
                as_of = DateTimeOffset.UtcNow.ToString("o"),
                trading_date = "2026-04-14",
                timezone = "America/Chicago",
                source = "process_stop_broadcast_test",
                is_replay = true,
                streams = new[]
                {
                    new { stream = "ES1", instrument = "MES", session = "S1", slot_time = "07:30", enabled = true }
                }
            });

            var timetablePath = Path.Combine(dataTimetable, "timetable_replay_current.json");
            File.WriteAllText(timetablePath, timetableJson);

            engine1 = new RobotEngine(
                tempRoot,
                TimeSpan.FromSeconds(60),
                ExecutionMode.SIM,
                customTimetablePath: timetablePath,
                instrument: "MES",
                masterInstrumentName: "MES",
                ignoreExistingStreamJournals: true,
                playbackAccountDetected: true,
                useAsyncLogging: false);
            engine2 = new RobotEngine(
                tempRoot,
                TimeSpan.FromSeconds(60),
                ExecutionMode.SIM,
                customTimetablePath: timetablePath,
                instrument: "MNQ",
                masterInstrumentName: "NQ",
                ignoreExistingStreamJournals: true,
                playbackAccountDetected: true,
                useAsyncLogging: false);

            engine1.Start();
            engine2.Start();
            engine1.StartEngineHeartbeatTimer();
            engine2.StartEngineHeartbeatTimer();
            PrimeHeartbeatForNextTimerEmission(engine1);
            PrimeHeartbeatForNextTimerEmission(engine2);
            engine1.StopAllProcessEnginesForCurrentRun(writeRunSummary: true, stopSource: "phase5_final_strategy_terminated");

            if (!engine2.IsShutdownRequested)
                return "Process-local final stop did not request shutdown on sibling playback engine.";

            var shutdownSignalPath = Path.Combine(tempRoot, "runs", runId, RunRootArtifacts.RunShutdownSignalFileName);
            if (File.Exists(shutdownSignalPath))
                return "Process-local final stop wrote RUN_SHUTDOWN.json; expected crash/freeze signal to remain absent.";

            Thread.Sleep(500);
            var robotLogDir = Path.Combine(tempRoot, "runs", runId, "logs", "robot");
            var afterStopText = ReadRobotLogText(robotLogDir);
            if (!afterStopText.Contains("PROCESS_RUN_STOP_BROADCAST", StringComparison.Ordinal))
                return "Expected PROCESS_RUN_STOP_BROADCAST audit event in run engine log. " +
                       DescribeRobotLogDir(robotLogDir, afterStopText);
            if (!afterStopText.Contains("PROCESS_RUN_STOP_COMPLETED", StringComparison.Ordinal))
                return "Expected PROCESS_RUN_STOP_COMPLETED audit event in run engine log. " +
                       DescribeRobotLogDir(robotLogDir, afterStopText);

            var afterStopLengths = SnapshotRobotLogLengths(robotLogDir);
            Thread.Sleep(1500);
            var afterWaitLengths = SnapshotRobotLogLengths(robotLogDir);
            if (!AreLengthSnapshotsEqual(afterStopLengths, afterWaitLengths))
                return "Process-local final stop allowed post-stop engine log growth; expected heartbeat/runtime timers to be quiesced. " +
                       DescribeLengthDelta(afterStopLengths, afterWaitLengths) + " tail=" + ReadFallbackTail(robotLogDir);
        }
        finally
        {
            try { engine1?.Stop(writeRunSummary: false, stopSource: "test_cleanup"); } catch { /* best effort */ }
            try { engine2?.Stop(writeRunSummary: false, stopSource: "test_cleanup"); } catch { /* best effort */ }
            try { Environment.SetEnvironmentVariable("QTSW2_RUN_ID", priorRunId); } catch { /* best effort */ }
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch { /* best effort */ }
        }

        return null;
    }

    /// <summary>
    /// Test: playback stall live-exposure finalization writes unsafe shutdown evidence and still routes
    /// through the process-wide stop path so sibling engines and background timers do not keep emitting.
    /// </summary>
    private static string? RunPlaybackStallLiveExposureFinalizeStopsAllProcessEngines()
    {
        var root = ProjectRootResolver.ResolveProjectRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "QTSW2_PlaybackStallLiveFinalize_" + Guid.NewGuid().ToString("N")[..8]);
        var priorRunId = Environment.GetEnvironmentVariable("QTSW2_RUN_ID");
        var runId = "playback_stall_live_finalize_" + Guid.NewGuid().ToString("N")[..8];
        RobotEngine? engine1 = null;
        RobotEngine? engine2 = null;

        try
        {
            Environment.SetEnvironmentVariable("QTSW2_RUN_ID", runId);

            var configsRobot = Path.Combine(tempRoot, "configs", "robot");
            var configsDir = Path.Combine(tempRoot, "configs");
            var dataTimetable = Path.Combine(tempRoot, "data", "timetable");
            Directory.CreateDirectory(configsRobot);
            Directory.CreateDirectory(configsDir);
            Directory.CreateDirectory(dataTimetable);

            File.Copy(Path.Combine(root, "configs", "analyzer_robot_parity.json"),
                Path.Combine(configsDir, "analyzer_robot_parity.json"));

            var policySrc = Path.Combine(root, "configs", "execution_policy.json");
            if (File.Exists(policySrc))
                File.Copy(policySrc, Path.Combine(configsDir, "execution_policy.json"));

            var loggingSrc = Path.Combine(root, "configs", "robot", "logging.json");
            if (File.Exists(loggingSrc))
                File.Copy(loggingSrc, Path.Combine(configsRobot, "logging.json"));

            var sessionCalendarSrc = Path.Combine(root, "configs", "robot", "session_calendar.json");
            if (File.Exists(sessionCalendarSrc))
                File.Copy(sessionCalendarSrc, Path.Combine(configsRobot, "session_calendar.json"));

            var timetableJson = JsonSerializer.Serialize(new
            {
                as_of = DateTimeOffset.UtcNow.ToString("o"),
                trading_date = "2026-04-14",
                timezone = "America/Chicago",
                source = "playback_stall_live_finalize_test",
                is_replay = true,
                streams = new[]
                {
                    new { stream = "ES1", instrument = "MES", session = "S1", slot_time = "07:30", enabled = true }
                }
            });

            var timetablePath = Path.Combine(dataTimetable, "timetable_replay_current.json");
            File.WriteAllText(timetablePath, timetableJson);

            engine1 = new RobotEngine(
                tempRoot,
                TimeSpan.FromSeconds(60),
                ExecutionMode.SIM,
                customTimetablePath: timetablePath,
                instrument: "MES",
                masterInstrumentName: "MES",
                ignoreExistingStreamJournals: true,
                playbackAccountDetected: true,
                useAsyncLogging: false);
            engine2 = new RobotEngine(
                tempRoot,
                TimeSpan.FromSeconds(60),
                ExecutionMode.SIM,
                customTimetablePath: timetablePath,
                instrument: "MNQ",
                masterInstrumentName: "NQ",
                ignoreExistingStreamJournals: true,
                playbackAccountDetected: true,
                useAsyncLogging: false);

            engine1.Start();
            engine2.Start();
            engine1.StartEngineHeartbeatTimer();
            engine2.StartEngineHeartbeatTimer();
            PrimeHeartbeatForNextTimerEmission(engine1);
            PrimeHeartbeatForNextTimerEmission(engine2);

            var finalize = typeof(RobotEngine).GetMethod(
                "TryForcePlaybackStallLiveExposureFinalize",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (finalize == null)
                return "Could not find TryForcePlaybackStallLiveExposureFinalize by reflection.";

            finalize.Invoke(engine1, new object[]
            {
                DateTimeOffset.UtcNow,
                2,
                2,
                2,
                "MYM",
                "MYM",
                120.0,
                0,
                0
            });

            if (!WaitFor(() => engine2.IsShutdownRequested, TimeSpan.FromSeconds(3)))
                return "Playback stall live-exposure finalize did not request shutdown on sibling playback engine.";

            var shutdownSignalPath = Path.Combine(tempRoot, "runs", runId, RunRootArtifacts.RunShutdownSignalFileName);
            if (!File.Exists(shutdownSignalPath))
                return "Playback stall live-exposure finalize did not write RUN_SHUTDOWN.json.";

            var robotLogDir = Path.Combine(tempRoot, "runs", runId, "logs", "robot");
            if (!WaitForLogContains(robotLogDir, "PROCESS_RUN_STOP_BROADCAST", TimeSpan.FromSeconds(3)))
                return "Expected PROCESS_RUN_STOP_BROADCAST after playback stall live-exposure finalize. " +
                       DescribeRobotLogDir(robotLogDir, ReadRobotLogText(robotLogDir));
            if (!WaitForLogContains(robotLogDir, "PROCESS_RUN_STOP_COMPLETED", TimeSpan.FromSeconds(3)))
                return "Expected PROCESS_RUN_STOP_COMPLETED after playback stall live-exposure finalize. " +
                       DescribeRobotLogDir(robotLogDir, ReadRobotLogText(robotLogDir));
            if (!ReadRobotLogText(robotLogDir).Contains("ENGINE_PLAYBACK_STALL_QUIESCENCE_FORCE_FINALIZE_LIVE_EXPOSURE", StringComparison.Ordinal))
                return "Expected ENGINE_PLAYBACK_STALL_QUIESCENCE_FORCE_FINALIZE_LIVE_EXPOSURE audit event.";

            var afterStopLengths = SnapshotRobotLogLengths(robotLogDir);
            Thread.Sleep(1500);
            var afterWaitLengths = SnapshotRobotLogLengths(robotLogDir);
            if (!AreLengthSnapshotsEqual(afterStopLengths, afterWaitLengths))
                return "Playback stall live-exposure finalize allowed post-stop engine log growth; expected timers to be quiesced. " +
                       DescribeLengthDelta(afterStopLengths, afterWaitLengths) + " tail=" + ReadFallbackTail(robotLogDir);
        }
        finally
        {
            try { engine1?.Stop(writeRunSummary: false, stopSource: "test_cleanup"); } catch { /* best effort */ }
            try { engine2?.Stop(writeRunSummary: false, stopSource: "test_cleanup"); } catch { /* best effort */ }
            try { Environment.SetEnvironmentVariable("QTSW2_RUN_ID", priorRunId); } catch { /* best effort */ }
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch { /* best effort */ }
        }

        return null;
    }

    private static void PrimeHeartbeatForNextTimerEmission(RobotEngine engine)
    {
        var field = typeof(RobotEngine).GetField("_engineHeartbeatWallTick", BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(engine, 4);
    }

    private static string ReadRobotLogText(string robotLogDir)
    {
        if (!Directory.Exists(robotLogDir))
            return "";
        var chunks = Directory.EnumerateFiles(robotLogDir, "*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                try { return File.ReadAllText(p); }
                catch { return ""; }
            });
        return string.Join("\n", chunks);
    }

    private static Dictionary<string, long> SnapshotRobotLogLengths(string robotLogDir)
    {
        if (!Directory.Exists(robotLogDir))
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(robotLogDir, "*.jsonl", SearchOption.TopDirectoryOnly)
            .ToDictionary(p => Path.GetFileName(p), p =>
            {
                try { return new FileInfo(p).Length; }
                catch { return -1L; }
            }, StringComparer.OrdinalIgnoreCase);
    }

    private static bool AreLengthSnapshotsEqual(
        IReadOnlyDictionary<string, long> left,
        IReadOnlyDictionary<string, long> right)
    {
        if (left.Count != right.Count)
            return false;
        foreach (var kvp in left)
        {
            if (!right.TryGetValue(kvp.Key, out var value) || value != kvp.Value)
                return false;
        }
        return true;
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    private static bool WaitForLogContains(string robotLogDir, string value, TimeSpan timeout)
    {
        return WaitFor(() => ReadRobotLogText(robotLogDir).Contains(value, StringComparison.Ordinal), timeout);
    }

    private static string DescribeLengthDelta(
        IReadOnlyDictionary<string, long> left,
        IReadOnlyDictionary<string, long> right)
    {
        var keys = left.Keys.Concat(right.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        foreach (var key in keys)
        {
            left.TryGetValue(key, out var l);
            right.TryGetValue(key, out var r);
            if (l != r)
                parts.Add($"{key}:{l}->{r}");
        }
        return string.Join("; ", parts);
    }

    private static string ReadFallbackTail(string robotLogDir)
    {
        var path = Path.Combine(robotLogDir, "robot_ENGINE_fallback.jsonl");
        if (!File.Exists(path))
            return "";
        try
        {
            var lines = File.ReadLines(path).Reverse().Take(3).Reverse();
            var joined = string.Join(" | ", lines);
            return joined.Length <= 1000 ? joined : joined.Substring(Math.Max(0, joined.Length - 1000));
        }
        catch
        {
            return "";
        }
    }

    private static string DescribeRobotLogDir(string robotLogDir, string text)
    {
        var files = Directory.Exists(robotLogDir)
            ? string.Join(",", Directory.EnumerateFiles(robotLogDir, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName))
            : "<missing>";
        var sample = text.Length <= 500 ? text : text.Substring(0, 500);
        return $"dir={robotLogDir}; files={files}; sample={sample}";
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
