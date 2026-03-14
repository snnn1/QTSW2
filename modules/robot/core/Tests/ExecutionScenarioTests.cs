// Execution scenario harness tests.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test EXECUTION_SCENARIOS
// Unit tests verify scenario runner executes, replay validation runs, failure conditions detected.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ExecutionScenarioTests
{
    /// <summary>
    /// Run full execution scenario suite. Returns (pass, error).
    /// </summary>
    public static (bool Pass, string? Error) RunExecutionScenarioTests()
    {
        var (allPassed, metrics, results) = ExecutionScenarioRunner.RunAll(null);
        if (allPassed)
            return (true, null);
        var failed = results.FirstOrDefault(r => !r.Pass);
        return (false, failed?.Error ?? "Unknown failure");
    }

    /// <summary>
    /// Unit test: scenario runner executes and produces results.
    /// </summary>
    public static (bool Pass, string? Error) TestScenarioRunnerExecutes()
    {
        var scenarios = ExecutionScenarioDefinitions.GetAll();
        if (scenarios.Count != 8)
            return (false, $"Expected 8 scenarios, got {scenarios.Count}");

        var (allPassed, metrics, results) = ExecutionScenarioRunner.RunAll(null);
        if (results.Count != 8)
            return (false, $"Expected 8 results, got {results.Count}");
        if (!allPassed)
            return (false, $"Scenarios failed: {results.First(r => !r.Pass).Error}");
        return (true, null);
    }

    /// <summary>
    /// Unit test: replay validation runs and compares state.
    /// </summary>
    public static (bool Pass, string? Error) TestReplayValidationRuns()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "QTSW2_ScenarioReplay_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(tempDir);
            var tradingDate = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            var eventDir = Path.Combine(tempDir, "automation", "logs", "execution_events", tradingDate);
            Directory.CreateDirectory(eventDir);

            var log = new RobotLogger(tempDir, Path.Combine(tempDir, "logs"), "TEST");
            var writer = new ExecutionEventWriter(tempDir, () => tradingDate, log);

            var scenario = ExecutionScenarioDefinitions.GetAll()[0];
            var result = ExecutionScenarioRunner.RunScenario(scenario, writer, tempDir, tradingDate);

            if (!result.Pass)
                return (false, $"Scenario 1 should pass: {result.Error}");
            if (result.FinalLifecycle != "TERMINAL")
                return (false, $"Expected TERMINAL lifecycle, got {result.FinalLifecycle}");
            if (Math.Abs(result.ReplayExposure) > 0.001m)
                return (false, $"Expected flat exposure, got {result.ReplayExposure}");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Unit test: failure conditions detected correctly (invalid expected state fails).
    /// </summary>
    public static (bool Pass, string? Error) TestFailureConditionsDetected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "QTSW2_ScenarioFail_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(tempDir);
            var tradingDate = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            var eventDir = Path.Combine(tempDir, "automation", "logs", "execution_events", tradingDate);
            Directory.CreateDirectory(eventDir);

            var log = new RobotLogger(tempDir, Path.Combine(tempDir, "logs"), "TEST");
            var writer = new ExecutionEventWriter(tempDir, () => tradingDate, log);

            var scenario = ExecutionScenarioDefinitions.GetAll()[0];
            var originalExpected = scenario.ExpectedState;
            scenario.ExpectedState = new ExpectedFinalState
            {
                LifecycleState = "WORKING",
                BrokerExposure = 0,
                ProtectiveBlock = false,
                MismatchState = "NONE",
                Terminal = false,
                OpenQuantity = 0,
                InstrumentBlocked = false,
                PositionFlattened = false,
                IntentId = scenario.ExpectedState.IntentId
            };

            var result = ExecutionScenarioRunner.RunScenario(scenario, writer, tempDir, tradingDate);
            scenario.ExpectedState = originalExpected;

            if (result.Pass)
                return (false, "Expected failure when expected state is wrong");
            if (string.IsNullOrEmpty(result.Error))
                return (false, "Failure should have error message");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }
}
