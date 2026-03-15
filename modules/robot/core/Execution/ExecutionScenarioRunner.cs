// Execution scenario runner for integrated system-level harness testing.
// Simulates broker events, collects canonical events, validates replay parity.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Result of a single scenario run.
/// </summary>
public sealed class ScenarioResult
{
    public string ScenarioName { get; set; } = "";
    public bool Pass { get; set; }
    public string? Error { get; set; }
    public string? FinalLifecycle { get; set; }
    public decimal ReplayExposure { get; set; }
    public bool ProtectiveBlock { get; set; }
    public bool MismatchFailClosed { get; set; }
    public bool ReplayMismatch { get; set; }
}

/// <summary>
/// Metrics for the scenario test suite.
/// </summary>
public sealed class ScenarioMetrics
{
    public int ScenarioPassCount { get; set; }
    public int ScenarioFailCount { get; set; }
    public int ReplayValidationFailures { get; set; }
}

/// <summary>
/// Runs execution scenarios, emits canonical events, validates replay parity.
/// </summary>
public static class ExecutionScenarioRunner
{
    /// <summary>
    /// Run chaos scenarios only (real-world failure simulations). Aborts on first failure.
    /// </summary>
    public static (bool AllPassed, ScenarioMetrics Metrics, List<ScenarioResult> Results) RunChaos(
        Action<string>? log = null)
    {
        return RunScenarios(ExecutionScenarioDefinitions.GetChaosScenarios(), log);
    }

    /// <summary>
    /// Run all scenarios in order. Aborts on first failure.
    /// </summary>
    public static (bool AllPassed, ScenarioMetrics Metrics, List<ScenarioResult> Results) RunAll(
        Action<string>? log = null)
    {
        return RunScenarios(ExecutionScenarioDefinitions.GetAll(), log);
    }

