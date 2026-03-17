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
    /// <param name="time">TimeService for LIVE adapter (breakout validity gate). Optional; defaults to America/Chicago.</param>
    public static IExecutionAdapter Create(ExecutionMode mode, string projectRoot, RobotLogger log, ExecutionJournal? executionJournal = null, TimeService? time = null)
    {
        IExecutionAdapter adapter;
        var timeService = time ?? new TimeService("America/Chicago");

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
                adapter = new NinjaTraderLiveAdapter(log, timeService);
                log.Write(RobotEvents.EngineBase(
                    DateTimeOffset.UtcNow,
                    tradingDate: "",
                    eventType: "ADAPTER_SELECTED",
                    state: "ENGINE",
                    new { mode = "LIVE", adapter = "NinjaTraderLiveAdapter" }));
                break;

            default:
                throw new ArgumentException($"Unknown execution mode: {mode}", nameof(mode));
        }

        return adapter;
    }
}
