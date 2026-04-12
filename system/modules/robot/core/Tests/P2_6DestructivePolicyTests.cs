// P2.6 — Destructive path closure policy tests.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test P2_6_DESTRUCTIVE_POLICY_TESTS

using System.Collections.Generic;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class P2_6DestructivePolicyTests
{
    public static (bool Pass, string? Error) RunP2_6DestructivePolicyTests()
    {
        // 1) Single-stream recovery flatten → no instrument scope
        var snap1 = new RecoveryAttributionSnapshot
        {
            ExecutionInstrumentKey = "MES",
            TriggerReason = "UNOWNED",
            BrokerPositionQty = 1,
            BrokerWorkingCount = 1,
            JournalOpenQtySum = 1,
            GateEngagedForSymbol = false,
            Intents = { new RecoveryAttributionIntentRow { IntentId = "i1", Stream = "S1", HasWorkingOrder = true } },
            RegistryWorking =
            {
                new RecoveryAttributionRegistryRow { BrokerOrderId = "b1", IntentId = "i1", Stream = "S1", OwnershipStatus = "UNOWNED", IsEntry = true }
            },
            BrokerWorking = { new RecoveryAttributionBrokerOrderRow { BrokerOrderId = "b1", TagIntentId = "i1" } }
        };
        var a1 = RecoveryOwnershipAttributionEvaluator.EvaluateOwnershipAttributionForRecovery(snap1);
        var d1 = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.RECOVERY,
            RecoveryReasonString = "UNOWNED",
            ReconstructionActionKind = RecoveryActionKind.Flatten,
            Attribution = a1,
            AttributionSnapshot = snap1,
            ExecutionInstrumentKey = "MES"
        });
        if (d1.AllowInstrumentScope)
            return (false, "Case1: single-stream should not allow instrument flatten");

        // 2) Multi-stream → instrument allowed
        var snap2 = new RecoveryAttributionSnapshot
        {
            ExecutionInstrumentKey = "MES",
            BrokerPositionQty = 1,
            JournalOpenQtySum = 1,
            Intents =
            {
                new RecoveryAttributionIntentRow { IntentId = "i1", Stream = "S1", HasWorkingOrder = true },
                new RecoveryAttributionIntentRow { IntentId = "i2", Stream = "S2", HasWorkingOrder = true }
            },
            RegistryWorking =
            {
                new RecoveryAttributionRegistryRow { BrokerOrderId = "b1", IntentId = "i1", Stream = "S1", OwnershipStatus = "UNOWNED", IsEntry = true },
                new RecoveryAttributionRegistryRow { BrokerOrderId = "b2", IntentId = "i2", Stream = "S2", OwnershipStatus = "UNOWNED", IsEntry = true }
            },
            BrokerWorking =
            {
                new RecoveryAttributionBrokerOrderRow { BrokerOrderId = "b1", TagIntentId = "i1" },
                new RecoveryAttributionBrokerOrderRow { BrokerOrderId = "b2", TagIntentId = "i2" }
            }
        };
        var a2 = RecoveryOwnershipAttributionEvaluator.EvaluateOwnershipAttributionForRecovery(snap2);
        var d2 = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.RECOVERY,
            ReconstructionActionKind = RecoveryActionKind.Flatten,
            Attribution = a2,
            AttributionSnapshot = snap2,
            ExecutionInstrumentKey = "MES"
        });
        if (!d2.AllowInstrumentScope)
            return (false, "Case2: multi-stream should allow instrument flatten");

        // 3) Emergency ORPHAN prefix → bypass
        var d3 = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.RECOVERY,
            RecoveryReasonString = "ORPHAN_FILL_X",
            ExplicitTrigger = null,
            ReconstructionActionKind = RecoveryActionKind.Flatten,
            ExecutionInstrumentKey = "MES"
        });
        if (!d3.AllowInstrumentScope || !d3.IsEmergency)
            return (false, "Case3: ORPHAN_FILL prefix should be emergency allow");

        // 4) FailClosed explicit trigger
        var d4 = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.FAIL_CLOSED,
            ExplicitTrigger = DestructiveTriggerReason.FAIL_CLOSED,
            RecoveryReasonString = "any",
            ExecutionInstrumentKey = "MES"
        });
        if (!d4.AllowInstrumentScope || !d4.IsEmergency)
            return (false, "Case4: FAIL_CLOSED explicit should allow");

        // 5) Bootstrap administrative flatten
        var d5 = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.BOOTSTRAP,
            BootstrapAdministrativeFlatten = true,
            ReconstructionActionKind = RecoveryActionKind.Flatten,
            ExecutionInstrumentKey = "MES"
        });
        if (!d5.AllowInstrumentScope || d5.IsEmergency)
            return (false, "Case5: bootstrap flatten should allow non-emergency");

        // 6) Manual / command flatten
        var d6 = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.MANUAL,
            ManualInstrumentFlatten = true,
            ReconstructionActionKind = RecoveryActionKind.Flatten,
            ExecutionInstrumentKey = "MES"
        });
        if (!d6.AllowInstrumentScope)
            return (false, "Case6: manual flatten should allow");

        // 7) Recovery enqueue seal
        var d7 = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.RECOVERY,
            RecoveryEnqueuePolicySealValid = true,
            RecoveryEnqueueAllowInstrument = true,
            RecoveryEnqueueReasonCode = "sealed",
            ReconstructionActionKind = RecoveryActionKind.Flatten,
            ExecutionInstrumentKey = "MES"
        });
        if (!d7.AllowInstrumentScope || d7.PolicyPath != "recovery_enqueue_seal")
            return (false, "Case7: enqueue seal should allow via recovery_enqueue_seal");

        // 8) Reconciliation instrument recovery when freeze path
        var rec = RecoveryOwnershipAttributionEvaluator.EvaluateReconciliationQuantityMismatch(
            "MES", 2, 2,
            new List<(string Stream, int OpenQty)> { ("S1", 1), ("S2", 1) });
        if (!RecoveryOwnershipAttributionEvaluator.CanEscalateReconciliationToInstrumentFreeze(rec, 2, 2))
            return (false, "Case8: setup should freeze");
        var d8 = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.RECONCILIATION,
            ReconciliationInstrumentRecoveryRequested = true,
            ReconciliationAttribution = rec,
            BrokerPositionQty = 2,
            JournalOpenQtySum = 2,
            ExecutionInstrumentKey = "MES"
        });
        if (!d8.AllowInstrumentScope)
            return (false, "Case8: reconciliation instrument recovery should allow");

        // 9) Substring FAIL_CLOSED in middle is NOT emergency (strict prefix only)
        if (RecoveryOwnershipAttributionEvaluator.IsEmergencyInstrumentRecoveryTrigger("X_FAIL_CLOSED_X"))
            return (false, "Case9: embedded FAIL_CLOSED substring must not be emergency");

        // --- P2.6.6 funnel / RequestFlatten final-gate contract (policy inputs) ---

        // 10) RequestFlatten template: default context + ToPolicyInput must hit policy surface (manual allow)
        var defCtx = FlattenPolicyExecutionContext.CreateDefault("FLATTEN_DELEGATED");
        var d10 = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(defCtx.ToPolicyInput("MES", 1, "FLATTEN_DELEGATED"));
        if (!d10.AllowInstrumentScope)
            return (false, "Case10: default flatten context should allow manual instrument flatten");

        // 11) In-context Flatten → COMMAND path (same metadata as NtFlattenInstrumentCommand from IEA)
        var cmdCtx = FlattenPolicyExecutionContext.FromDestructivePolicyInput(new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.COMMAND,
            RecoveryReasonString = "FLATTEN_DELEGATED",
            ExplicitTrigger = DestructiveTriggerReason.MANUAL,
            ManualInstrumentFlatten = true,
            ReconstructionActionKind = RecoveryActionKind.Flatten,
            ExecutionInstrumentKey = "MES",
            BrokerPositionQty = 1,
            JournalOpenQtySum = 0
        });
        var d11 = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(cmdCtx.ToPolicyInput("MES", 1, "FLATTEN_DELEGATED"));
        if (!d11.AllowInstrumentScope || d11.ReasonCode != "COMMAND_FLATTEN")
            return (false, "Case11: COMMAND flatten template should allow via COMMAND_FLATTEN");

        // 12) Recovery flatten (bootstrap administrative + normal recovery seal)
        var d12a = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.BOOTSTRAP,
            BootstrapAdministrativeFlatten = true,
            ReconstructionActionKind = RecoveryActionKind.Flatten,
            ExecutionInstrumentKey = "MES"
        });
        if (!d12a.AllowInstrumentScope)
            return (false, "Case12a: bootstrap administrative flatten should allow");
        var d12b = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.RECOVERY,
            RecoveryEnqueuePolicySealValid = true,
            RecoveryEnqueueAllowInstrument = true,
            RecoveryEnqueueReasonCode = "recovery_attribution_allows_instrument",
            ReconstructionActionKind = RecoveryActionKind.Flatten,
            ExecutionInstrumentKey = "MES"
        });
        if (!d12b.AllowInstrumentScope)
            return (false, "Case12b: recovery NT-command seal should allow");

        // 13) FailClosed path template (matches NtFlattenInstrumentCommand FAIL_CLOSED)
        var d13 = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(
            FlattenPolicyExecutionContext.FromDestructivePolicyInput(new DestructiveActionPolicyInput
            {
                Source = DestructiveActionSource.FAIL_CLOSED,
                ExplicitTrigger = DestructiveTriggerReason.FAIL_CLOSED,
                RecoveryReasonString = "protective_failure",
                ReconstructionActionKind = RecoveryActionKind.Flatten,
                ExecutionInstrumentKey = "MES",
                BrokerPositionQty = 1,
                JournalOpenQtySum = 0
            }).ToPolicyInput("MES", 1, "protective_failure"));
        if (!d13.AllowInstrumentScope || !d13.IsEmergency)
            return (false, "Case13: fail-closed template must allow as emergency");

        // 14) Policy deny → would block order submission (recovery missing attribution)
        var d14 = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.RECOVERY,
            RecoveryReasonString = "SOME_REASON",
            ReconstructionActionKind = RecoveryActionKind.Flatten,
            ExecutionInstrumentKey = "MES",
            BrokerPositionQty = 1,
            JournalOpenQtySum = 1
        });
        if (d14.AllowInstrumentScope)
            return (false, "Case14: recovery without attribution must deny instrument scope");

        // 15) P2.6.7 — DIRECT_EMERGENCY_FLATTEN must classify as emergency allow (constrained EmergencyFlatten API)
        var d15 = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.EMERGENCY,
            ExplicitTrigger = DestructiveTriggerReason.DIRECT_EMERGENCY_FLATTEN,
            RecoveryReasonString = "EMERGENCY_FLATTEN_BYPASS",
            ExecutionInstrumentKey = "MES",
            BrokerPositionQty = 1,
            JournalOpenQtySum = 0,
            ReconstructionActionKind = RecoveryActionKind.Flatten
        });
        if (!d15.AllowInstrumentScope || !d15.IsEmergency || d15.PolicyPath != "emergency_bypass")
            return (false, "Case15: DIRECT_EMERGENCY_FLATTEN must allow via emergency_bypass");

        return (true, null);
    }
}
