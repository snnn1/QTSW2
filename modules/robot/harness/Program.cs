using System.Text.Json;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;
using QTSW2.Robot.Core.Tests;
using QTSW2.Robot.Harness;
using DateOnly = QTSW2.Robot.Core.DateOnly; // Use compat shim instead of System.DateOnly

static void PrintUsage()
{
    Console.WriteLine("Robot Harness (non-NinjaTrader)");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --write-sample-timetable");
    Console.WriteLine("  dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --validate-forced-flatten");
    Console.WriteLine("  dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --validate-slot-expiry");
    Console.WriteLine("  dotnet run --project modules/robot/harness/Robot.Harness.csproj [--mode DRYRUN|SIM|LIVE] [--test SCENARIO]");
    Console.WriteLine("  dotnet run --project modules/robot/harness/Robot.Harness.csproj --mode DRYRUN --replay --start YYYY-MM-DD --end YYYY-MM-DD [--log-dir DIR] [--timetable-path PATH]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --mode DRYRUN|SIM|LIVE  Execution mode (default: DRYRUN)");
    Console.WriteLine("  --replay                Replay historical bars from snapshot (requires --start and --end)");
    Console.WriteLine("  --start YYYY-MM-DD      Start date for replay (Chicago timezone)");
    Console.WriteLine("  --end YYYY-MM-DD        End date for replay (Chicago timezone)");
    Console.WriteLine("  --log-dir DIR           Custom log directory (default: logs/robot/)");
    Console.WriteLine("  --timetable-path PATH   Custom timetable file path (default: data/timetable/timetable_current.json)");
    Console.WriteLine();
    Console.WriteLine("Notes:");
    Console.WriteLine("- Engine reads ONLY from:");
    Console.WriteLine("  configs/analyzer_robot_parity.json");
    Console.WriteLine("  data/timetable/timetable_current.json");
    Console.WriteLine("  data/translated/ (when --replay is used)");
    Console.WriteLine("- Logs: logs/robot/robot_skeleton.jsonl (or --log-dir if specified)");
    Console.WriteLine("- Journal: logs/robot/journal/<trading_date>_<stream>.json");
}

var argsList = args.ToList();
if (argsList.Contains("--help") || argsList.Contains("-h"))
{
    PrintUsage();
    return;
}

