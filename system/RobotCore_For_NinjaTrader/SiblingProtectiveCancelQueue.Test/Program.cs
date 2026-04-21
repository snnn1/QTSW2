// Regression for urgent sibling-protective cancel lane. Build/run:
//   dotnet run --project RobotCore_For_NinjaTrader/SiblingProtectiveCancelQueue.Test/SiblingProtectiveCancelQueue.Test.csproj
//   dotnet run --project ... -- QTSW2_HYDRATION_DEFER
//   dotnet run --project ... -- EXECUTION_CONTEXT_CONTRACT
//   dotnet run --project ... -- STARTUP_EXEC_CONTEXT_RACE
//   dotnet run --project ... -- EPA_ADAPTER_PREFLIGHT
//   dotnet run --project ... -- MARKET_REENTRY_SUBMIT_PATH
//   dotnet run --project ... -- SCENARIO_HARNESS_SIX
//   dotnet run --project ... -- AUTHORITY_CONTRADICTIONS
// (mirrors modules/robot/core/Tests/SiblingProtectiveCancelQueueTests.cs; runnable when harness core snapshot is broken.)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;
using QTSW2.Robot.Core.Tests;

namespace QTSW2.Robot.SiblingProtectiveCancelQueue.Test;

internal static class Program
{
    private sealed class RecordingExecutor : INtActionExecutor
    {
        private readonly List<string> _trace;
        public RecordingExecutor(List<string> trace) => _trace = trace;

        public void ExecuteCancelOrders(NtCancelOrdersCommand cmd) =>
            _trace.Add($"CANCEL:{cmd.CorrelationId}:{cmd.Reason}");

