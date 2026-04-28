// Tier 1 quant execution control store — mapped / unmapped / parity transitions.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test QUANT_EXECUTION_CONTROL

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class QuantExecutionControlStoreTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var prevStore = FeatureFlags.QuantExecutionControlStoreEnabled;
        var prevAlign = FeatureFlags.EnablePostFillAlignmentGate;
        var prevLag = FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag;
        var prevLedger = FeatureFlags.StructuralLayerUseLedgerOwnership;
        try
        {
            FeatureFlags.QuantExecutionControlStoreEnabled = true;
            FeatureFlags.EnablePostFillAlignmentGate = true;
            FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag = true;

            var e = Case_MappedFillThenParityOk_ReturnsNormal();
            if (e != null) return (false, e);

            e = Case_UnmappedExecution_BlocksSubmit_AllowsFlattenBypass();
            if (e != null) return (false, e);

            e = Case_RegisterAdoptedOrderPath_RecoveredOffersLagProtection();
            if (e != null) return (false, e);

            e = Case_EvaluateEscalation_PendingRecoveryAndImpossible();
            if (e != null) return (false, e);

            e = Case_Integration_ApplyEscalation_TransitionsToRecoveryRequiredPhase();
            if (e != null) return (false, e);

            e = Case_RecoveryRequired_Structural_AllowsProtectiveBlocksEntry();
            if (e != null) return (false, e);

            e = Case_RecoveryRequired_MarketReentry_GrossMismatchBypassesStructuralDeny();
            if (e != null) return (false, e);

            e = Case_WorkingOrderSubmitWindow_IsBoundedAndNonExposure();
            if (e != null) return (false, e);

            e = Case_ProtectiveReplacementWindow_IsBoundedBrokerJournalAlignment();
            if (e != null) return (false, e);

            e = Case_RecoveryRequired_OpeningEntryStopFlatWorkingBypassesStructuralDeny();
            if (e != null) return (false, e);

            e = Case_MarketReentry_UsesJournalAuthorityWhenLedgerIsStale();
            if (e != null) return (false, e);

            e = Case_MarketReentry_PositionMismatchBypassesWhenAuthorityIsReal();
            if (e != null) return (false, e);

            e = Case_RiskCoverage_PositionMismatchBypassesWhenAuthorityIsReal();
            if (e != null) return (false, e);

            e = Case_MarketReentry_NoActiveExposureLagBypassesWhenBrokerAlreadyOpen();
            if (e != null) return (false, e);

            e = Case_RiskCoverage_UsesJournalAuthorityWhenLedgerIsStale();
            if (e != null) return (false, e);

            e = Case_OpeningEntryStop_UsesJournalAuthorityWhenLedgerIsStale();
            if (e != null) return (false, e);

            e = Case_MappedFill_DoesNotMutateRecoveryRequiredUnmappedOrLocked();
            if (e != null) return (false, e);

            e = Case_RepeatedMappedFills_TakeLaterPendingAlignmentExpiry();
            if (e != null) return (false, e);

            e = Case_BrokerExecutionCallbackPending_ArmsBoundedAlignment();
            if (e != null) return (false, e);

            // Last: mutates QuantExecutionControlStoreEnabled to false for the assertion.
            e = Case_StoreDisabled_NoOps();
            if (e != null) return (false, e);

            return (true, null);
        }
        finally
        {
            FeatureFlags.QuantExecutionControlStoreEnabled = prevStore;
            FeatureFlags.EnablePostFillAlignmentGate = prevAlign;
            FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag = prevLag;
            FeatureFlags.StructuralLayerUseLedgerOwnership = prevLedger;
            JournalParityPendingLedger.Clear();
        }
    }

    private static string? Case_MappedFillThenParityOk_ReturnsNormal()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.UtcNow;
        var inst = "QM1";
        JournalParityPendingLedger.TryRecordTrustedFill(inst, "qk1", 2, "intent-q", utc);

        var s1 = QuantExecutionControlStore.GetSnapshot(inst);
        if (s1.Phase != QuantExecutionInstrumentPhase.PendingAlignment)
            return $"expected PendingAlignment after mapped fill, got {s1.Phase}";
        if (s1.Expected.ExpectedSignedNetPosition != 2)
            return $"expected expected net 2, got {s1.Expected.ExpectedSignedNetPosition}";

        PostFillAlignmentGate.ClearOnParityOk(inst);

        var s2 = QuantExecutionControlStore.GetSnapshot(inst);
        if (s2.Phase != QuantExecutionInstrumentPhase.Normal)
            return $"expected Normal after parity OK, got {s2.Phase}";
        return null;
    }

    private static string? Case_UnmappedExecution_BlocksSubmit_AllowsFlattenBypass()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.UtcNow;
        var inst = "QM2";
        QuantExecutionControlStore.NotifyUnmappedExecution(inst, utc, "TEST_UNMAPPED");

        var root = Path.Combine(Path.GetTempPath(), "quant_um_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            // Flat broker + empty journal → PARITY_OK so the flatten-bypass branch can be reached after quant gate skip.
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = 0 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utc
            };

            var req = new ExecutionSafetyEvaluationRequest
            {
                Instrument = inst,
                CanonicalInstrument = inst,
                UtcNow = utc,
                Journal = journal,
                AccountSnapshot = snap,
                UseInstrumentExecutionAuthority = false,
                IeaOwnedPlusAdoptedWorking = 0,
                RecoveryExecutionDisallowed = false,
                JournalIntegrityOrReconciliationRepairActive = false
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var denySnap))
                return "expected structural deny when unmapped";
            if (denySnap.Reason != ExecutionStructuralLayer.StructuralBlocker.QuantUnmappedExecution)
                return "expected quant_unmapped_execution, got " + denySnap.Reason;

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", flattenAuthorityBypass: true, out var flatSnap))
                return "expected structural allow for flatten bypass when unmapped, got " + flatSnap.Reason;
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private static string? Case_StoreDisabled_NoOps()
    {
        JournalParityPendingLedger.Clear();
        FeatureFlags.QuantExecutionControlStoreEnabled = false;
        var utc = DateTimeOffset.UtcNow;
        var inst = "QM3";
        QuantExecutionControlStore.NotifyUnmappedExecution(inst, utc, "SHOULD_NOT_STICK");

        var s = QuantExecutionControlStore.GetSnapshot(inst);
        if (s.Phase != QuantExecutionInstrumentPhase.Normal)
            return "when store disabled, snapshot should remain default Normal";
        return null;
    }

    /// <summary>
    /// Same state transition as InstrumentExecutionAuthority.RegisterAdoptedOrder → NotifyRecoveredReconnect: Recovered + lag protection.
    /// </summary>
    private static string? Case_RegisterAdoptedOrderPath_RecoveredOffersLagProtection()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.UtcNow;
        var inst = "QM4";
        QuantExecutionControlStore.NotifyRecoveredReconnect(inst, utc);

        var s = QuantExecutionControlStore.GetSnapshot(inst);
        if (s.Phase != QuantExecutionInstrumentPhase.Recovered)
            return $"expected Recovered after NotifyRecoveredReconnect, got {s.Phase}";
        if (!s.Expected.RecoveryMode)
            return "expected RecoveryMode true";
        if (!QuantExecutionControlStore.OffersBookkeepingLagProtection(inst, utc))
            return "expected OffersBookkeepingLagProtection true for recovered+recovery";
        return null;
    }

    private static string? Case_EvaluateEscalation_PendingRecoveryAndImpossible()
    {
        JournalParityPendingLedger.Clear();
        var t0 = DateTimeOffset.Parse("2099-06-15T12:00:00Z");
        var instPend = "QM_ESC_PEND";
        var prevWindow = FeatureFlags.PostFillAlignmentWindowMs;
        FeatureFlags.PostFillAlignmentWindowMs = 1000;
        try
        {
            JournalParityPendingLedger.TryRecordTrustedFill(instPend, "ek", 1, "intent-e", t0);
            var rWithin = QuantExecutionControlStore.EvaluateEscalation(instPend, t0.AddMilliseconds(100), brokerGrossPositionAbs: 0);
            if (rWithin.Kind != QuantEscalationKind.StillPendingAlignment)
                return $"pending within window: expected StillPendingAlignment, got {rWithin.Kind} ({rWithin.Reason})";

            var rExpired = QuantExecutionControlStore.EvaluateEscalation(instPend, t0.AddMilliseconds(2000), brokerGrossPositionAbs: 0);
            if (rExpired.Kind != QuantEscalationKind.EscalationRequired || rExpired.Reason != "pending_alignment_expired_broker_gross_mismatch")
                return $"pending expired mismatch: expected escalation pending_alignment_expired_broker_gross_mismatch, got {rExpired.Kind} / {rExpired.Reason}";

            var instRec = "QM_ESC_REC";
            QuantExecutionControlStore.NotifyRecoveredReconnect(instRec, t0);
            const int recoveryMs = 5000;
            var rRecMid = QuantExecutionControlStore.EvaluateEscalation(
                instRec,
                t0.AddMilliseconds(1000),
                brokerGrossPositionAbs: 3,
                recoveryStabilizationWindowMsOverride: recoveryMs);
            if (rRecMid.Kind != QuantEscalationKind.RecoveredLagTolerated)
                return $"recovery within window: expected RecoveredLagTolerated, got {rRecMid.Kind}";

            var rRecLate = QuantExecutionControlStore.EvaluateEscalation(
                instRec,
                t0.AddMilliseconds(recoveryMs + 1000),
                brokerGrossPositionAbs: 3,
                recoveryStabilizationWindowMsOverride: recoveryMs);
            if (rRecLate.Kind != QuantEscalationKind.EscalationRequired ||
                rRecLate.Reason != "recovery_stabilization_window_expired_broker_gross_mismatch")
                return $"recovery expired: expected recovery_stabilization_window_expired_broker_gross_mismatch, got {rRecLate.Kind} / {rRecLate.Reason}";

            var instSign = "QM_ESC_SIGN";
            JournalParityPendingLedger.Clear();
            JournalParityPendingLedger.TryRecordTrustedFill(instSign, "sk", 5, "i", t0);
            var rSign = QuantExecutionControlStore.EvaluateEscalation(
                instSign,
                t0.AddMilliseconds(100),
                brokerGrossPositionAbs: 5,
                brokerSignedNetPosition: -5);
            if (rSign.Kind != QuantEscalationKind.EscalationRequired || rSign.Reason != "impossible_signed_net_direction")
                return $"impossible sign: got {rSign.Kind} / {rSign.Reason}";

            var instUm = "QM_ESC_UM";
            QuantExecutionControlStore.NotifyUnmappedExecution(instUm, t0, "x");
            var rUm = QuantExecutionControlStore.EvaluateEscalation(instUm, t0, brokerGrossPositionAbs: 1);
            if (rUm.Kind != QuantEscalationKind.NoAction || rUm.Reason != "terminal_phase_handled_elsewhere")
                return $"unmapped should no-op escalation, got {rUm.Kind} / {rUm.Reason}";
        }
        finally
        {
            FeatureFlags.PostFillAlignmentWindowMs = prevWindow;
        }

        return null;
    }

    /// <summary>
    /// Engine-style chain: Tier-1 evaluation returns EscalationRequired → apply → durable <see cref="QuantExecutionInstrumentPhase.RecoveryRequired"/>.
    /// </summary>
    private static string? Case_Integration_ApplyEscalation_TransitionsToRecoveryRequiredPhase()
    {
        JournalParityPendingLedger.Clear();
        QuantExecutionControlStore.Clear();
        var t0 = DateTimeOffset.Parse("2099-07-01T12:00:00Z");
        var prev = FeatureFlags.PostFillAlignmentWindowMs;
        FeatureFlags.PostFillAlignmentWindowMs = 500;
        try
        {
            var inst1 = "QM_INT_PEND";
            JournalParityPendingLedger.TryRecordTrustedFill(inst1, "k1", 1, "i", t0);
            var ap1 = QuantExecutionControlStore.EvaluateEscalationAndApplyIfRequired(inst1, t0.AddSeconds(2), 0, null);
            if (ap1.Kind != QuantEscalationKind.EscalationRequired)
                return "integration pending: expected EscalationRequired";
            var s1 = QuantExecutionControlStore.GetSnapshot(inst1);
            if (s1.Phase != QuantExecutionInstrumentPhase.RecoveryRequired)
                return $"integration pending: expected RecoveryRequired, got {s1.Phase}";
            if (s1.RecoveryRequiredReason != "pending_alignment_expired_broker_gross_mismatch")
                return "integration pending: wrong reason " + s1.RecoveryRequiredReason;

            var ap1b = QuantExecutionControlStore.EvaluateEscalation(inst1, t0.AddSeconds(3), 0, null);
            if (ap1b.Kind != QuantEscalationKind.NoAction || ap1b.Reason != "already_recovery_required")
                return "integration pending: second eval should already_recovery_required";

            var inst2 = "QM_INT_REC";
            QuantExecutionControlStore.NotifyRecoveredReconnect(inst2, t0);
            var ap2 = QuantExecutionControlStore.EvaluateEscalationAndApplyIfRequired(
                inst2,
                t0.AddMilliseconds(6500),
                3,
                null,
                recoveryStabilizationWindowMsOverride: 5000);
            if (ap2.Kind != QuantEscalationKind.EscalationRequired)
                return "integration rec: expected EscalationRequired";
            var s2 = QuantExecutionControlStore.GetSnapshot(inst2);
            if (s2.Phase != QuantExecutionInstrumentPhase.RecoveryRequired)
                return $"integration rec: expected RecoveryRequired, got {s2.Phase}";
            if (s2.RecoveryRequiredReason != "recovery_stabilization_window_expired_broker_gross_mismatch")
                return "integration rec: wrong reason " + s2.RecoveryRequiredReason;
        }
        finally
        {
            FeatureFlags.PostFillAlignmentWindowMs = prev;
        }

        return null;
    }

    /// <summary>
    /// Phase 4B contract: mapped fill does not transition <see cref="QuantExecutionInstrumentPhase.RecoveryRequired"/> to
    /// <see cref="QuantExecutionInstrumentPhase.PendingAlignment"/> (no post-fill convergence softening). Unmapped and
    /// execution-locked entries are likewise immutable on mapped fill.
    /// </summary>
    private static string? Case_MappedFill_DoesNotMutateRecoveryRequiredUnmappedOrLocked()
    {
        JournalParityPendingLedger.Clear();
        QuantExecutionControlStore.Clear();
        var t0 = DateTimeOffset.Parse("2099-08-10T12:00:00Z");
        var prev = FeatureFlags.PostFillAlignmentWindowMs;
        FeatureFlags.PostFillAlignmentWindowMs = 500;
        try
        {
            var instRr = "QM_RR_MAP";
            JournalParityPendingLedger.TryRecordTrustedFill(instRr, "k1", 1, "i", t0);
            QuantExecutionControlStore.EvaluateEscalationAndApplyIfRequired(instRr, t0.AddSeconds(2), 0, null);
            var snapRr = QuantExecutionControlStore.GetSnapshot(instRr);
            if (snapRr.Phase != QuantExecutionInstrumentPhase.RecoveryRequired)
                return $"RR setup: expected RecoveryRequired, got {snapRr.Phase}";
            var netBefore = snapRr.Expected.ExpectedSignedNetPosition;
            var reasonBefore = snapRr.RecoveryRequiredReason;

            QuantExecutionControlStore.NotifyMappedTrustedFill(instRr, 5, t0.AddMinutes(1));
            var afterRr = QuantExecutionControlStore.GetSnapshot(instRr);
            if (afterRr.Phase != QuantExecutionInstrumentPhase.RecoveryRequired)
                return $"after mapped fill on RR: expected RecoveryRequired, got {afterRr.Phase}";
            if (afterRr.Expected.ExpectedSignedNetPosition != netBefore)
                return $"RR: expected expected net unchanged ({netBefore}), got {afterRr.Expected.ExpectedSignedNetPosition}";
            if (afterRr.RecoveryRequiredReason != reasonBefore)
                return "RR: RecoveryRequiredReason should not change from mapped fill";

            var instUm = "QM_UM_MAP";
            QuantExecutionControlStore.NotifyUnmappedExecution(instUm, t0, "TEST_UM");
            QuantExecutionControlStore.NotifyMappedTrustedFill(instUm, 2, t0.AddSeconds(1));
            var afterUm = QuantExecutionControlStore.GetSnapshot(instUm);
            if (afterUm.Phase != QuantExecutionInstrumentPhase.UnmappedExecution)
                return $"unmapped: expected UnmappedExecution after mapped fill, got {afterUm.Phase}";

            var instLk = "QM_LK_MAP";
            QuantExecutionControlStore.NotifyExecutionLocked(instLk, "TEST_LOCK", t0);
            QuantExecutionControlStore.NotifyMappedTrustedFill(instLk, 2, t0.AddSeconds(1));
            var afterLk = QuantExecutionControlStore.GetSnapshot(instLk);
            if (afterLk.Phase != QuantExecutionInstrumentPhase.ExecutionLocked)
                return $"locked: expected ExecutionLocked after mapped fill, got {afterLk.Phase}";
        }
        finally
        {
            FeatureFlags.PostFillAlignmentWindowMs = prev;
        }

        return null;
    }

    /// <summary>
    /// Documented contract: successive PendingAlignment mapped fills set <see cref="QuantExpectedInstrumentState.PendingAlignmentExpiresUtc"/>
    /// to the later of (previous expiry) and (this fill's utcNow + windowMs).
    /// </summary>
    private static string? Case_RepeatedMappedFills_TakeLaterPendingAlignmentExpiry()
    {
        QuantExecutionControlStore.Clear();
        var t0 = DateTimeOffset.Parse("2099-08-11T12:00:00Z");
        var prev = FeatureFlags.PostFillAlignmentWindowMs;
        FeatureFlags.PostFillAlignmentWindowMs = 5000;
        try
        {
            const string inst = "QM_REP_FILL";
            QuantExecutionControlStore.NotifyMappedTrustedFill(inst, 1, t0);
            var e1 = QuantExecutionControlStore.GetSnapshot(inst).Expected.PendingAlignmentExpiresUtc;
            var expectFirst = t0.AddMilliseconds(5000);
            if (e1 != expectFirst)
                return $"first fill: expected expiry {expectFirst}, got {e1}";

            var tSecond = t0.AddMilliseconds(1000);
            QuantExecutionControlStore.NotifyMappedTrustedFill(inst, 1, tSecond);
            var e2 = QuantExecutionControlStore.GetSnapshot(inst).Expected.PendingAlignmentExpiresUtc;
            var expectSecond = tSecond.AddMilliseconds(5000);
            if (e2 != expectSecond)
                return $"second fill: expected expiry {expectSecond} (later deadline), got {e2}";

            if (!QuantExecutionControlStore.IsPostFillAlignmentWindowActive(inst, t0.AddMilliseconds(5500)))
                return "at t0+5500ms window should still be active (extended past first fill's t0+5000)";
            if (QuantExecutionControlStore.IsPostFillAlignmentWindowActive(inst, t0.AddMilliseconds(6500)))
                return "at t0+6500ms window should be inactive (after extended expiry t0+6000)";
        }
        finally
        {
            FeatureFlags.PostFillAlignmentWindowMs = prev;
        }

        return null;
    }

    private static string? Case_RecoveryRequired_Structural_AllowsProtectiveBlocksEntry()
    {
        JournalParityPendingLedger.Clear();
        QuantExecutionControlStore.Clear();
        var t0 = DateTimeOffset.Parse("2099-07-02T12:00:00Z");
        var prev = FeatureFlags.PostFillAlignmentWindowMs;
        FeatureFlags.PostFillAlignmentWindowMs = 100;
        try
        {
            var inst = "QM_ST_RR";
            JournalParityPendingLedger.TryRecordTrustedFill(inst, "sk", 1, "i", t0);
            QuantExecutionControlStore.EvaluateEscalationAndApplyIfRequired(inst, t0.AddSeconds(1), 0, null);
            if (QuantExecutionControlStore.GetSnapshot(inst).Phase != QuantExecutionInstrumentPhase.RecoveryRequired)
                return "structural: setup RecoveryRequired failed";

            var root = Path.Combine(Path.GetTempPath(), "quant_rr_struct_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var log = new RobotLogger(root);
                var journal = new ExecutionJournal(root, log);
                var snap = new AccountSnapshot
                {
                    Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = 0 } },
                    WorkingOrders = new List<WorkingOrderSnapshot>(),
                    CapturedAtUtc = t0
                };
                var req = new ExecutionSafetyEvaluationRequest
                {
                    Instrument = inst,
                    CanonicalInstrument = inst,
                    UtcNow = t0,
                    Journal = journal,
                    AccountSnapshot = snap,
                    UseInstrumentExecutionAuthority = false,
                    IeaOwnedPlusAdoptedWorking = 0,
                    RecoveryExecutionDisallowed = false,
                    JournalIntegrityOrReconciliationRepairActive = false
                };

                if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var deny))
                    return "structural: expected deny SUBMIT_ENTRY when RecoveryRequired";
                if (deny.Reason != ExecutionStructuralLayer.StructuralBlocker.QuantRecoveryRequired)
                    return "structural: wrong deny reason " + deny.Reason;

                if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", false, out var okProt))
                    return "structural: expected allow protective, got " + okProt.Reason;
                return null;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, true);
                }
                catch
                {
                    /* best effort */
                }
            }
        }
        finally
        {
            FeatureFlags.PostFillAlignmentWindowMs = prev;
        }
    }

    private static string? Case_BrokerExecutionCallbackPending_ArmsBoundedAlignment()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.UtcNow;
        var inst = "QCB1";

        QuantExecutionControlStore.NotifyBrokerExecutionCallbackPending(inst, utc, "ENTRY", "FILLED");
        var snap = QuantExecutionControlStore.GetSnapshot(inst);
        if (snap.Phase != QuantExecutionInstrumentPhase.PendingAlignment)
            return $"expected PendingAlignment after broker callback, got {snap.Phase}";
        if (snap.Expected.LastBrokerExecutionCallbackRole != "ENTRY" ||
            snap.Expected.LastBrokerExecutionCallbackState != "FILLED")
            return "broker callback diagnostics were not preserved";
        if (!QuantExecutionControlStore.IsBrokerExecutionCallbackPendingActive(inst, utc.AddMilliseconds(1)))
            return "expected broker callback pending window to be active";
        if (!PendingAlignmentAuthority.IsPendingAlignment(inst, utc.AddMilliseconds(1)))
            return "expected PendingAlignmentAuthority to include broker callback pending window";

        var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
        if (QuantExecutionControlStore.IsBrokerExecutionCallbackPendingActive(inst, utc.AddMilliseconds(windowMs + 10)))
            return "broker callback pending window did not expire";

        QuantExecutionControlStore.NotifyParityOkBrokerJournalAligned(inst, utc.AddMilliseconds(5));
        snap = QuantExecutionControlStore.GetSnapshot(inst);
        if (snap.Phase != QuantExecutionInstrumentPhase.Normal)
            return $"expected Normal after parity OK, got {snap.Phase}";
        if (snap.Expected.LastBrokerExecutionCallbackUtc.HasValue)
            return "parity OK did not clear broker callback pending state";

        return null;
    }

    private static string? Case_RecoveryRequired_MarketReentry_GrossMismatchBypassesStructuralDeny()
    {
        JournalParityPendingLedger.Clear();
        QuantExecutionControlStore.Clear();
        var t0 = DateTimeOffset.Parse("2099-07-03T12:00:00Z");
        var prev = FeatureFlags.PostFillAlignmentWindowMs;
        FeatureFlags.PostFillAlignmentWindowMs = 100;
        try
        {
            var inst = "QM_REENTRY_RR";
            JournalParityPendingLedger.TryRecordTrustedFill(inst, "rk", 1, "i", t0);
            QuantExecutionControlStore.EvaluateEscalationAndApplyIfRequired(inst, t0.AddSeconds(1), 0, null);
            var qSnap = QuantExecutionControlStore.GetSnapshot(inst);
            if (qSnap.Phase != QuantExecutionInstrumentPhase.RecoveryRequired)
                return "market reentry bypass: setup RecoveryRequired failed";
            if (qSnap.RecoveryRequiredReason != "pending_alignment_expired_broker_gross_mismatch")
                return "market reentry bypass: wrong recovery reason " + qSnap.RecoveryRequiredReason;

            var root = Path.Combine(Path.GetTempPath(), "quant_rr_reentry_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var log = new RobotLogger(root);
                var journal = new ExecutionJournal(root, log);
                var flatFirstSnap = new AccountSnapshot
                {
                    Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = 0 } },
                    WorkingOrders = new List<WorkingOrderSnapshot>(),
                    CapturedAtUtc = t0
                };
                var flatFirstReq = new ExecutionSafetyEvaluationRequest
                {
                    Instrument = inst,
                    CanonicalInstrument = inst,
                    UtcNow = t0.AddSeconds(1),
                    Journal = journal,
                    AccountSnapshot = flatFirstSnap,
                    UseInstrumentExecutionAuthority = true,
                    IeaOwnedPlusAdoptedWorking = 0,
                    RecoveryExecutionDisallowed = false,
                    JournalIntegrityOrReconciliationRepairActive = false
                };

                if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(flatFirstReq, "SUBMIT_ENTRY_STOP", false, out var firstOk))
                    return "opening entry stop first order: expected allow, got " + firstOk.Reason;
                if (firstOk.Detail == null || firstOk.Detail.IndexOf("quant_recovery_required_cleared_authoritative_flat", StringComparison.Ordinal) < 0)
                    return "opening entry stop first order: missing recovery clear detail, got " + firstOk.Detail;
                if (QuantExecutionControlStore.GetSnapshot(inst).Phase != QuantExecutionInstrumentPhase.Normal)
                    return "opening entry stop first order: expected RecoveryRequired to clear";

                JournalParityPendingLedger.Clear();
                QuantExecutionControlStore.Clear();
                QuantExecutionControlStore.NotifyMappedTrustedFill(inst, 1, t0);
                QuantExecutionControlStore.EvaluateEscalationAndApplyIfRequired(inst, t0.AddSeconds(1), 0, null);
                QuantExecutionControlStore.NotifyWorkingOrderSubmitTransition(inst, 1, t0.AddSeconds(1));

                var snap = new AccountSnapshot
                {
                    Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = 0 } },
                    WorkingOrders = new List<WorkingOrderSnapshot>(),
                    CapturedAtUtc = t0
                };
                var req = new ExecutionSafetyEvaluationRequest
                {
                    Instrument = inst,
                    CanonicalInstrument = inst,
                    UtcNow = t0,
                    Journal = journal,
                    AccountSnapshot = snap,
                    UseInstrumentExecutionAuthority = false,
                    IeaOwnedPlusAdoptedWorking = 0,
                    RecoveryExecutionDisallowed = false,
                    JournalIntegrityOrReconciliationRepairActive = false
                };

                if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_MARKET_REENTRY", false, out var ok))
                    return "market reentry bypass: expected allow, got " + ok.Reason;
                if (ok.Detail == null || ok.Detail.IndexOf("market_reentry_quant_recovery_gross_mismatch_bypass", StringComparison.Ordinal) < 0)
                    return "market reentry bypass: missing bypass detail, got " + ok.Detail;
                return null;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, true);
                }
                catch
                {
                    /* best effort */
                }
            }
        }
        finally
        {
            FeatureFlags.PostFillAlignmentWindowMs = prev;
        }
    }

    private static string? Case_WorkingOrderSubmitWindow_IsBoundedAndNonExposure()
    {
        JournalParityPendingLedger.Clear();
        QuantExecutionControlStore.Clear();
        var t0 = DateTimeOffset.Parse("2099-07-03T12:10:00Z");
        var prev = FeatureFlags.PostFillAlignmentWindowMs;
        FeatureFlags.PostFillAlignmentWindowMs = 500;
        try
        {
            const string inst = "QM_WORK_SUBMIT";
            QuantExecutionControlStore.NotifyWorkingOrderSubmitTransition(inst, 1, t0);
            if (!QuantExecutionControlStore.IsWorkingOrderSubmitWindowActive(inst, t0.AddMilliseconds(100)))
                return "working-order submit window should be active inside bounded window";
            if (QuantExecutionControlStore.IsWorkingOrderSubmitWindowActive(inst, t0.AddMilliseconds(700)))
                return "working-order submit window should expire";

            var r = QuantExecutionControlStore.EvaluateEscalationAndApplyIfRequired(inst, t0.AddMilliseconds(700), 0, null);
            if (r.Kind != QuantEscalationKind.NoAction || r.Reason != "pending_alignment_expired_gross_match_stale_phase")
                return $"working-order submit should not escalate flat broker as exposure, got {r.Kind}/{r.Reason}";

            var s = QuantExecutionControlStore.GetSnapshot(inst);
            if (s.Phase == QuantExecutionInstrumentPhase.RecoveryRequired)
                return "working-order submit transition must not create RecoveryRequired";
            return null;
        }
        finally
        {
            FeatureFlags.PostFillAlignmentWindowMs = prev;
        }
    }

    private static string? Case_ProtectiveReplacementWindow_IsBoundedBrokerJournalAlignment()
    {
        JournalParityPendingLedger.Clear();
        QuantExecutionControlStore.Clear();
        var t0 = DateTimeOffset.Parse("2099-07-03T12:15:00Z");
        var prev = FeatureFlags.PostFillAlignmentWindowMs;
        FeatureFlags.PostFillAlignmentWindowMs = 500;
        try
        {
            const string inst = "MNQ_BE_REPLACE";
            QuantExecutionControlStore.NotifyProtectiveCancelReplacePending(inst, 2, t0);
            if (!QuantExecutionControlStore.IsProtectiveCancelReplacePendingActive(inst, t0.AddMilliseconds(100)))
                return "protective cancel/replace window should be active inside bounded window";
            if (!QuantExecutionControlStore.IsAnyBrokerJournalAlignmentWindowActive(inst, t0.AddMilliseconds(100)))
                return "protective cancel/replace should count as broker/journal alignment";
            if (QuantExecutionControlStore.IsProtectiveCancelReplacePendingActive(inst, t0.AddMilliseconds(700)))
                return "protective cancel/replace window should expire";
            if (QuantExecutionControlStore.IsAnyBrokerJournalAlignmentWindowActive(inst, t0.AddMilliseconds(700)))
                return "protective cancel/replace should not keep broker/journal alignment active after expiry";

            QuantExecutionControlStore.Clear();
            QuantExecutionControlStore.NotifyProtectiveResizePending(inst, 1, t0);
            if (!QuantExecutionControlStore.IsAnyBrokerJournalAlignmentWindowActive(inst, t0.AddMilliseconds(100)))
                return "protective resize should count as broker/journal alignment";

            return null;
        }
        finally
        {
            FeatureFlags.PostFillAlignmentWindowMs = prev;
        }
    }

    private static string? Case_RecoveryRequired_OpeningEntryStopFlatWorkingBypassesStructuralDeny()
    {
        JournalParityPendingLedger.Clear();
        QuantExecutionControlStore.Clear();
        var t0 = DateTimeOffset.Parse("2099-07-03T12:20:00Z");
        var prev = FeatureFlags.PostFillAlignmentWindowMs;
        FeatureFlags.PostFillAlignmentWindowMs = 100;
        try
        {
            const string inst = "QM_ENTRY_STOP_RR";
            QuantExecutionControlStore.NotifyMappedTrustedFill(inst, 1, t0);
            QuantExecutionControlStore.EvaluateEscalationAndApplyIfRequired(inst, t0.AddSeconds(1), 0, null);
            var rr = QuantExecutionControlStore.GetSnapshot(inst);
            if (rr.Phase != QuantExecutionInstrumentPhase.RecoveryRequired)
                return "opening entry stop bypass: setup RecoveryRequired failed";
            QuantExecutionControlStore.NotifyWorkingOrderSubmitTransition(inst, 1, t0.AddSeconds(1));

            var root = Path.Combine(Path.GetTempPath(), "quant_rr_entry_stop_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var log = new RobotLogger(root);
                var journal = new ExecutionJournal(root, log);
                var snap = new AccountSnapshot
                {
                    Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = 0 } },
                    WorkingOrders = new List<WorkingOrderSnapshot>
                    {
                        new() { Instrument = inst, OrderId = "broker-entry-working", Tag = "QTSW2:test", Quantity = 1 }
                    },
                    CapturedAtUtc = t0
                };
                var req = new ExecutionSafetyEvaluationRequest
                {
                    Instrument = inst,
                    CanonicalInstrument = inst,
                    UtcNow = t0.AddSeconds(1),
                    Journal = journal,
                    AccountSnapshot = snap,
                    UseInstrumentExecutionAuthority = true,
                    IeaOwnedPlusAdoptedWorking = 1,
                    RecoveryExecutionDisallowed = false,
                    JournalIntegrityOrReconciliationRepairActive = false
                };

                if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var denyPlain))
                    return "opening entry stop bypass: plain SUBMIT_ENTRY should still deny RecoveryRequired";
                if (denyPlain.Reason != ExecutionStructuralLayer.StructuralBlocker.QuantRecoveryRequired)
                    return "opening entry stop bypass: wrong plain deny reason " + denyPlain.Reason;

                if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY_STOP", false, out var ok))
                    return "opening entry stop bypass: expected allow, got " + ok.Reason;
                if (ok.Detail == null || ok.Detail.IndexOf("opening_entry_stop_quant_recovery_bypass_flat_working_order_transition", StringComparison.Ordinal) < 0)
                    return "opening entry stop bypass: missing detail, got " + ok.Detail;
                return null;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, true);
                }
                catch
                {
                    /* best effort */
                }
            }
        }
        finally
        {
            FeatureFlags.PostFillAlignmentWindowMs = prev;
        }
    }

    private static string? Case_MarketReentry_UsesJournalAuthorityWhenLedgerIsStale()
    {
        JournalParityPendingLedger.Clear();
        QuantExecutionControlStore.Clear();
        var prevLedger = FeatureFlags.StructuralLayerUseLedgerOwnership;
        FeatureFlags.StructuralLayerUseLedgerOwnership = true;
        var t0 = DateTimeOffset.Parse("2099-07-04T12:00:00Z");
        var root = Path.Combine(Path.GetTempPath(), "market_reentry_stale_ledger_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = "QM_REENTRY_LEDGER", Quantity = 0 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = t0
            };
            var req = new ExecutionSafetyEvaluationRequest
            {
                Instrument = "QM_REENTRY_LEDGER",
                CanonicalInstrument = "QM_REENTRY_LEDGER",
                UtcNow = t0,
                Journal = journal,
                AccountSnapshot = snap,
                UseInstrumentExecutionAuthority = false,
                IeaOwnedPlusAdoptedWorking = 0,
                RecoveryExecutionDisallowed = false,
                JournalIntegrityOrReconciliationRepairActive = false,
                LedgerOwnershipSnapshot = new InstrumentOwnershipSnapshot
                {
                    Account = "default",
                    ExecutionInstrumentKey = "QM_REENTRY_LEDGER",
                    OwnershipVersion = 1,
                    LedgerSignedNetQty = -2,
                    SnapshotUtc = t0
                }
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var denyEntry))
                return "stale ledger reentry: expected SUBMIT_ENTRY deny under stale ledger authority";
            if (denyEntry.Reason != ExecutionStructuralLayer.StructuralBlocker.AuthorityUnknown)
                return "stale ledger reentry: wrong SUBMIT_ENTRY deny reason " + denyEntry.Reason;

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_MARKET_REENTRY", false, out var ok))
                return "stale ledger reentry: expected reentry allow, got " + ok.Reason;
            if (ok.Detail == null || ok.Detail.IndexOf("market_reentry_authority_fallback_journal_real", StringComparison.Ordinal) < 0)
                return "stale ledger reentry: missing journal fallback detail, got " + ok.Detail;
            return null;
        }
        finally
        {
            FeatureFlags.StructuralLayerUseLedgerOwnership = prevLedger;
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private static string? Case_MarketReentry_PositionMismatchBypassesWhenAuthorityIsReal()
    {
        JournalParityPendingLedger.Clear();
        QuantExecutionControlStore.Clear();
        var prevLedger = FeatureFlags.StructuralLayerUseLedgerOwnership;
        FeatureFlags.StructuralLayerUseLedgerOwnership = true;
        var t0 = DateTimeOffset.Parse("2099-07-05T12:00:00Z");
        var root = Path.Combine(Path.GetTempPath(), "market_reentry_parity_real_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = "QM_REENTRY_PARITY", Quantity = 2 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = t0
            };
            var req = new ExecutionSafetyEvaluationRequest
            {
                Instrument = "QM_REENTRY_PARITY",
                CanonicalInstrument = "QM_REENTRY_PARITY",
                UtcNow = t0,
                Journal = journal,
                AccountSnapshot = snap,
                UseInstrumentExecutionAuthority = false,
                IeaOwnedPlusAdoptedWorking = 0,
                RecoveryExecutionDisallowed = false,
                JournalIntegrityOrReconciliationRepairActive = false,
                LedgerOwnershipSnapshot = new InstrumentOwnershipSnapshot
                {
                    Account = "default",
                    ExecutionInstrumentKey = "QM_REENTRY_PARITY",
                    OwnershipVersion = 1,
                    LedgerSignedNetQty = -2,
                    SnapshotUtc = t0
                }
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var denyEntry))
                return "market reentry parity bypass: expected SUBMIT_ENTRY deny under parity mismatch";
            if (denyEntry.Reason != ExecutionStructuralLayer.StructuralBlocker.ParityNotOk)
                return "market reentry parity bypass: wrong SUBMIT_ENTRY deny reason " + denyEntry.Reason;

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_MARKET_REENTRY", false, out var ok))
                return "market reentry parity bypass: expected allow, got " + ok.Reason;
            if (ok.Detail == null || ok.Detail.IndexOf("market_reentry_parity_position_mismatch_bypass_real_authority", StringComparison.Ordinal) < 0)
                return "market reentry parity bypass: missing parity bypass detail, got " + ok.Detail;
            return null;
        }
        finally
        {
            FeatureFlags.StructuralLayerUseLedgerOwnership = prevLedger;
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private static string? Case_RiskCoverage_UsesJournalAuthorityWhenLedgerIsStale()
    {
        JournalParityPendingLedger.Clear();
        QuantExecutionControlStore.Clear();
        var prevLedger = FeatureFlags.StructuralLayerUseLedgerOwnership;
        FeatureFlags.StructuralLayerUseLedgerOwnership = true;
        var t0 = DateTimeOffset.Parse("2099-07-06T12:00:00Z");
        var root = Path.Combine(Path.GetTempPath(), "risk_coverage_stale_ledger_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            const string inst = "QM_PROTECTIVE_LEDGER";
            journal.RecordSubmission("intent-prot", "2099-07-06", "S1", inst, "ENTRY", "broker-entry", t0);
            journal.RecordEntryFill("intent-prot", "2099-07-06", "S1", 100m, 2, t0, 1m, "Short", inst, inst);

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = -2 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = t0
            };
            var req = new ExecutionSafetyEvaluationRequest
            {
                Instrument = inst,
                CanonicalInstrument = inst,
                UtcNow = t0,
                Journal = journal,
                AccountSnapshot = snap,
                UseInstrumentExecutionAuthority = false,
                IeaOwnedPlusAdoptedWorking = 0,
                RecoveryExecutionDisallowed = false,
                JournalIntegrityOrReconciliationRepairActive = false,
                LedgerOwnershipSnapshot = new InstrumentOwnershipSnapshot
                {
                    Account = "default",
                    ExecutionInstrumentKey = inst,
                    OwnershipVersion = 1,
                    LedgerSignedNetQty = 0,
                    SnapshotUtc = t0
                }
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var denyEntry))
                return "risk coverage stale ledger: expected SUBMIT_ENTRY deny under stale ledger authority";
            if (denyEntry.Reason != ExecutionStructuralLayer.StructuralBlocker.AuthorityUnknown)
                return "risk coverage stale ledger: wrong SUBMIT_ENTRY deny reason " + denyEntry.Reason;

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", false, out var okProt))
                return "risk coverage stale ledger: expected protective stop allow, got " + okProt.Reason;
            if (okProt.Detail == null || okProt.Detail.IndexOf("risk_coverage_authority_fallback_journal_real", StringComparison.Ordinal) < 0)
                return "risk coverage stale ledger: missing protective fallback detail, got " + okProt.Detail;

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_TARGET", false, out var okTgt))
                return "risk coverage stale ledger: expected target allow, got " + okTgt.Reason;
            if (okTgt.Detail == null || okTgt.Detail.IndexOf("risk_coverage_authority_fallback_journal_real", StringComparison.Ordinal) < 0)
                return "risk coverage stale ledger: missing target fallback detail, got " + okTgt.Detail;

            return null;
        }
        finally
        {
            FeatureFlags.StructuralLayerUseLedgerOwnership = prevLedger;
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private static string? Case_RiskCoverage_PositionMismatchBypassesWhenAuthorityIsReal()
    {
        JournalParityPendingLedger.Clear();
        QuantExecutionControlStore.Clear();
        var prevLedger = FeatureFlags.StructuralLayerUseLedgerOwnership;
        FeatureFlags.StructuralLayerUseLedgerOwnership = true;
        var t0 = DateTimeOffset.Parse("2099-07-06T12:30:00Z");
        var root = Path.Combine(Path.GetTempPath(), "risk_coverage_parity_real_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            const string inst = "QM_PROTECTIVE_PARITY";
            journal.RecordSubmission("intent-prot-parity", "2099-07-06", "S1", inst, "ENTRY", "broker-entry", t0);
            journal.RecordEntryFill("intent-prot-parity", "2099-07-06", "S1", 100m, 2, t0, 1m, "Short", inst, inst);

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = 4 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = t0
            };
            var req = new ExecutionSafetyEvaluationRequest
            {
                Instrument = inst,
                CanonicalInstrument = inst,
                UtcNow = t0,
                Journal = journal,
                AccountSnapshot = snap,
                UseInstrumentExecutionAuthority = false,
                IeaOwnedPlusAdoptedWorking = 0,
                RecoveryExecutionDisallowed = false,
                JournalIntegrityOrReconciliationRepairActive = false,
                LedgerOwnershipSnapshot = new InstrumentOwnershipSnapshot
                {
                    Account = "default",
                    ExecutionInstrumentKey = inst,
                    OwnershipVersion = 1,
                    LedgerSignedNetQty = -4,
                    SnapshotUtc = t0
                }
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var denyEntry))
                return "risk coverage parity bypass: expected SUBMIT_ENTRY deny under parity mismatch";
            if (denyEntry.Reason != ExecutionStructuralLayer.StructuralBlocker.ParityNotOk)
                return "risk coverage parity bypass: wrong SUBMIT_ENTRY deny reason " + denyEntry.Reason;

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", false, out var okProt))
                return "risk coverage parity bypass: expected protective stop allow, got " + okProt.Reason;
            if (okProt.Detail == null || okProt.Detail.IndexOf("risk_coverage_parity_position_mismatch_bypass_real_authority", StringComparison.Ordinal) < 0)
                return "risk coverage parity bypass: missing protective bypass detail, got " + okProt.Detail;

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_TARGET", false, out var okTgt))
                return "risk coverage parity bypass: expected target allow, got " + okTgt.Reason;
            if (okTgt.Detail == null || okTgt.Detail.IndexOf("risk_coverage_parity_position_mismatch_bypass_real_authority", StringComparison.Ordinal) < 0)
                return "risk coverage parity bypass: missing target bypass detail, got " + okTgt.Detail;

            return null;
        }
        finally
        {
            FeatureFlags.StructuralLayerUseLedgerOwnership = prevLedger;
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private static string? Case_OpeningEntryStop_UsesJournalAuthorityWhenLedgerIsStale()
    {
        JournalParityPendingLedger.Clear();
        QuantExecutionControlStore.Clear();
        var prevLedger = FeatureFlags.StructuralLayerUseLedgerOwnership;
        FeatureFlags.StructuralLayerUseLedgerOwnership = true;
        var t0 = DateTimeOffset.Parse("2099-07-08T12:00:00Z");
        var root = Path.Combine(Path.GetTempPath(), "quant_entry_stop_stale_ledger_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            const string inst = "QM_ENTRY_STOP_LEDGER";
            const string intent = "ENTRY_STOP_STALE_LEDGER";
            journal.RecordSubmission(
                intent,
                "2099-07-08",
                "QM_ENTRY_STOP_STREAM",
                inst,
                "ENTRY_STOP",
                "broker-entry-stop",
                t0,
                expectedEntryPrice: 100m,
                entryPrice: 100m,
                stopPrice: 102m,
                targetPrice: 96m,
                beTriggerPrice: 98m,
                direction: "SHORT");
            journal.RecordEntryFill(intent, "2099-07-08", "QM_ENTRY_STOP_STREAM", 100m, 2, t0.AddSeconds(1), 1m,
                "Short", inst, inst);

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = -2 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = t0
            };

            var req = new ExecutionSafetyEvaluationRequest
            {
                Instrument = inst,
                CanonicalInstrument = inst,
                UtcNow = t0,
                Journal = journal,
                AccountSnapshot = snap,
                UseInstrumentExecutionAuthority = false,
                IeaOwnedPlusAdoptedWorking = 0,
                RecoveryExecutionDisallowed = false,
                JournalIntegrityOrReconciliationRepairActive = false,
                LedgerOwnershipSnapshot = new InstrumentOwnershipSnapshot
                {
                    Account = "default",
                    ExecutionInstrumentKey = inst,
                    OwnershipVersion = 1,
                    LedgerSignedNetQty = 0,
                    SnapshotUtc = t0
                }
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var denyEntry))
                return "opening entry stop stale ledger: expected SUBMIT_ENTRY deny under stale ledger authority";
            if (denyEntry.Reason != ExecutionStructuralLayer.StructuralBlocker.AuthorityUnknown)
                return "opening entry stop stale ledger: wrong SUBMIT_ENTRY deny reason " + denyEntry.Reason;

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY_STOP", false, out var ok))
                return "opening entry stop stale ledger: expected SUBMIT_ENTRY_STOP allow, got " + ok.Reason;
            if (ok.Detail == null || ok.Detail.IndexOf("opening_entry_stop_authority_fallback_journal_real", StringComparison.Ordinal) < 0)
                return "opening entry stop stale ledger: missing fallback detail, got " + ok.Detail;

            return null;
        }
        finally
        {
            FeatureFlags.StructuralLayerUseLedgerOwnership = prevLedger;
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private static string? Case_MarketReentry_NoActiveExposureLagBypassesWhenBrokerAlreadyOpen()
    {
        JournalParityPendingLedger.Clear();
        QuantExecutionControlStore.Clear();
        var t0 = DateTimeOffset.Parse("2099-07-07T12:00:00Z");
        var root = Path.Combine(Path.GetTempPath(), "market_reentry_no_exposure_lag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            const string inst = "QM_REENTRY_EXPOSURE_LAG";
            journal.RecordSubmission("intent-reentry", "2099-07-07", "S1", inst, "ENTRY", "broker-entry", t0);
            journal.RecordEntryFill("intent-reentry", "2099-07-07", "S1", 100m, 2, t0, 1m, "Short", inst, inst);

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = -2 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = t0
            };
            var req = new ExecutionSafetyEvaluationRequest
            {
                Instrument = inst,
                CanonicalInstrument = inst,
                UtcNow = t0,
                Journal = journal,
                AccountSnapshot = snap,
                UseInstrumentExecutionAuthority = false,
                IeaOwnedPlusAdoptedWorking = 0,
                RecoveryExecutionDisallowed = false,
                JournalIntegrityOrReconciliationRepairActive = false
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var denyEntry))
                return "market reentry no exposure lag: expected SUBMIT_ENTRY deny";
            if (denyEntry.Reason != ExecutionStructuralLayer.StructuralBlocker.NoActiveExposuresWithBrokerPosition)
                return "market reentry no exposure lag: wrong SUBMIT_ENTRY deny reason " + denyEntry.Reason;

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_MARKET_REENTRY", false, out var ok))
                return "market reentry no exposure lag: expected allow, got " + ok.Reason;
            if (ok.Detail == null || ok.Detail.IndexOf("no_active_exposures_bypass_market_reentry_with_broker_position", StringComparison.Ordinal) < 0)
                return "market reentry no exposure lag: missing bypass detail, got " + ok.Detail;

            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                /* best effort */
            }
        }
    }
}
