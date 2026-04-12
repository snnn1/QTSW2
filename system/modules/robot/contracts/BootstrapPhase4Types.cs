// Phase 4: Crash/Disconnect Determinism - bootstrap types and classification.
// Used by Robot.Core (harness tests) and RobotCore_For_NinjaTrader (IEA).

using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Bootstrap/reconnect reason.</summary>
public enum BootstrapReason
{
    STRATEGY_START,
    PLATFORM_RESTART,
    CONNECTION_RECOVERED,
    RESTART_WITH_LIVE_POSITION,
    RESTART_WITH_LIVE_ORDERS,
    RESTART_WITH_JOURNAL_OPEN_INTENT,
    RESTART_WITH_ADOPTION_REQUIRED,
    RESTART_WITH_AMBIGUOUS_STATE
}

/// <summary>Bootstrap classification from startup snapshot.</summary>
public enum BootstrapClassification
{
    CLEAN_START,
    RESUME_WITH_NO_POSITION_NO_ORDERS,
    ADOPTION_REQUIRED,
    POSITION_PRESENT_NO_OWNED_ORDERS,
    LIVE_ORDERS_PRESENT_NO_POSITION,
    JOURNAL_RUNTIME_DIVERGENCE,
    REGISTRY_BROKER_DIVERGENCE_ON_START,
    MANUAL_INTERVENTION_PRESENT,
    UNSAFE_STARTUP_AMBIGUITY,
    /// <summary>Explicit bucket: cannot classify; requires supervisor before trading.</summary>
    UNKNOWN_REQUIRES_SUPERVISOR
}

/// <summary>Deterministic bootstrap decision.</summary>
public enum BootstrapDecision
{
    RESUME,
    ADOPT,
    RECONCILE_THEN_RESUME,
    FLATTEN_THEN_RECONSTRUCT,
    HALT
}

/// <summary>Protective order status from broker truth.</summary>
public enum ProtectiveStatus
{
    UNKNOWN,
    NONE,
    STOP_ONLY,
    TARGET_ONLY,
    BOTH_PRESENT,
    QTY_MISMATCH
}

/// <summary>Structured startup snapshot from four views.</summary>
public sealed class BootstrapSnapshot
{
    public string? Instrument { get; set; }
    public int BrokerPositionQty { get; set; }
    public int BrokerWorkingOrderCount { get; set; }
    public int JournalQty { get; set; }
    public int UnownedLiveOrderCount { get; set; }
    public long SnapshotEpoch { get; set; }
    public DateTimeOffset CaptureUtc { get; set; }
    public object? BrokerSnapshot { get; set; }
    public object? RegistrySnapshot { get; set; }
    public object? JournalSnapshot { get; set; }
    public object? RuntimeIntentSnapshot { get; set; }
    /// <summary>Broker-truth protective status when position exists.</summary>
    public ProtectiveStatus ProtectiveStatus { get; set; } = ProtectiveStatus.UNKNOWN;

    /// <summary>
    /// True when slot journal shows RANGE_LOCKED + StopBracketsSubmittedAtLock for a stream trading this instrument.
    /// Used to ADOPT working entry stops on restart instead of flattening them as orphans.
    /// </summary>
    public bool SlotJournalShowsEntryStopsExpected { get; set; }
}

