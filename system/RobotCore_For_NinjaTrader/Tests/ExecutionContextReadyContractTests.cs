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

            log?.Invoke("PASS: ExecutionContextReady contract (Null + Sim pre-wire)");
            return true;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
    }
}
