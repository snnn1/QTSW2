namespace QTSW2.Robot.Core.Execution;

/// <summary>Runtime toggles for staged robot behaviors (defaults are production-safe).</summary>
public static class FeatureFlags
{
    /// <summary>
    /// When true, runs an extra <see cref="ExecutionJournal.CloseRecoveredRowsSupersededByRealExposure"/> pass
    /// when real open quantity already matches broker but recovered rows still carry open qty (overlap / illegal authority).
    /// </summary>
    public static bool EnablePositionAuthorityEnforcement { get; set; }

    /// <summary>
    /// When true: journal integrity does not align / upsert recovered intents / reconstruct when parity or authority is unsafe;
    /// engine may trigger broker <see cref="HardFailClosedExecutionModel"/> flatten once per instrument.
    /// </summary>
    public static bool EnableHardFailClosedJournalIntegrity { get; set; } = true;

    /// <summary>When true, engine requests broker hard flatten when parity pre-check fails (after execution activity).</summary>
    public static bool EnableHardFailClosedBrokerFlatten { get; set; } = true;
}