        public void ExecuteSubmitProtectives(NtSubmitProtectivesCommand cmd) => _trace.Add("SUBMIT_PROTECTIVES");
        public void ExecuteFlattenInstrument(NtFlattenInstrumentCommand cmd) => _trace.Add("FLATTEN");
        public void ExecuteSubmitEntryIntent(NtSubmitEntryIntentCommand cmd) => _trace.Add("ENTRY");
        public void ExecuteSubmitMarketReentry(NtSubmitMarketReentryCommand cmd) => _trace.Add("REENTRY");
    }

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("IDENTITY_REPLAY_SCENARIOS", StringComparison.OrdinalIgnoreCase))
        {
            var (pass, err) = IdentityReplayScenarioTests.RunAll(Console.WriteLine);
            if (pass)
            {
                Console.WriteLine("PASS: identity replay scenarios (all)");
                return 0;
            }

            Console.Error.WriteLine("FAIL: " + err);
            return 1;
        }

        if (args.Length > 0 && args[0].Equals("QTSW2_HYDRATION_DEFER", StringComparison.OrdinalIgnoreCase))
        {
            var (pass, err) = Qtsw2HydrationDeferPolicyTests.RunQtsw2HydrationDeferPolicyTests();
            if (pass)
            {
                Console.WriteLine("PASS: QTSW2 hydration + reconciliation defer policy");
                return 0;
            }

            Console.Error.WriteLine("FAIL: " + err);
            return 1;
        }

        if (args.Length > 0 && args[0].Equals("EXECUTION_CONTEXT_CONTRACT", StringComparison.OrdinalIgnoreCase))
        {
            if (ExecutionContextReadyContractTests.RunAll(Console.WriteLine))
                return 0;
            return 1;
        }

        if (args.Length > 0 && args[0].Equals("STARTUP_EXEC_CONTEXT_RACE", StringComparison.OrdinalIgnoreCase))
        {
            if (StartupExecutionContextRaceScenarioTests.RunAll(Console.WriteLine))
                return 0;
            return 1;
        }

        if (args.Length > 0 && args[0].Equals("EPA_ADAPTER_PREFLIGHT", StringComparison.OrdinalIgnoreCase))
        {
            var (pass, err) = EpaAdapterPreflightTests.RunAll();
            if (pass)
            {
                Console.WriteLine("PASS: EPA adapter preflight (Phase 8)");
                return 0;
            }

            Console.Error.WriteLine("FAIL: " + err);
            return 1;
        }

        if (args.Length > 0 && args[0].Equals("MARKET_REENTRY_SUBMIT_PATH", StringComparison.OrdinalIgnoreCase))
        {
            var (pass, err) = MarketReentrySubmitPathTests.RunAll();
            if (pass)
            {
                Console.WriteLine("PASS: MARKET_REENTRY_SUBMIT_PATH");
                return 0;
            }

            Console.Error.WriteLine("FAIL: " + err);
            return 1;
        }

        if (args.Length > 0 && args[0].Equals("SCENARIO_HARNESS_SIX", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("=== Scenario Harness (Six) ===");
            var (allPassed, metrics, _) = ExecutionScenarioRunner.RunFirstSix(Console.WriteLine);
            Console.WriteLine($"scenario_pass_count: {metrics.ScenarioPassCount}  scenario_fail_count: {metrics.ScenarioFailCount}");
            if (!allPassed)
            {
                Console.Error.WriteLine("FAIL: SCENARIO_HARNESS_SIX");
                return 1;
            }

            Console.WriteLine("PASS: SCENARIO_HARNESS_SIX");
            return 0;
        }

        if (args.Length > 0 && args[0].Equals("AUTHORITY_CONTRADICTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var (pass, err) = AuthorityContradictionTests.RunAll();
            if (pass)
            {
                Console.WriteLine("PASS: AUTHORITY_CONTRADICTIONS");
                return 0;
            }

            Console.Error.WriteLine("FAIL: " + err);
            return 1;
        }

        var root = Path.Combine(Path.GetTempPath(), "SiblingProtectiveCancelQueue_NT_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var utc = DateTimeOffset.Parse("2026-04-01T12:00:00Z", CultureInfo.InvariantCulture);

            var trace = new List<string>();

            var exec = new StrategyThreadExecutor(log, () => utc);
            if (!exec.EnqueueNtAction(new NtDeferredAction("norm-1", null, null, "test", () => trace.Add("DEFER:norm-1")), out var dupNorm1) || dupNorm1)
            {
                Console.Error.WriteLine("FAIL: expected norm-1 enqueue");
                return 1;
            }

            var sibling = new NtCancelOrdersCommand(
                "sib-1",
                "intent-partial-stop-2lot",
                null,
                true,
                NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill,
                utc,
                preferUrgentDrain: true);
            if (!exec.EnqueueNtAction(sibling, out var dupSib) || dupSib)
            {
                Console.Error.WriteLine("FAIL: expected sib-1 enqueue");
                return 1;
            }

            if (!exec.EnqueueNtAction(new NtDeferredAction("norm-2", null, null, "test", () => trace.Add("DEFER:norm-2")), out var dupNorm2) || dupNorm2)
            {
                Console.Error.WriteLine("FAIL: expected norm-2 enqueue");
                return 1;
            }

            exec.DrainNtActions(new RecordingExecutor(trace));

            if (trace.Count != 3)
            {
                Console.Error.WriteLine("FAIL: expected 3 trace entries, got " + trace.Count + ": " + string.Join(" | ", trace));
                return 1;
            }

            if (trace[0] != "CANCEL:sib-1:" + NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill)
            {
                Console.Error.WriteLine("FAIL: first must be urgent sibling cancel, got [" + trace[0] + "]");
                return 1;
            }

            if (trace[1] != "DEFER:norm-1" || trace[2] != "DEFER:norm-2")
            {
                Console.Error.WriteLine("FAIL: after sibling cancel, normal FIFO must hold: " + string.Join(" | ", trace));
                return 1;
            }

            var trace2 = new List<string>();
            var exec2 = new StrategyThreadExecutor(log, () => utc);
            if (!exec2.EnqueueNtAction(new NtCancelOrdersCommand("u1", "i", null, true, NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill, utc, preferUrgentDrain: true), out _))
            {
                Console.Error.WriteLine("FAIL: u1 enqueue");
                return 1;
            }
            if (!exec2.EnqueueNtAction(new NtCancelOrdersCommand("u2", "i", null, true, NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill, utc, preferUrgentDrain: true), out _))
            {
                Console.Error.WriteLine("FAIL: u2 enqueue");
                return 1;
            }
            exec2.DrainNtActions(new RecordingExecutor(trace2));
            if (trace2.Count != 2 ||
                trace2[0] != "CANCEL:u1:" + NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill ||
                trace2[1] != "CANCEL:u2:" + NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill)
            {
                Console.Error.WriteLine("FAIL: urgent FIFO: " + string.Join(" | ", trace2));
                return 1;
            }

            var trace3 = new List<string>();
            var exec3 = new StrategyThreadExecutor(log, () => utc);
            if (!exec3.EnqueueNtAction(new NtDeferredAction("n1", null, null, "t", () => trace3.Add("D1")), out _))
            {
                Console.Error.WriteLine("FAIL: n1");
                return 1;
            }
            if (!exec3.EnqueueNtAction(new NtDeferredAction("n2", null, null, "t", () => trace3.Add("D2")), out _))
            {
                Console.Error.WriteLine("FAIL: n2");
                return 1;
            }
            exec3.DrainNtActions(new RecordingExecutor(trace3));
            if (trace3.Count != 2 || trace3[0] != "D1" || trace3[1] != "D2")
            {
                Console.Error.WriteLine("FAIL: normal-only FIFO: " + string.Join(" | ", trace3));
                return 1;
            }

            if (exec.PendingCount != 0 || exec2.PendingCount != 0 || exec3.PendingCount != 0)
            {
                Console.Error.WriteLine("FAIL: queues must be empty after drain");
                return 1;
            }

            Console.WriteLine("PASS: Sibling protective cancel queue (urgent lane)");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* temp */ }
        }
    }
}
