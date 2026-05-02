using System;
using System.IO;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

/// <summary>
/// Regression: execution context must be observable on IExecutionAdapter so streams/engine can gate
/// order submission and pre-hydration before DataLoaded wiring (SIM verify + SetNTContext).
/// </summary>
public static class ExecutionContextReadyContractTests
{
    public static bool RunAll(Action<string>? log = null)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ect_ready_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);
        try
        {
            var logger = new RobotLogger(tmp);
            var dry = new NullExecutionAdapter(logger);
            if (!dry.IsExecutionContextReady)
            {
                log?.Invoke("NullExecutionAdapter.IsExecutionContextReady must be true for DRYRUN");
                return false;
            }

            var journal = new ExecutionJournal(tmp, logger);
            var sim = new NinjaTraderSimAdapter(tmp, tmp, logger, journal);
            if (sim.IsExecutionContextReady)
            {
                log?.Invoke("NinjaTraderSimAdapter must be not-ready before SetNTContext");
                return false;
            }

            var utcNow = DateTimeOffset.Parse("2026-04-30T18:00:00+00:00");
            if (!ExpectThrows(() => sim.GetCurrentPosition("MNG"), "GetCurrentPosition", log) ||
                !ExpectThrows(() => sim.GetAccountSnapshot(utcNow), "GetAccountSnapshot", log) ||
                !ExpectThrows(() => sim.CancelRobotOwnedWorkingOrders(new AccountSnapshot(), utcNow), "CancelRobotOwnedWorkingOrders", log))
            {
                return false;
            }

            RobotLoggingService.GetInstance(tmp, RobotRunArtifactPaths.LogsRobot(tmp))?.FlushNowForSummary();
            var logText = ReadRobotLogs(tmp);
            if (!ContainsToken(logText, "EXECUTION_CONTEXT_NOT_READY") ||
                !ContainsToken(logText, "GetCurrentPosition") ||
                !ContainsToken(logText, "GetAccountSnapshot") ||
                !ContainsToken(logText, "CancelRobotOwnedWorkingOrders"))
            {
                log?.Invoke("pre-wire broker calls must log EXECUTION_CONTEXT_NOT_READY with operation names");
                return false;
            }

            if (ContainsToken(logText, "EXECUTION_BLOCKED"))
            {
                log?.Invoke("pre-wire broker calls must not log EXECUTION_BLOCKED");
                return false;
            }

            log?.Invoke("PASS: ExecutionContextReady contract (Null + Sim pre-wire)");
            return true;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static bool ExpectThrows(Action action, string operation, Action<string>? log)
    {
        try
        {
            action();
            log?.Invoke(operation + " should throw before SetNTContext");
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static string ReadRobotLogs(string root)
    {
        var robotLogDir = RobotRunArtifactPaths.LogsRobot(root);
        if (!Directory.Exists(robotLogDir))
            return "";

        var text = "";
        foreach (var file in Directory.GetFiles(robotLogDir, "*.jsonl", SearchOption.AllDirectories))
            text += File.ReadAllText(file);
        return text;
    }

    private static bool ContainsToken(string text, string token)
    {
        return text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