// --test DST: run session close fallback timezone tests (DST boundary weeks)
// --test TERMINAL_INTENT: run terminal intent hardening tests (IsIntentCompleted, BE exclusion)
// --test IEA_FLATTEN: run IEA flatten authority tests (exposure-reduction invariant)
// --test PHASE5_HARDENING: run Phase 5 hardening tests (kill switch, RiskGate, hysteresis)
// --test CHAOS: run chaos scenarios (stop cancel, mismatch, queue poison, forced flatten)
// --test RANDOM_STRESS: run randomized event stress test (default 60s, use --stress-duration 300 for 5 min)
// --test MISMATCH_ESCALATION: run Gap 4 mismatch escalation tests
// --test STATE_CONSISTENCY_GATE: run P1.5 closed-loop state-consistency gate tests
// --test EXECUTION_EVENT_REPLAY: run Gap 5 canonical event replay tests
// --test INTENT_LIFECYCLE: run intent lifecycle state machine tests (transitions, command legality)
// --test EXECUTION_ORDERING: run execution event ordering hardening tests (deferred resolution, dedup)
var testIndex = argsList.IndexOf("--test");
if (testIndex >= 0 && testIndex + 1 < argsList.Count)
{
    var testName = argsList[testIndex + 1];
    if (testName.Equals("DST", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = SessionCloseFallbackTimeZoneTests.RunDstBoundaryTests();
        Console.WriteLine(pass ? "PASS: DST boundary tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("TERMINAL_INTENT", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = TerminalIntentHardeningTests.RunTerminalIntentTests();
        Console.WriteLine(pass ? "PASS: Terminal intent hardening tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("IEA_FLATTEN", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = IeaFlattenAuthorityTests.RunIeaFlattenTests();
        Console.WriteLine(pass ? "PASS: IEA flatten authority tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("ORDER_REGISTRY", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = OrderRegistryTests.RunOrderRegistryTests();
        Console.WriteLine(pass ? "PASS: Order registry tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("SHARED_ADOPTED_ORDER_REGISTRY", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = SharedAdoptedOrderRegistryTests.RunSharedAdoptedOrderRegistryTests();
        Console.WriteLine(pass ? "PASS: Shared adopted order registry tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("RECOVERY_PHASE3", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = RecoveryPhase3Tests.RunRecoveryPhase3Tests();
        Console.WriteLine(pass ? "PASS: Recovery Phase 3 tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("RECOVERY_P2_PHASE1", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = P2RecoveryPhase1Tests.RunP2RecoveryPhase1Tests();
        Console.WriteLine(pass ? "PASS: P2 Phase 1 recovery attribution tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("P2_6_DESTRUCTIVE_POLICY_TESTS", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = P2_6DestructivePolicyTests.RunP2_6DestructivePolicyTests();
        Console.WriteLine(pass ? "PASS: P2.6 destructive policy tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("RANGE_BUILDING_SNAPSHOT", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = RangeBuildingSnapshotTests.RunRangeBuildingSnapshotTests();
        Console.WriteLine(pass ? "PASS: RANGE_BUILDING snapshot tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("BREAKOUT_EXECUTION_DECISION", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = BreakoutExecutionDecisionTests.RunBreakoutExecutionDecisionTests();
        Console.WriteLine(pass ? "PASS: Breakout execution decision tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("MIXED_STOP_MARKET_ENTRY", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = MixedStopMarketEntryTests.RunMixedStopMarketEntryTests();
        Console.WriteLine(pass ? "PASS: Mixed STOP/MARKET entry tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("BOOTSTRAP_PHASE4", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = BootstrapPhase4Tests.RunBootstrapPhase4Tests();
        Console.WriteLine(pass ? "PASS: Bootstrap Phase 4 tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("ADOPTION_RECOVERY", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = AdoptionRecoveryTests.RunAdoptionRecoveryTests();
        Console.WriteLine(pass ? "PASS: Adoption recovery tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("DELAYED_JOURNAL_VISIBILITY", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = DelayedJournalVisibilityTests.RunDelayedJournalVisibilityTests();
        Console.WriteLine(pass ? "PASS: Delayed journal visibility tests (adoption-on-restart)" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("UNMATCHED_POSITION_POLICY", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = UnmatchedPositionPolicyTests.RunUnmatchedPositionPolicyTests();
        Console.WriteLine(pass ? "PASS: Unmatched position policy tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("RECONCILIATION_RECOVERY_SCENARIOS", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = ReconciliationRecoveryScenarioTests.RunReconciliationRecoveryScenarioTests();
        Console.WriteLine(pass ? "PASS: Reconciliation recovery scenario tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("DATA_STALL_DETECTION", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = HealthMonitorDataStallTests.RunDataStallDetectionTests();
        Console.WriteLine(pass ? "PASS: Data stall detection tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("SUPERVISORY_PHASE5", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = SupervisoryPhase5Tests.RunSupervisoryPhase5Tests();
        Console.WriteLine(pass ? "PASS: Supervisory Phase 5 tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("PHASE5_HARDENING", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = Phase5HardeningTests.RunPhase5HardeningTests();
        Console.WriteLine(pass ? "PASS: Phase 5 hardening tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("EXECUTION_COMMANDS", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = ExecutionCommandTests.RunExecutionCommandTests();
        Console.WriteLine(pass ? "PASS: Execution command tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("IEA_ALIGNMENT", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = ForcedFlattenSlotExpiryReentryAlignmentTests.RunIeaAlignmentTests();
        Console.WriteLine(pass ? "PASS: IEA alignment tests (forced flatten, slot expiry, reentry)" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("REENTRY_PROTECTION", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = ReentryProtectionAcceptanceTests.RunReentryProtectionTests();
        Console.WriteLine(pass ? "PASS: Reentry protection acceptance tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("REENTRY_TIMING", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = ReentryTimingTests.RunReentryTimingTests();
        Console.WriteLine(pass ? "PASS: Reentry timing tests (holiday, early close, delayed open, fallback)" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("INTENT_LIFECYCLE", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = IntentLifecycleTests.RunIntentLifecycleTests();
        Console.WriteLine(pass ? "PASS: Intent lifecycle tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("EXECUTION_ORDERING", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = ExecutionEventOrderingTests.RunExecutionEventOrderingTests();
        Console.WriteLine(pass ? "PASS: Execution event ordering tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("PROTECTIVE_AUDIT", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = ProtectiveCoverageAuditTests.RunProtectiveAuditTests();
        Console.WriteLine(pass ? "PASS: Protective coverage audit tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("MISMATCH_ESCALATION", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = MismatchEscalationTests.RunMismatchEscalationTests();
        Console.WriteLine(pass ? "PASS: Mismatch escalation tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("STATE_CONSISTENCY_GATE", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = StateConsistencyGateTests.RunStateConsistencyGateTests();
        Console.WriteLine(pass ? "PASS: State consistency gate tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("EXECUTION_EVENT_REPLAY", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = ExecutionEventReplayTests.RunExecutionEventReplayTests();
        Console.WriteLine(pass ? "PASS: Execution event replay tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("ORDER_RECONCILIATION", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = OrderReconciliationRecoveryTests.RunOrderReconciliationRecoveryTests();
        Console.WriteLine(pass ? "PASS: Order reconciliation recovery tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("BREAKOUT_VALIDITY_GATE", StringComparison.OrdinalIgnoreCase))
    {
        var (pass, err) = BreakoutValidityGateTests.RunBreakoutValidityGateTests();
        Console.WriteLine(pass ? "PASS: Breakout validity gate tests" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("CHAOS", StringComparison.OrdinalIgnoreCase))
    {
        RunChaosTests();
        return;
    }
    else if (testName.Equals("RANDOM_STRESS", StringComparison.OrdinalIgnoreCase))
    {
        var durationIndex = argsList.IndexOf("--stress-duration");
        var durationSec = (durationIndex >= 0 && durationIndex + 1 < argsList.Count && int.TryParse(argsList[durationIndex + 1], out var d))
            ? d : 60;
        var (pass, err) = RandomEventStressTests.RunRandomStressTests(durationSec);
        Console.WriteLine(pass ? $"PASS: Random stress test ({durationSec}s)" : $"FAIL: {err}");
        Environment.Exit(pass ? 0 : 1);
    }
    else if (testName.Equals("EXECUTION_SCENARIOS", StringComparison.OrdinalIgnoreCase))
    {
        RunExecutionScenarioTests();
        return;
    }
}

static void RunChaosTests()
{
    Console.WriteLine("=== Chaos Tests (Real-World Failure Simulation) ===");
    Console.WriteLine("  Test B: Stop cancel recovery");
    Console.WriteLine("  Test C: Protective recovery failure -> emergency flatten");
    Console.WriteLine("  Test D: Persistent broker mismatch -> fail-closed");
    Console.WriteLine("  Test E: Queue poison -> instrument frozen -> emergency flatten");
    Console.WriteLine("  Test F: Session forced flatten near close");
    Console.WriteLine();

    var (allPassed, metrics, results) = ExecutionScenarioRunner.RunChaos(msg => Console.WriteLine(msg));

    foreach (var r in results)
    {
        Console.WriteLine();
        Console.WriteLine($"Scenario: {r.ScenarioName}");
        Console.WriteLine($"  Final lifecycle: {r.FinalLifecycle ?? "N/A"}");
        Console.WriteLine($"  Replay exposure: {r.ReplayExposure}");
        Console.WriteLine($"  Protective block: {r.ProtectiveBlock}");
        Console.WriteLine($"  Mismatch fail-closed: {r.MismatchFailClosed}");
        Console.WriteLine($"  RESULT: {(r.Pass ? "PASS" : "FAIL")}");
        if (!r.Pass && !string.IsNullOrEmpty(r.Error))
            Console.WriteLine($"  REPLAY_STATE_MISMATCH: {r.Error}");
    }

    Console.WriteLine();
    Console.WriteLine("=== Chaos Summary ===");
    Console.WriteLine($"  chaos_pass_count: {metrics.ScenarioPassCount}");
    Console.WriteLine($"  chaos_fail_count: {metrics.ScenarioFailCount}");
    Console.WriteLine();

    if (!allPassed)
    {
        Console.WriteLine("CHAOS TESTS FAILED");
        Environment.Exit(1);
    }
    Console.WriteLine("ALL CHAOS TESTS PASSED");
    Environment.Exit(0);
}

static void RunExecutionScenarioTests()
{
    Console.WriteLine("=== Execution Scenario Harness Tests ===");
    Console.WriteLine();

    var (allPassed, metrics, results) = ExecutionScenarioRunner.RunAll(msg => Console.WriteLine(msg));

    foreach (var r in results)
    {
        Console.WriteLine();
        Console.WriteLine($"Scenario: {r.ScenarioName}");
        Console.WriteLine($"  Final lifecycle: {r.FinalLifecycle ?? "N/A"}");
        Console.WriteLine($"  Replay exposure: {r.ReplayExposure}");
        Console.WriteLine($"  Protective block: {r.ProtectiveBlock}");
        Console.WriteLine($"  Mismatch fail-closed: {r.MismatchFailClosed}");
        Console.WriteLine($"  RESULT: {(r.Pass ? "PASS" : "FAIL")}");
        if (!r.Pass && !string.IsNullOrEmpty(r.Error))
            Console.WriteLine($"  REPLAY_STATE_MISMATCH: {r.Error}");
    }

    Console.WriteLine();
    Console.WriteLine("=== Summary ===");
    Console.WriteLine($"  scenario_pass_count: {metrics.ScenarioPassCount}");
    Console.WriteLine($"  scenario_fail_count: {metrics.ScenarioFailCount}");
    Console.WriteLine($"  replay_validation_failures: {metrics.ReplayValidationFailures}");
    Console.WriteLine();

    if (!allPassed)
    {
        Console.WriteLine("SOME SCENARIOS FAILED");
        Environment.Exit(1);
    }

    Console.WriteLine("=== Unit Tests ===");
    var (ut1, e1) = ExecutionScenarioTests.TestScenarioRunnerExecutes();
    Console.WriteLine(ut1 ? "  TestScenarioRunnerExecutes: PASS" : $"  TestScenarioRunnerExecutes: FAIL - {e1}");
    var (ut2, e2) = ExecutionScenarioTests.TestReplayValidationRuns();
    Console.WriteLine(ut2 ? "  TestReplayValidationRuns: PASS" : $"  TestReplayValidationRuns: FAIL - {e2}");
    var (ut3, e3) = ExecutionScenarioTests.TestFailureConditionsDetected();
    Console.WriteLine(ut3 ? "  TestFailureConditionsDetected: PASS" : $"  TestFailureConditionsDetected: FAIL - {e3}");
    Console.WriteLine();

    var unitPass = ut1 && ut2 && ut3;
    Console.WriteLine(unitPass ? "ALL SCENARIOS AND UNIT TESTS PASSED" : "UNIT TESTS FAILED");
    Environment.Exit(unitPass ? 0 : 1);
}

var root = ProjectRootResolver.ResolveProjectRoot();

// Parse timetable path argument
string? customTimetablePath = null;
var timetablePathIndex = argsList.IndexOf("--timetable-path");
if (timetablePathIndex >= 0 && timetablePathIndex + 1 < argsList.Count)
{
    customTimetablePath = argsList[timetablePathIndex + 1];
    if (!Path.IsPathRooted(customTimetablePath))
    {
        // If relative path, resolve relative to project root
        customTimetablePath = Path.Combine(root, customTimetablePath);
    }
    if (!File.Exists(customTimetablePath))
    {
        Console.Error.WriteLine($"Error: Timetable file not found: {customTimetablePath}");
        return;
    }
}

var timetablePath = customTimetablePath ?? Path.Combine(root, "data", "timetable", "timetable_current.json");

if (argsList.Contains("--write-sample-timetable"))
{
    // Do not write timetable_current.json from harness — live file is published only via TimetableEngine (Python).
    var samplePath = Path.Combine(root, "data", "timetable", "timetable_harness_sample.json");
    Directory.CreateDirectory(Path.GetDirectoryName(samplePath)!);
    var spec = ParitySpec.LoadFromFile(Path.Combine(root, "configs", "analyzer_robot_parity.json"));
    var time = new TimeService(spec.timezone);
    var utcNow = DateTimeOffset.UtcNow;
    var tradingDate = time.GetChicagoDateToday(utcNow).ToString("yyyy-MM-dd");

    var sample = new
    {
        as_of = time.GetChicagoNow(utcNow).ToString("o"),
        trading_date = tradingDate,
        timezone = "America/Chicago",
        source = "harness_sample",
        streams = new[]
        {
            new { stream = "ES1", instrument = "ES", session = "S1", slot_time = "09:00", enabled = true },
            new { stream = "ES2", instrument = "ES", session = "S2", slot_time = "09:30", enabled = true }
        }
    };

    File.WriteAllText(samplePath, JsonSerializer.Serialize(sample));
    Console.WriteLine($"Wrote sample to {samplePath} (not live timetable_current — use TimetableEngine / dashboard / matrix publish).");
}

Console.WriteLine($"Project root: {root}");
Console.WriteLine($"Timetable path: {timetablePath}");
if (customTimetablePath != null)
{
    Console.WriteLine($"Using custom timetable: {customTimetablePath}");
}

// Parse execution mode argument (fail-closed validation)
var executionMode = ExecutionMode.DRYRUN;
var modeIndex = argsList.IndexOf("--mode");
if (modeIndex >= 0 && modeIndex + 1 < argsList.Count)
{
    var modeStr = argsList[modeIndex + 1];
    if (!Enum.TryParse<ExecutionMode>(modeStr, ignoreCase: true, out var parsedMode))
    {
        Console.Error.WriteLine($"Error: Invalid execution mode '{modeStr}'. Must be DRYRUN, SIM, or LIVE.");
        return;
    }
    executionMode = parsedMode;
    
    // LIVE mode requires explicit confirmation (Phase C)
    if (executionMode == ExecutionMode.LIVE)
    {
        Console.Error.WriteLine("Error: LIVE mode is not yet enabled. Use DRYRUN or SIM.");
        return;
    }
}

// Load spec and time service early (needed for replay)
var specLoaded = ParitySpec.LoadFromFile(Path.Combine(root, "configs", "analyzer_robot_parity.json"));
var timeSvc = new TimeService(specLoaded.timezone);

// Handle replay mode
if (argsList.Contains("--replay"))
{
    var startIndex = argsList.IndexOf("--start");
    var endIndex = argsList.IndexOf("--end");
    if (startIndex < 0 || startIndex + 1 >= argsList.Count || endIndex < 0 || endIndex + 1 >= argsList.Count)
    {
        Console.Error.WriteLine("Error: --replay requires --start YYYY-MM-DD and --end YYYY-MM-DD");
        return;
    }
    if (!DateOnly.TryParse(argsList[startIndex + 1], out var startDate))
    {
        Console.Error.WriteLine($"Error: Invalid start date: {argsList[startIndex + 1]}");
        return;
    }
    if (!DateOnly.TryParse(argsList[endIndex + 1], out var endDate))
    {
        Console.Error.WriteLine($"Error: Invalid end date: {argsList[endIndex + 1]}");
        return;
    }
    string? logDir = null;
    var logDirIndex = argsList.IndexOf("--log-dir");
    if (logDirIndex >= 0 && logDirIndex + 1 < argsList.Count)
        logDir = argsList[logDirIndex + 1];
    
    // Replay consumes timetable_replay_current.json (never overwrites live timetable_current.json).
    string? engineTimetablePath = customTimetablePath;
    if (engineTimetablePath == null)
    {
        HistoricalReplay.UpdateTimetableTradingDateIfReplay(root, startDate, timeSvc);
        var replayCurrent = HistoricalReplay.ReplayCurrentTimetablePath(root);
        if (File.Exists(replayCurrent))
        {
            engineTimetablePath = replayCurrent;
            Console.WriteLine("Replay mode: Using timetable_replay_current.json");
        }
        else
        {
            var replayTemplate = Path.Combine(root, "data", "timetable", "timetable_replay.json");
            if (File.Exists(replayTemplate))
            {
                engineTimetablePath = replayTemplate;
                Console.WriteLine("Replay mode: Using timetable_replay.json (replay_current missing)");
            }
        }
    }
    
    Console.WriteLine($"Replay mode: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
    if (logDir != null)
        Console.WriteLine($"Custom log directory: {logDir}");
    
    var engine = new RobotEngine(root, TimeSpan.FromSeconds(1), executionMode, logDir, engineTimetablePath);
    engine.Start();
    var snapshotRoot = Path.Combine(root, "data", "translated");
    HistoricalReplay.Replay(engine, specLoaded, timeSvc, snapshotRoot, root, startDate, endDate);
    engine.Stop();
    Console.WriteLine($"Done. Inspect logs in: {logDir ?? "logs/robot/"}");
    return;
}

// --validate-forced-flatten: simulate through session close to verify forced flatten triggers
var validateForcedFlatten = argsList.Contains("--validate-forced-flatten");
if (validateForcedFlatten)
{
    var testDate = new DateOnly(2026, 3, 4); // Wednesday, CST (before DST)
    var testTimetablePath = Path.Combine(root, "data", "timetable", "timetable_validate_forced_flatten.json");
    Directory.CreateDirectory(Path.GetDirectoryName(testTimetablePath)!);
    var timetableJson = JsonSerializer.Serialize(new
    {
        as_of = timeSvc.ConvertUtcToChicago(DateTimeOffset.UtcNow).ToString("o"),
        trading_date = testDate.ToString("yyyy-MM-dd"),
        timezone = "America/Chicago",
        source = "validate-forced-flatten",
        streams = new[]
        {
            new { stream = "ES1", instrument = "MES", session = "S1", slot_time = "07:30", enabled = true },
            new { stream = "ES2", instrument = "MES", session = "S2", slot_time = "09:30", enabled = true }
        }
    });
    File.WriteAllText(testTimetablePath, timetableJson);
    Console.WriteLine($"Validate forced flatten: test date {testDate:yyyy-MM-dd}, timetable: {testTimetablePath}");

    var engine = new RobotEngine(root, TimeSpan.FromSeconds(1), executionMode, null, testTimetablePath, "MES");
    engine.Start();
    Thread.Sleep(800); // Allow initial timetable poll so trading date is locked

    // Simulate from 15:50 CT to 16:05 CT (brackets flatten trigger at 15:55 CT)
    var simStartChicago = timeSvc.ConstructChicagoTime(testDate, "15:50");
    var simEndChicago = timeSvc.ConstructChicagoTime(testDate, "16:05");
    var simStart = timeSvc.ConvertChicagoToUtc(simStartChicago);
    var simEnd = timeSvc.ConvertChicagoToUtc(simEndChicago);

    Console.WriteLine($"Simulating from {simStart:o} to {simEnd:o} (UTC) — flatten trigger at 15:55 CT");

    var rng = new Random(1);
    decimal px = 5000m;
    for (var t = simStart; t <= simEnd; t = t.AddMinutes(1))
    {
        var delta = (decimal)(rng.NextDouble() - 0.5) * 2m;
        var open = px;
        var high = open + Math.Abs(delta) + 0.25m;
        var low = open - Math.Abs(delta) - 0.25m;
        var close = open + delta;
        px = close;
        engine.OnBar(t, "MES", open, high, low, close, t);
        engine.Tick(t);
    }

    engine.Stop();

    var logDir = Path.Combine(root, "logs", "robot");
    var journalDir = Path.Combine(logDir, "journal");
    var markersPath = Path.Combine(journalDir, "_forced_flatten_markers.json");
    var hasTriggered = File.Exists(markersPath);
    var markersContent = hasTriggered ? File.ReadAllText(markersPath) : "";

    // Parse log files for forced flatten events (Test 1: Pre-close flatten trigger)
    var logFiles = Directory.Exists(logDir) ? Directory.GetFiles(logDir, "*.jsonl") : Array.Empty<string>();
    var hasForcedFlattenTriggered = false;
    var hasForcedFlattenPositionClosed = false;
    var hasForcedFlattenOrdersCancelled = false;
    var hasNoTradeForcedFlatten = false;
    foreach (var logFile in logFiles)
    {
        try
        {
            var lines = File.ReadAllLines(logFile);
            foreach (var line in lines)
            {
                if (line.Contains("FORCED_FLATTEN_TRIGGERED")) hasForcedFlattenTriggered = true;
                if (line.Contains("FORCED_FLATTEN_POSITION_CLOSED")) hasForcedFlattenPositionClosed = true;
                if (line.Contains("FORCED_FLATTEN_ORDERS_CANCELLED")) hasForcedFlattenOrdersCancelled = true;
                if (line.Contains("NO_TRADE_FORCED_FLATTEN_PRE_ENTRY")) hasNoTradeForcedFlatten = true;
            }
        }
        catch { /* skip */ }
    }

    Console.WriteLine();
    Console.WriteLine("=== Forced Flatten Validation ===");
    Console.WriteLine($"  FORCED_FLATTEN_TRIGGERED: {(hasForcedFlattenTriggered ? "FOUND" : "MISSING")}");
    Console.WriteLine($"  FORCED_FLATTEN_POSITION_CLOSED: {(hasForcedFlattenPositionClosed ? "FOUND" : "MISSING (expected in DRYRUN - no execution adapter)")}");
    Console.WriteLine($"  FORCED_FLATTEN_ORDERS_CANCELLED: {(hasForcedFlattenOrdersCancelled ? "FOUND" : "MISSING (expected in DRYRUN)")}");
    Console.WriteLine($"  NO_TRADE_FORCED_FLATTEN_PRE_ENTRY: {(hasNoTradeForcedFlatten ? "FOUND" : "MISSING")}");
    Console.WriteLine($"  _forced_flatten_markers.json: {(hasTriggered ? "EXISTS" : "MISSING")} at {markersPath}");
    if (hasTriggered)
        Console.WriteLine($"  Markers content: {markersContent.Trim()}");
    Console.WriteLine($"  Slot journals (ForcedFlattenTimestamp): {journalDir}");
    Console.WriteLine();
    Console.WriteLine("Note: DRYRUN mode has no execution adapter. For full post-entry flatten validation (position closed, orders cancelled), run in SIM mode with NinjaTrader.");
    Console.WriteLine("Done. Run: python scripts/check_forced_flatten_today.py " + testDate.ToString("yyyy-MM-dd"));
    return;
}

// --validate-slot-expiry: simulate across two days to verify slot expiry triggers at next slot time
// Timetable stays at day1 (trading date locked). Forced flatten runs first (15:55 CT), then slot expiry.
// Slot expiry fires for streams still ACTIVE at NextSlotTimeUtc (e.g. reentry at 09:30 for ES2).
var validateSlotExpiry = argsList.Contains("--validate-slot-expiry");
if (validateSlotExpiry)
{
    var day1 = new DateOnly(2026, 3, 18); // Wednesday (fresh date to avoid stale journals)
    var day2 = new DateOnly(2026, 3, 19); // Thursday
    var testTimetablePath = Path.Combine(root, "data", "timetable", "timetable_validate_slot_expiry.json");
    Directory.CreateDirectory(Path.GetDirectoryName(testTimetablePath)!);

    var timetableJson = JsonSerializer.Serialize(new
    {
        as_of = timeSvc.ConvertUtcToChicago(DateTimeOffset.UtcNow).ToString("o"),
        trading_date = day1.ToString("yyyy-MM-dd"),
        timezone = "America/Chicago",
        source = "validate-slot-expiry",
        streams = new[]
        {
            new { stream = "ES1", instrument = "MES", session = "S1", slot_time = "07:30", enabled = true },
            new { stream = "ES2", instrument = "MES", session = "S2", slot_time = "09:30", enabled = true }
        }
    });
    File.WriteAllText(testTimetablePath, timetableJson);
    Console.WriteLine($"Validate slot expiry: day1={day1:yyyy-MM-dd}, NextSlotTimeUtc for ES1=day2 07:30 CT");

    var engine = new RobotEngine(root, TimeSpan.FromSeconds(1), executionMode, null, testTimetablePath, "MES");
    engine.Start();
    Thread.Sleep(800);

    // Day 1: 07:00–08:00 CT (streams created, NextSlotTimeUtc = day2 07:30 CT)
    var day1StartUtc = timeSvc.ConvertChicagoToUtc(timeSvc.ConstructChicagoTime(day1, "07:00"));
    var day1EndUtc = timeSvc.ConvertChicagoToUtc(timeSvc.ConstructChicagoTime(day1, "08:00"));
    Console.WriteLine($"Day 1: 07:00–08:00 CT (streams stay ACTIVE, no forced flatten)");

    var rng = new Random(1);
    decimal px = 5000m;
    for (var t = day1StartUtc; t <= day1EndUtc; t = t.AddMinutes(1))
    {
        var delta = (decimal)(rng.NextDouble() - 0.5) * 2m;
        var open = px;
        var high = open + Math.Abs(delta) + 0.25m;
        var low = open - Math.Abs(delta) - 0.25m;
        var close = open + delta;
        px = close;
        engine.OnBar(t, "MES", open, high, low, close, t);
        engine.Tick(t);
    }

    // Day 2: Tick only (no bars - they'd be rejected as future).
    // Forced flatten runs first (15:55 CT day1). Then slot expiry: ES1 at 07:30, ES2 at 09:30.
    // Simulate to 10:00 CT to hit ES2's slot expiry (reentry case).
    var day2StartUtc = timeSvc.ConvertChicagoToUtc(timeSvc.ConstructChicagoTime(day2, "07:30"));
    var day2EndUtc = timeSvc.ConvertChicagoToUtc(timeSvc.ConstructChicagoTime(day2, "10:00"));
    Console.WriteLine($"Day 2: Tick from 07:30–10:00 CT (forced flatten first, then slot expiry for ES2 at 09:30)");

    for (var t = day2StartUtc; t <= day2EndUtc; t = t.AddMinutes(1))
    {
        engine.Tick(t); // No OnBar - day2 bars would be rejected; Tick alone triggers forced flatten then slot expiry
    }

    engine.Stop();

    var logDir = Path.Combine(root, "logs", "robot");
    var journalDir = Path.Combine(logDir, "journal");

    var logFiles = Directory.Exists(logDir) ? Directory.GetFiles(logDir, "*.jsonl") : Array.Empty<string>();
    var hasSlotExpired = false;
    var hasSlotStatusChanged = false;
    foreach (var logFile in logFiles)
    {
        try
        {
            foreach (var line in File.ReadAllLines(logFile))
            {
                if (line.Contains("SLOT_EXPIRED")) hasSlotExpired = true;
                if (line.Contains("SLOT_STATUS_CHANGED") && line.Contains("EXPIRED")) hasSlotStatusChanged = true;
            }
        }
        catch { /* skip */ }
    }

    var slotExpiredJournals = new List<string>();
    if (Directory.Exists(journalDir))
    {
        foreach (var f in Directory.GetFiles(journalDir, $"{day1:yyyy-MM-dd}_*.json"))
        {
            try
            {
                var json = File.ReadAllText(f);
                if (json.Contains("\"CommitReason\":\"SLOT_EXPIRED\"") || json.Contains("\"SlotStatus\":4"))
                    slotExpiredJournals.Add(Path.GetFileName(f));
            }
            catch { /* skip */ }
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== Slot Expiry Validation ===");
    Console.WriteLine($"  SLOT_EXPIRED in logs: {(hasSlotExpired ? "FOUND" : "MISSING")}");
    Console.WriteLine($"  SLOT_STATUS_CHANGED -> EXPIRED: {(hasSlotStatusChanged ? "FOUND" : "MISSING")}");
    Console.WriteLine($"  Journals with SLOT_EXPIRED: {(slotExpiredJournals.Count > 0 ? string.Join(", ", slotExpiredJournals) : "NONE")}");
    Console.WriteLine();
    Console.WriteLine("Note: stream.Tick runs before forced flatten so slot expiry can commit at NextSlotTimeUtc.");
    return;
}

// Non-replay path: synthetic bar generation
{
    var engine = new RobotEngine(root, TimeSpan.FromSeconds(1), executionMode, null, customTimetablePath);
    engine.Start();

// Simulate time progression and bars (UTC-based)
var startUtc = DateTimeOffset.UtcNow;
var chicagoDate = timeSvc.GetChicagoDateToday(startUtc);

// Pick ES1 window: S1 starts 02:00 CT, slot_time 07:30 CT (from sample)
var rangeStartUtc = timeSvc.ConvertChicagoLocalToUtc(chicagoDate, specLoaded.sessions["S1"].range_start_time);
var slotUtc = timeSvc.ConvertChicagoLocalToUtc(chicagoDate, "07:30");
var closeUtc = timeSvc.ConvertChicagoLocalToUtc(chicagoDate, specLoaded.entry_cutoff.market_close_time);

// If it's already after close in real time, just run a short tick loop for wiring verification.
var simStart = startUtc < rangeStartUtc ? startUtc : rangeStartUtc.AddMinutes(-1);
var simEnd = startUtc < closeUtc ? closeUtc.AddMinutes(1) : startUtc.AddMinutes(2);

Console.WriteLine($"Simulating from {simStart:o} to {simEnd:o} (UTC)");

var rng = new Random(1);
decimal px = 5000m;

for (var t = simStart; t <= simEnd; t = t.AddMinutes(1))
{
    // Emit a bar each minute for ES
    var delta = (decimal)(rng.NextDouble() - 0.5) * 2m;
    var open = px;
    var high = open + Math.Abs(delta) + 0.25m;
    var low = open - Math.Abs(delta) - 0.25m;
    var close = open + delta;
    px = close;

    engine.OnBar(t, "ES", open, high, low, close, t);
    engine.Tick(t);
}

    engine.Stop();
    Console.WriteLine("Done. Inspect logs/robot/robot_skeleton.jsonl and logs/robot/journal/ for output.");
}