    private static (bool AllPassed, ScenarioMetrics Metrics, List<ScenarioResult> Results) RunScenarios(
        IReadOnlyList<ScenarioDefinition> scenarios,
        Action<string>? log)
    {
        var metrics = new ScenarioMetrics();
        var results = new List<ScenarioResult>();
        var tempBase = Path.Combine(Path.GetTempPath(), "QTSW2_ExecutionScenarios_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(tempBase);
            var tradingDate = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            var eventDir = Path.Combine(tempBase, "automation", "logs", "execution_events", tradingDate);
            Directory.CreateDirectory(eventDir);
            var logDir = Path.Combine(tempBase, "logs");
            Directory.CreateDirectory(logDir);

            var robotLog = new RobotLogger(tempBase, logDir, "SCENARIO");

            for (var i = 0; i < scenarios.Count; i++)
            {
                var scenario = scenarios[i];
                log?.Invoke($"Running scenario {i + 1}/{scenarios.Count}: {scenario.Name}");

                // Fresh temp dir per scenario for isolated event streams
                var scenarioDir = Path.Combine(tempBase, "scenario_" + i);
                Directory.CreateDirectory(scenarioDir);
                var scenarioEventDir = Path.Combine(scenarioDir, "automation", "logs", "execution_events", tradingDate);
                Directory.CreateDirectory(scenarioEventDir);

                var writer = new ExecutionEventWriter(scenarioDir, () => tradingDate, robotLog);

                var result = RunScenario(scenario, writer, scenarioDir, tradingDate);
                results.Add(result);

                if (result.Pass)
                {
                    metrics.ScenarioPassCount++;
                    log?.Invoke($"  RESULT: PASS");
                }
                else
                {
                    metrics.ScenarioFailCount++;
                    if (result.ReplayMismatch)
                        metrics.ReplayValidationFailures++;
                    log?.Invoke($"  RESULT: FAIL - {result.Error}");
                    return (false, metrics, results);
                }
            }

            return (true, metrics, results);
        }
        finally
        {
            try { Directory.Delete(tempBase, true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Run a single scenario: emit events, rebuild, validate.
    /// </summary>
    public static ScenarioResult RunScenario(ScenarioDefinition scenario, ExecutionEventWriter writer,
        string projectRoot, string tradingDate)
    {
        var ts = DateTimeOffset.UtcNow.ToString("o");

        foreach (var step in scenario.EventSteps)
        {
            var evt = StepToEvent(step, ts);
            writer.Emit(evt);
        }

        var events = ExecutionReplayReader.ReadAllEventsForTradingDate(projectRoot, tradingDate).ToList();
        var replayState = ExecutionReplayRebuilder.Rebuild(events);

        return ValidateScenario(scenario, replayState);
    }

    private static CanonicalExecutionEvent StepToEvent(EventStep step, string timestampUtc)
    {
        var evt = new CanonicalExecutionEvent
        {
            TimestampUtc = timestampUtc,
            EventType = step.Type,
            Instrument = step.Instrument ?? "ES",
            IntentId = step.IntentId,
            LifecycleStateBefore = step.LifecycleStateBefore,
            LifecycleStateAfter = step.LifecycleStateAfter,
            CommandId = step.CommandId,
            Source = "ExecutionScenario",
            Payload = step.Payload
        };
        return evt;
    }

    private static ScenarioResult ValidateScenario(ScenarioDefinition scenario, ReplayState replayState)
    {
        var expected = scenario.ExpectedState;
        var instrument = scenario.EventSteps.FirstOrDefault(s => !string.IsNullOrEmpty(s.Instrument))?.Instrument ?? "ES";

        var result = new ScenarioResult
        {
            ScenarioName = scenario.Name,
            Pass = true,
            FinalLifecycle = expected.IntentId != null && replayState.LifecycleByIntent.TryGetValue(expected.IntentId, out var lc)
                ? lc
                : null,
            ReplayExposure = expected.IntentId != null && replayState.OpenQuantityByIntent.TryGetValue(expected.IntentId, out var qty)
                ? qty
                : replayState.OpenQuantityByIntent.Values.Sum(),
            ProtectiveBlock = replayState.ProtectiveBlockedInstruments.Contains(instrument),
            MismatchFailClosed = replayState.MismatchFailClosedInstruments.Contains(instrument)
        };

        var protectiveBlocked = replayState.ProtectiveBlockedInstruments.Contains(instrument);
        var mismatchBlocked = replayState.MismatchFailClosedInstruments.Contains(instrument);
        var flattened = replayState.FlattenedInstruments.Contains(instrument);
        var terminal = expected.IntentId != null && replayState.TerminalIntents.Contains(expected.IntentId);
        var lifecycle = expected.IntentId != null && replayState.LifecycleByIntent.TryGetValue(expected.IntentId, out var lc2)
            ? lc2
            : null;
        var openQty = expected.IntentId != null && replayState.OpenQuantityByIntent.TryGetValue(expected.IntentId, out var oq)
            ? oq
            : replayState.OpenQuantityByIntent.Values.Sum();

        result.FinalLifecycle = lifecycle;
        result.ReplayExposure = openQty;
        result.ProtectiveBlock = protectiveBlocked;
        result.MismatchFailClosed = mismatchBlocked;

        var errors = new List<string>();

        if (expected.LifecycleState != null && lifecycle != expected.LifecycleState)
            errors.Add($"Lifecycle: expected {expected.LifecycleState}, got {lifecycle}");

        if (Math.Abs(expected.BrokerExposure - openQty) > 0.001m)
            errors.Add($"Broker exposure: expected {expected.BrokerExposure}, got {openQty}");

        if (expected.ProtectiveBlock != protectiveBlocked)
            errors.Add($"Protective block: expected {expected.ProtectiveBlock}, got {protectiveBlocked}");

        if (expected.MismatchState != "NONE" && !mismatchBlocked)
            errors.Add($"Mismatch state: expected {expected.MismatchState}, instrument should be fail-closed");
        else if (expected.MismatchState == "NONE" && mismatchBlocked)
            errors.Add($"Mismatch state: expected NONE, got fail-closed");

        if (expected.InstrumentBlocked && !protectiveBlocked && !mismatchBlocked)
            errors.Add($"Instrument blocked: expected true, got false");

        if (expected.Terminal && expected.IntentId != null && !terminal)
            errors.Add($"Terminal: expected intent {expected.IntentId} to be terminal");

        if (expected.PositionFlattened && !flattened)
            errors.Add($"Position flattened: expected true, got false");

        if (errors.Count > 0)
        {
            result.Pass = false;
            result.Error = string.Join("; ", errors);
            result.ReplayMismatch = true;
        }

        return result;
    }
}