/// <summary>Classifies startup snapshot into controlled set.</summary>
public static class BootstrapClassifier
{
    public static BootstrapClassification Classify(BootstrapSnapshot snapshot)
    {
        var brokerQty = snapshot.BrokerPositionQty;
        var brokerWorking = snapshot.BrokerWorkingOrderCount;
        var journalQty = snapshot.JournalQty;
        var unownedLive = snapshot.UnownedLiveOrderCount;

        if (unownedLive > 0)
            return BootstrapClassification.MANUAL_INTERVENTION_PRESENT;

        if (brokerQty == 0 && brokerWorking == 0 && journalQty == 0)
            return BootstrapClassification.CLEAN_START;

        if (brokerQty == 0 && brokerWorking == 0)
            return BootstrapClassification.RESUME_WITH_NO_POSITION_NO_ORDERS;

        if (brokerWorking > 0 && journalQty > 0 && brokerQty != 0)
        {
            var reg = snapshot.RegistrySnapshot as RecoveryRegistrySnapshot;
            if (reg != null && reg.UnownedLiveCount == 0 && (reg.AdoptedCount > 0 || reg.OwnedCount > 0))
                return BootstrapClassification.RESUME_WITH_NO_POSITION_NO_ORDERS;
            if (reg != null && reg.UnownedLiveCount == 0 && reg.AdoptedCount == 0 && reg.OwnedCount == 0)
                return BootstrapClassification.ADOPTION_REQUIRED;
        }

        if (brokerQty != journalQty)
            return BootstrapClassification.JOURNAL_RUNTIME_DIVERGENCE;

        if (brokerQty != 0 && brokerWorking == 0)
            return BootstrapClassification.POSITION_PRESENT_NO_OWNED_ORDERS;

        if (brokerWorking > 0 && brokerQty == 0)
            return BootstrapClassification.LIVE_ORDERS_PRESENT_NO_POSITION;

        return BootstrapClassification.UNKNOWN_REQUIRES_SUPERVISOR;
    }
}

/// <summary>Per-instrument startup reconciliation report. Emitted after classification.</summary>
public sealed class StartupReconciliationReport
{
    public string Instrument { get; set; } = "";
    public int BrokerQty { get; set; }
    public int JournalQty { get; set; }
    public int WorkingOrderCount { get; set; }
    public ProtectiveStatus ProtectiveStatus { get; set; }
    public string ReconstructedLifecycleState { get; set; } = "";
    public string ReconciliationStatus { get; set; } = "";
    public string ActionTaken { get; set; } = "";
    public string Classification { get; set; } = "";
    public string Decision { get; set; } = "";
    public DateTimeOffset ReportUtc { get; set; }
}

/// <summary>Chooses deterministic bootstrap decision from classification.</summary>
public static class BootstrapDecider
{
    public static BootstrapDecision Decide(BootstrapClassification classification, BootstrapSnapshot snapshot)
    {
        // RESTART FIX: Working entry stops with no position are valid when slot journal shows RANGE_LOCKED + StopBracketsSubmittedAtLock.
        // ADOPT instead of flatten to preserve entry stops after NinjaTrader restart.
        if (classification == BootstrapClassification.LIVE_ORDERS_PRESENT_NO_POSITION && snapshot.SlotJournalShowsEntryStopsExpected)
            return BootstrapDecision.ADOPT;

        return classification switch
        {
            BootstrapClassification.CLEAN_START => BootstrapDecision.RESUME,
            BootstrapClassification.RESUME_WITH_NO_POSITION_NO_ORDERS => BootstrapDecision.RESUME,
            BootstrapClassification.ADOPTION_REQUIRED => BootstrapDecision.ADOPT,
            BootstrapClassification.POSITION_PRESENT_NO_OWNED_ORDERS => BootstrapDecision.FLATTEN_THEN_RECONSTRUCT,
            BootstrapClassification.LIVE_ORDERS_PRESENT_NO_POSITION => BootstrapDecision.FLATTEN_THEN_RECONSTRUCT,
            BootstrapClassification.JOURNAL_RUNTIME_DIVERGENCE => BootstrapDecision.FLATTEN_THEN_RECONSTRUCT,
            BootstrapClassification.REGISTRY_BROKER_DIVERGENCE_ON_START => BootstrapDecision.FLATTEN_THEN_RECONSTRUCT,
            BootstrapClassification.MANUAL_INTERVENTION_PRESENT => BootstrapDecision.HALT,
            BootstrapClassification.UNSAFE_STARTUP_AMBIGUITY => BootstrapDecision.HALT,
            BootstrapClassification.UNKNOWN_REQUIRES_SUPERVISOR => BootstrapDecision.HALT,
            _ => BootstrapDecision.HALT
        };
    }
}
