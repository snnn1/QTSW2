// P2 Phase 1: stream-scoped recovery vs instrument-scoped destructive action.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test RECOVERY_P2_PHASE1

using System;
using System.Collections.Generic;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class P2RecoveryPhase1Tests
{
    public static (bool Pass, string? Error) RunP2RecoveryPhase1Tests()
    {
        // 1) Single-stream snapshot → stream-scoped recommendation; escalation blocked for flatten
        var snap = new RecoveryAttributionSnapshot
        {
            ExecutionInstrumentKey = "MES",
            TriggerReason = "UNOWNED_LIVE_ORDER_DETECTED",
            TriggerIntentId = "i1",
            BrokerPositionQty = 1,
            BrokerWorkingCount = 1,
            JournalOpenQtySum = 1,
            DegradedOwnershipOrAmbiguity = true,
            GateEngagedForSymbol = false,
            Intents =
            {
                new RecoveryAttributionIntentRow { IntentId = "i1", Stream = "ES1", HasWorkingOrder = true },
                new RecoveryAttributionIntentRow { IntentId = "i2", Stream = "ES2", HasWorkingOrder = true }
            },
            RegistryWorking =
            {
                new RecoveryAttributionRegistryRow { BrokerOrderId = "b1", IntentId = "i1", Stream = "ES1", OwnershipStatus = "UNOWNED", IsEntry = true }
            },
            BrokerWorking =
            {
                new RecoveryAttributionBrokerOrderRow { BrokerOrderId = "b1", TagIntentId = "i1", RegistryIntentId = "i1" }
            }
        };
        var a1 = RecoveryOwnershipAttributionEvaluator.EvaluateOwnershipAttributionForRecovery(snap);
        if (!a1.IsSingleStreamAttributable || a1.AttributableScope != AttributableScopeKind.SingleStream)
            return (false, $"Case1: expected SingleStream attributable, got {a1.AttributableScope}");
        if (RecoveryOwnershipAttributionEvaluator.CanEscalateToInstrumentScopedRecovery(a1, snap, snap.TriggerReason, false, false))
            return (false, "Case1: instrument escalation should be blocked for single-stream ambiguity");

        // 2) Gate engaged + single-stream → still no instrument escalation (P2.1-G)
        if (RecoveryOwnershipAttributionEvaluator.CanEscalateToInstrumentScopedRecovery(a1, snap, snap.TriggerReason, false, gateEngagedForSymbol: true))
            return (false, "Case2: gate engaged must not auto-escalate to instrument flatten");

        // 3) Unattributed broker order → instrument escalation allowed
        var snapUn = new RecoveryAttributionSnapshot
        {
            ExecutionInstrumentKey = "MES",
            TriggerReason = "UNOWNED_LIVE_ORDER_DETECTED",
            BrokerPositionQty = 0,
            BrokerWorkingCount = 1,
            JournalOpenQtySum = 0,
            DegradedOwnershipOrAmbiguity = true,
            Intents = { new RecoveryAttributionIntentRow { IntentId = "i1", Stream = "ES1" } },
            BrokerWorking =
            {
                new RecoveryAttributionBrokerOrderRow { BrokerOrderId = "x99", TagIntentId = null, RegistryIntentId = null }
            }
        };
        var aUn = RecoveryOwnershipAttributionEvaluator.EvaluateOwnershipAttributionForRecovery(snapUn);
        if (!aUn.IsUnattributable)
            return (false, "Case3: expected unattributable broker order");
        if (!RecoveryOwnershipAttributionEvaluator.CanEscalateToInstrumentScopedRecovery(aUn, snapUn, snapUn.TriggerReason, false, false))
            return (false, "Case3: instrument escalation should be allowed when unattributable");

        // 4) Emergency trigger bypasses attribution block
        if (!RecoveryOwnershipAttributionEvaluator.IsEmergencyInstrumentRecoveryTrigger("ORPHAN_FILL_INTENT_NOT_FOUND"))
            return (false, "Case4: ORPHAN_FILL should be emergency");

        // 5) Reconciliation: one stream with journal exposure → single-stream; freeze-all false for journal_ahead only
        var rec = RecoveryOwnershipAttributionEvaluator.EvaluateReconciliationQuantityMismatch(
            "MES", accountQty: 1, journalQty: 2,
            new List<(string Stream, int OpenQty)> { ("ES1", 2) });
        if (!rec.IsSingleStreamAttributable)
            return (false, "Case5: expected single-stream reconciliation attribution");
        if (RecoveryOwnershipAttributionEvaluator.CanEscalateReconciliationToInstrumentFreeze(rec, 1, 2))
            return (false, "Case5: journal_ahead single-stream should not force instrument freeze");

        // 6) Reconciliation: two streams → multi-stream attribution → instrument freeze path
        var rec2 = RecoveryOwnershipAttributionEvaluator.EvaluateReconciliationQuantityMismatch(
            "MES", 2, 2,
            new List<(string Stream, int OpenQty)> { ("ES1", 1), ("ES2", 1) });
        if (rec2.AttributableScope != AttributableScopeKind.MultiStreamShared)
            return (false, $"Case6: expected MultiStreamShared, got {rec2.AttributableScope}");
        if (!RecoveryOwnershipAttributionEvaluator.CanEscalateReconciliationToInstrumentFreeze(rec2, 2, 2))
            return (false, "Case6: two-stream journal exposure should allow instrument-wide freeze escalation");

        // 7) RecoveryPhase3DecisionRules matches prior flatten-on-manual trigger
        var reg = new RecoveryRegistrySnapshot { UnownedLiveCount = 0 };
        var rt = new RecoveryRuntimeIntentSnapshot { ActiveIntentIds = new List<string>() };
        var recon = RecoveryReconstructor.Reconstruct("MES", 1, 0, 0, reg, rt, "MANUAL_OR_EXTERNAL_ORDER");
        if (RecoveryPhase3DecisionRules.GetActionKind(recon.Classification, recon) != RecoveryActionKind.Flatten)
            return (false, "Case7: MANUAL trigger should still map to Flatten action kind");

        return (true, null);
    }
}
