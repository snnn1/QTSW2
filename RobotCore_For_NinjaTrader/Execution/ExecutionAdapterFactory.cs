using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Factory for creating execution adapters based on execution mode.
/// Single adapter instance per run (no per-bar creation).
/// </summary>
public static class ExecutionAdapterFactory
{
    /// <summary>
    /// Create execution adapter based on mode.
    /// </summary>
    public static IExecutionAdapter Create(ExecutionMode mode, string projectRoot, RobotLogger log, ExecutionJournal? executionJournal = null)
    {
        IExecutionAdapter adapter;
        
        switch (mode)
        {
            case ExecutionMode.DRYRUN:
                adapter = new NullExecutionAdapter(log);
                log.Write(RobotEvents.EngineBase(
                    DateTimeOffset.UtcNow,
                    tradingDate: "",
                    eventType: "ADAPTER_SELECTED",
                    state: "ENGINE",
                    new { mode = "DRYRUN", adapter = "NullExecutionAdapter" }));
                break;

            case ExecutionMode.SIM:
                adapter = new NinjaTraderSimAdapter(projectRoot, log, executionJournal ?? new ExecutionJournal(projectRoot, log));
                log.Write(RobotEvents.EngineBase(
                    DateTimeOffset.UtcNow,
                    tradingDate: "",
                    eventType: "ADAPTER_SELECTED",
                    state: "ENGINE",
                    new { mode = "SIM", adapter = "NinjaTraderSimAdapter" }));
                break;

            case ExecutionMode.LIVE:
                // LIVE mode requires explicit enablement (Phase C)
                throw new InvalidOperationException("LIVE mode is not yet enabled. Use DRYRUN or SIM.");

            default:
                throw new ArgumentException($"Unknown execution mode: {mode}", nameof(mode));
        }

        return adapter;
    }
}
