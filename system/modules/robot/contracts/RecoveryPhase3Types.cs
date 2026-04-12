// Phase 3: Deterministic Recovery - shared types and reconstruction classifier.
// Used by Robot.Core (harness tests) and RobotCore_For_NinjaTrader (IEA).

using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Reconstruction classification from four-view comparison.</summary>
public enum ReconstructionClassification
{
    CLEAN,
    POSITION_ONLY_MISMATCH,
    LIVE_ORDER_OWNERSHIP_MISMATCH,
    JOURNAL_BROKER_MISMATCH,
    TERMINALITY_MISMATCH,
    MANUAL_INTERVENTION_DETECTED,
    ADOPTION_POSSIBLE,
    UNSAFE_AMBIGUOUS_STATE
}

/// <summary>Reconstruction result from four-view comparison.</summary>
public sealed class ReconstructionResult
{
    public string? Instrument { get; set; }
    public ReconstructionClassification Classification { get; set; }
    public int BrokerPositionQty { get; set; }
    public int JournalQty { get; set; }
    public int UnownedLiveOrderCount { get; set; }
    public int BrokerWorkingOrderCount { get; set; }
    public object? BrokerSnapshot { get; set; }
    public object? JournalSnapshot { get; set; }
    public object? RegistrySnapshot { get; set; }
    public object? RuntimeIntentSnapshot { get; set; }
}

/// <summary>Registry snapshot for recovery reconstruction.</summary>
public sealed class RecoveryRegistrySnapshot
{
    public List<string> WorkingOrderIds { get; set; } = new();
    public int UnownedLiveCount { get; set; }
    public int OwnedCount { get; set; }
    public int AdoptedCount { get; set; }
}

/// <summary>Runtime intent snapshot for recovery reconstruction.</summary>
public sealed class RecoveryRuntimeIntentSnapshot
{
    public List<string> ActiveIntentIds { get; set; } = new();
}

/// <summary>Runs deterministic reconstruction from four views and produces classification.</summary>
public static class RecoveryReconstructor
{
    public static ReconstructionResult Reconstruct(
        string instrument,
        int brokerPositionQty,
        int brokerWorkingOrderCount,
        int journalQty,
        RecoveryRegistrySnapshot registry,
        RecoveryRuntimeIntentSnapshot runtimeIntent,
        string? triggerReason = null)
    {
        var result = new ReconstructionResult
        {
            Instrument = instrument,
            BrokerPositionQty = brokerPositionQty,
            JournalQty = journalQty,
            BrokerWorkingOrderCount = brokerWorkingOrderCount,
            UnownedLiveOrderCount = registry.UnownedLiveCount,
            RegistrySnapshot = registry,
            RuntimeIntentSnapshot = runtimeIntent
        };

        var classification = Classify(brokerPositionQty, journalQty, registry, runtimeIntent, brokerWorkingCount: brokerWorkingOrderCount, triggerReason);
        result.Classification = classification;
        return result;
    }

    internal static ReconstructionClassification Classify(
        int brokerQty,
        int journalQty,
        RecoveryRegistrySnapshot registry,
        RecoveryRuntimeIntentSnapshot runtimeIntent,
        int brokerWorkingCount,
        string? triggerReason)
    {
        var posMismatch = brokerQty != journalQty;
        var hasUnownedLive = registry.UnownedLiveCount > 0;
        var hasBrokerWorking = brokerWorkingCount > 0;
        var hasRegistryWorking = registry.WorkingOrderIds.Count > 0;
        var registryBrokerMismatch = hasBrokerWorking && registry.WorkingOrderIds.Count != brokerWorkingCount;

        if (triggerReason != null)
        {
            if (triggerReason.IndexOf("MANUAL", StringComparison.OrdinalIgnoreCase) >= 0 || triggerReason.IndexOf("EXTERNAL", StringComparison.OrdinalIgnoreCase) >= 0 || triggerReason.IndexOf("UNOWNED", StringComparison.OrdinalIgnoreCase) >= 0)
                return ReconstructionClassification.MANUAL_INTERVENTION_DETECTED;
            if (triggerReason.IndexOf("RECONCILIATION", StringComparison.OrdinalIgnoreCase) >= 0 || triggerReason.IndexOf("QTY_MISMATCH", StringComparison.OrdinalIgnoreCase) >= 0)
                return ReconstructionClassification.JOURNAL_BROKER_MISMATCH;
            if (triggerReason.IndexOf("TERMINAL", StringComparison.OrdinalIgnoreCase) >= 0 || triggerReason.IndexOf("COMPLETED_INTENT", StringComparison.OrdinalIgnoreCase) >= 0)
                return ReconstructionClassification.TERMINALITY_MISMATCH;
            if (triggerReason.IndexOf("REGISTRY", StringComparison.OrdinalIgnoreCase) >= 0 || triggerReason.IndexOf("DIVERGENCE", StringComparison.OrdinalIgnoreCase) >= 0)
                return ReconstructionClassification.LIVE_ORDER_OWNERSHIP_MISMATCH;
        }

        if (!posMismatch && !hasUnownedLive && !registryBrokerMismatch && !hasBrokerWorking)
            return ReconstructionClassification.CLEAN;

        if (posMismatch && !hasUnownedLive && !registryBrokerMismatch)
            return ReconstructionClassification.POSITION_ONLY_MISMATCH;

        if (hasUnownedLive || registryBrokerMismatch)
            return ReconstructionClassification.LIVE_ORDER_OWNERSHIP_MISMATCH;

        if (posMismatch && (brokerQty != 0 || journalQty != 0))
            return ReconstructionClassification.JOURNAL_BROKER_MISMATCH;

        if (registry.AdoptedCount > 0 && registry.UnownedLiveCount == 0 && !posMismatch)
            return ReconstructionClassification.ADOPTION_POSSIBLE;

        return ReconstructionClassification.UNSAFE_AMBIGUOUS_STATE;
    }
}

/// <summary>Deterministic recovery action from reconstruction (shared by IEA and P2 guard).</summary>
public enum RecoveryActionKind
{
    Resume,
    Adopt,
    Flatten,
    Halt
}

/// <summary>Maps reconstruction classification to recovery action (single source of truth).</summary>
public static class RecoveryPhase3DecisionRules
{
    public static RecoveryActionKind GetActionKind(ReconstructionClassification classification, ReconstructionResult result)
    {
        return classification switch
        {
            ReconstructionClassification.CLEAN => RecoveryActionKind.Resume,
            ReconstructionClassification.ADOPTION_POSSIBLE => result.BrokerPositionQty != 0 ? RecoveryActionKind.Adopt : RecoveryActionKind.Resume,
            ReconstructionClassification.POSITION_ONLY_MISMATCH => result.UnownedLiveOrderCount > 0 ? RecoveryActionKind.Flatten : RecoveryActionKind.Resume,
            ReconstructionClassification.LIVE_ORDER_OWNERSHIP_MISMATCH => RecoveryActionKind.Flatten,
            ReconstructionClassification.JOURNAL_BROKER_MISMATCH => RecoveryActionKind.Flatten,
            ReconstructionClassification.TERMINALITY_MISMATCH => RecoveryActionKind.Flatten,
            ReconstructionClassification.MANUAL_INTERVENTION_DETECTED => RecoveryActionKind.Flatten,
            ReconstructionClassification.UNSAFE_AMBIGUOUS_STATE => RecoveryActionKind.Halt,
            _ => RecoveryActionKind.Halt
        };
    }
}
