// CRITICAL: Define NINJATRADER for NinjaTrader's compiler
// NinjaTrader compiles to tmp folder and may not respect .csproj DefineConstants
#define NINJATRADER

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class NinjaTraderSimAdapter
{
    /// <summary>
    /// STEP 1: Verify we're connected to a NT non-live account (Simulation/Playback — fail closed if live).
    /// REQUIRES: NINJATRADER preprocessor directive and NT context to be set.
    /// </summary>
    private void VerifySimAccount()
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif

        if (!_ntContextSet)
        {
            var error = "NT context is not ready yet. " +
                       "SetNTContext() must be called by RobotSimStrategy before execution can be verified.";
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_CONTEXT_NOT_READY", state: "ENGINE",
                new { reason = "NT_CONTEXT_NOT_SET", note = error }));
            throw new InvalidOperationException(error);
        }

        VerifySimAccountReal();
    }

    private int GetIeaOwnedPlusAdoptedWorkingForParity()
    {
        if (!_useInstrumentExecutionAuthority || _iea == null) return 0;
        return _iea.GetMismatchTrustedWorkingCount();
    }

    private string ResolveCanonicalInstrumentForExecutionSafety(string instrument, string? intentId)
    {
        var trimmed = instrument?.Trim() ?? "";
        if (!string.IsNullOrEmpty(intentId) && IntentPolicy.TryGetValue(intentId, out var pol) &&
            !string.IsNullOrWhiteSpace(pol.CanonicalInstrument))
            return pol.CanonicalInstrument.Trim();
        var nk = BrokerPositionResolver.NormalizeCanonicalKey(trimmed);
        return string.IsNullOrEmpty(nk) ? trimmed : nk;
    }

    private readonly struct ExecutionPreflightAuthoritySample
    {
        public ExecutionPreflightAuthoritySample(
            bool globalKillSwitchActive,
            bool mismatchExecutionBlocked,
            bool? mismatchExecutionBlockedForSubmit,
            bool instrumentFrozenOrEpaBlocked)
        {
            GlobalKillSwitchActive = globalKillSwitchActive;
            MismatchExecutionBlocked = mismatchExecutionBlocked;
            MismatchExecutionBlockedForSubmit = mismatchExecutionBlockedForSubmit;
            InstrumentFrozenOrEpaBlocked = instrumentFrozenOrEpaBlocked;
        }

        public bool GlobalKillSwitchActive { get; }
        public bool MismatchExecutionBlocked { get; }
        public bool? MismatchExecutionBlockedForSubmit { get; }
        public bool InstrumentFrozenOrEpaBlocked { get; }
    }

    private ExecutionPreflightAuthoritySample SampleExecutionPreflightAuthority(string instrument, string submitPath)
    {
        var inst = instrument?.Trim() ?? "";
        var globalKillSwitchActive = _isGlobalKillSwitchActive?.Invoke() == true;
        var mismatchExecutionBlocked = !string.IsNullOrEmpty(inst) &&
                                       _isMismatchExecutionBlocked?.Invoke(inst) == true;
        bool? mismatchExecutionBlockedForSubmit = null;
        if (!string.IsNullOrEmpty(inst) && _isMismatchExecutionBlockedForSubmit != null)
            mismatchExecutionBlockedForSubmit = _isMismatchExecutionBlockedForSubmit(inst, submitPath);
        var instrumentFrozenOrEpaBlocked = !string.IsNullOrEmpty(inst) &&
                                           _isInstrumentFrozenOrEpaBlocked?.Invoke(inst, submitPath) == true;
        return new ExecutionPreflightAuthoritySample(
            globalKillSwitchActive,
            mismatchExecutionBlocked,
            mismatchExecutionBlockedForSubmit,
            instrumentFrozenOrEpaBlocked);
    }

    private static int CountBrokerWorkingOrdersForAuthorityFrame(AccountSnapshot? snap, string instrument)
    {
        if (snap?.WorkingOrders == null || string.IsNullOrWhiteSpace(instrument)) return 0;
        var inst = instrument.Trim();
        var count = 0;
        foreach (var w in snap.WorkingOrders)
        {
            if (!string.IsNullOrWhiteSpace(w.Instrument) &&
                string.Equals(w.Instrument.Trim(), inst, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    private ExecutionAuthorityFrame BuildExecutionAuthorityFrame(
        string instrument,
        string? intentId,
        string? submitPath,
        DateTimeOffset utcNow,
        AccountSnapshot? snap,
        InstrumentOwnershipSnapshot? ledgerSnap,
        bool repairActive,
        bool recoveryExecutionDisallowed,
        int ieaOwnedPlusAdoptedWorking,
        string? snapshotError,
        ExecutionPreflightAuthoritySample? preflightAuthority)
    {
        var trimmed = instrument?.Trim() ?? "";
        var canonical = ResolveCanonicalInstrumentForExecutionSafety(trimmed, intentId);
        var positionAuthority = PositionAuthorityInstrumentEvaluator.BuildEvaluatedArgs(
            snap,
            _executionJournal,
            trimmed,
            canonical);
        var (journalOpenQty, journalOpenHash) =
            _executionJournal.GetOpenJournalStructuralStateForInstrument(trimmed, canonical);

        return new ExecutionAuthorityFrame
        {
            FrameId = ExecutionAuthorityFrame.CreateFrameId(utcNow),
            Source = "adapter_execution_safety_request",
            Instrument = trimmed,
            CanonicalInstrument = canonical,
            ExecutionInstrumentKey = _iea?.ExecutionInstrumentKey,
            IntentId = intentId ?? "",
            SubmitPath = submitPath ?? "",
            DecisionUtc = utcNow,
            FrameCreatedUtc = DateTimeOffset.UtcNow,
            BrokerSnapshotCapturedUtc = snap?.CapturedAtUtc,
            SnapshotError = snapshotError,
            BrokerPositionQty = positionAuthority.BrokerQty,
            BrokerWorkingOrderCount = CountBrokerWorkingOrdersForAuthorityFrame(snap, trimmed),
            JournalOpenQty = journalOpenQty,
            JournalOpenIntentSetHash = journalOpenHash,
            RealOpenQty = positionAuthority.RealOpenQty,
            RecoveryOpenQty = positionAuthority.RecoveryOpenQty,
            AuthorityState = positionAuthority.AuthorityState,
            UseInstrumentExecutionAuthority = _useInstrumentExecutionAuthority,
            IeaOwnedPlusAdoptedWorking = ieaOwnedPlusAdoptedWorking,
            RecoveryExecutionDisallowed = recoveryExecutionDisallowed,
            JournalIntegrityOrReconciliationRepairActive = repairActive,
            PreflightAuthoritySampled = preflightAuthority.HasValue,
            PreflightGlobalKillSwitchActive = preflightAuthority?.GlobalKillSwitchActive ?? false,
            PreflightMismatchExecutionBlocked = preflightAuthority?.MismatchExecutionBlocked ?? false,
            PreflightMismatchExecutionBlockedForSubmit = preflightAuthority?.MismatchExecutionBlockedForSubmit,
            PreflightInstrumentFrozenOrEpaBlocked = preflightAuthority?.InstrumentFrozenOrEpaBlocked ?? false,
            LedgerAccountName = GetLedgerAccountName(),
            LedgerOwnershipVersion = ledgerSnap?.OwnershipVersion,
            LedgerSignedNetQty = ledgerSnap?.LedgerSignedNetQty,
            LedgerActiveSlotCount = ledgerSnap?.ActiveSlotCount,
            LedgerOrphanSlotCount = ledgerSnap?.OrphanSlotCount,
            OwnershipSnapshot = ledgerSnap
        };
    }

    private void EmitAuthorityFrameEvaluated(ExecutionAuthorityFrame frame, DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, frame.IntentId, frame.Instrument, "AUTHORITY_FRAME_EVALUATED",
            new
            {
                authority_frame_id = frame.FrameId,
                source = frame.Source,
                instrument = frame.Instrument,
                canonical_instrument = frame.CanonicalInstrument,
                execution_instrument_key = frame.ExecutionInstrumentKey,
                intent_id = frame.IntentId,
                submit_path = frame.SubmitPath,
                broker_snapshot_captured_at = frame.BrokerSnapshotCapturedUtc,
                snapshot_error = frame.SnapshotError,
                broker_qty = frame.BrokerPositionQty,
                broker_working_count = frame.BrokerWorkingOrderCount,
                journal_open_qty = frame.JournalOpenQty,
                journal_open_intent_hash = frame.JournalOpenIntentSetHash,
                real_open_qty = frame.RealOpenQty,
                recovery_open_qty = frame.RecoveryOpenQty,
                authority_state = frame.AuthorityState,
                use_iea = frame.UseInstrumentExecutionAuthority,
                iea_owned_plus_adopted_working = frame.IeaOwnedPlusAdoptedWorking,
                recovery_execution_disallowed = frame.RecoveryExecutionDisallowed,
                repair_active = frame.JournalIntegrityOrReconciliationRepairActive,
                preflight_authority_sampled = frame.PreflightAuthoritySampled,
                preflight_global_kill_switch_active = frame.PreflightGlobalKillSwitchActive,
                preflight_mismatch_execution_blocked = frame.PreflightMismatchExecutionBlocked,
                preflight_mismatch_execution_blocked_for_submit = frame.PreflightMismatchExecutionBlockedForSubmit,
                preflight_instrument_frozen_or_epa_blocked = frame.PreflightInstrumentFrozenOrEpaBlocked,
                ledger_account = frame.LedgerAccountName,
                ledger_version = frame.LedgerOwnershipVersion,
                ledger_signed_net_qty = frame.LedgerSignedNetQty,
                ledger_active_slot_count = frame.LedgerActiveSlotCount,
                ledger_orphan_slot_count = frame.LedgerOrphanSlotCount
            }));
    }

    private ExecutionSafetyEvaluationRequest BuildExecutionSafetyEvaluationRequest(
        string instrument,
        string? intentId,
        DateTimeOffset utcNow,
        string? submitPath = null,
        ExecutionPreflightAuthoritySample? preflightAuthority = null)
    {
        var trimmed = instrument?.Trim() ?? "";
        AccountSnapshot? snap = null;
        string? snapshotError = null;
        try
        {
            snap = _executionSafetyTestGetAccountSnapshot != null
                ? _executionSafetyTestGetAccountSnapshot(utcNow)
                : GetAccountSnapshot(utcNow);
        }
        catch (Exception ex)
        {
            snapshotError = ex.GetType().Name;
            snap = null;
        }

        var repair = _journalIntegrityRepairActiveForInstrumentCallback?.Invoke(trimmed) == true;
        var ieaOwnedPlusAdoptedWorking = GetIeaOwnedPlusAdoptedWorkingForParity();
        var recoveryExecutionDisallowed = _isRecoveryExecutionAllowedCallback != null && !_isRecoveryExecutionAllowedCallback();

        InstrumentOwnershipSnapshot? ledgerSnap = null;
        if (FeatureFlags.StructuralLayerUseLedgerOwnership && _ownershipLedger != null)
        {
            try { ledgerSnap = _ownershipLedger.GetOwnershipSnapshot(GetLedgerAccountName(), trimmed); }
            catch { }
        }

        var frame = BuildExecutionAuthorityFrame(
            trimmed,
            intentId,
            submitPath,
            utcNow,
            snap,
            ledgerSnap,
            repair,
            recoveryExecutionDisallowed,
            ieaOwnedPlusAdoptedWorking,
            snapshotError,
            preflightAuthority);
        if (!string.IsNullOrWhiteSpace(submitPath))
            EmitAuthorityFrameEvaluated(frame, utcNow);

        return new ExecutionSafetyEvaluationRequest
        {
            Instrument = trimmed,
            CanonicalInstrument = ResolveCanonicalInstrumentForExecutionSafety(trimmed, intentId),
            ExecutionInstrumentKey = _iea?.ExecutionInstrumentKey,
            AccountSnapshot = snap,
            SnapshotTakenUtc = snap?.CapturedAtUtc ?? utcNow,
            UtcNow = utcNow,
            Journal = _executionJournal,
            UseInstrumentExecutionAuthority = _useInstrumentExecutionAuthority,
            IeaOwnedPlusAdoptedWorking = ieaOwnedPlusAdoptedWorking,
            Coordinator = _coordinator,
            RecoveryExecutionDisallowed = recoveryExecutionDisallowed,
            JournalIntegrityOrReconciliationRepairActive = repair,
            AuthorityFrame = frame,
            LedgerOwnershipSnapshot = ledgerSnap
        };
    }

    private void EmitPositionAuthorityEvaluated(ExecutionSafetyEvaluationRequest req, DateTimeOffset utcNow)
    {
        var a = PositionAuthorityInstrumentEvaluator.BuildEvaluatedArgs(
            req.AccountSnapshot,
            req.Journal,
            req.Instrument,
            req.CanonicalInstrument);
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "POSITION_AUTHORITY_EVALUATED", state: "ENGINE",
            new
            {
                authority_frame_id = req.AuthorityFrame?.FrameId,
                instrument = a.Instrument,
                broker_qty = a.BrokerQty,
                real_open_qty = a.RealOpenQty,
                recovery_open_qty = a.RecoveryOpenQty,
                journal_open_qty = a.JournalOpenQty,
                authority_state = a.AuthorityState,
                frame_broker_qty = req.AuthorityFrame?.BrokerPositionQty,
                frame_journal_open_qty = req.AuthorityFrame?.JournalOpenQty,
                frame_iea_working = req.AuthorityFrame?.IeaOwnedPlusAdoptedWorking,
                frame_ledger_signed_net_qty = req.AuthorityFrame?.LedgerSignedNetQty
            }));
    }

    private static string MapGateReasonToCriticalCategory(string gateReason)
    {
        return gateReason switch
        {
            "repair_active" or "recovery_active" => "repair / recovery active",
            "parity_not_ok" => "parity not ok",
            "no_active_exposures_with_broker_position" or "no_exposure_broker_position" => "no exposure + broker position",
            "unsafe_locked_kill_switch" => "unmapped fill",
            "mismatch" or "insufficient_data" or "unmapped_or_external_orders" or "working_order_mismatch" or "authority_not_real_dominant" =>
                "mismatch",
            "authority_unknown" or "authority_recovery" or "authority_not_real" or "position_authority_not_real" => "position authority",
            _ => string.IsNullOrEmpty(gateReason) ? "mismatch" : gateReason
        };
    }

    private void TryEmitCriticalUnsafeStateDetected(string instrument, string gateReason, DateTimeOffset utcNow)
    {
        var k = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(k)) return;
        var cat = MapGateReasonToCriticalCategory(gateReason);
        if (_criticalUnsafeStateEmittedOnce.TryGetValue(k, out var prev) && string.Equals(prev, cat, StringComparison.OrdinalIgnoreCase))
            return;
        _criticalUnsafeStateEmittedOnce[k] = cat;
        _log.Write(RobotEvents.ExecutionBase(utcNow, "", k, "CRITICAL_UNSAFE_STATE_DETECTED",
            new { instrument = k, reason = cat }));
    }

    private void EmitExecutionBlockedUnsafeState(string blockedWhat, string? intentId, ExecutionSafetySnapshot snap, DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", snap.Instrument, "EXECUTION_BLOCKED_UNSAFE_STATE",
            new
            {
                authority_frame_id = snap.AuthorityFrameId,
                instrument = snap.Instrument,
                broker_qty = snap.BrokerQty,
                journal_qty = snap.JournalQty,
                journal_open_qty = snap.JournalOpenQty != 0 ? snap.JournalOpenQty : snap.RealOpenQty + snap.RecoveredOpenQty,
                real_open_qty = snap.RealOpenQty,
                recovered_open_qty = snap.RecoveredOpenQty,
                parity_status = snap.ParityStatus,
                structural_repair_active = snap.StructuralRepairActive,
                no_active_exposures_with_broker_position = snap.NoActiveExposuresWithBrokerPosition,
                authority_state = snap.AuthorityState,
                reason = snap.Reason,
                detail = snap.Detail,
                blocked_what = blockedWhat,
                instrument_state = snap.InstrumentOperationalState
            }));
    }

    private void EmitExecutionBlockedPositionAuthority(string blockedWhat, string? intentId, ExecutionSafetySnapshot snap, DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", snap.Instrument, "EXECUTION_BLOCKED_POSITION_AUTHORITY",
            new
            {
                authority_frame_id = snap.AuthorityFrameId,
                instrument = snap.Instrument,
                broker_qty = snap.BrokerQty,
                real_open_qty = snap.RealOpenQty,
                recovery_open_qty = snap.RecoveredOpenQty,
                journal_open_qty = snap.JournalOpenQty != 0 ? snap.JournalOpenQty : snap.RealOpenQty + snap.RecoveredOpenQty,
                authority_state = snap.AuthorityState,
                reason = snap.Reason,
                blocked_what = blockedWhat
            }));
    }

    private void EmitExecutionBlockedOverlay(
        string blockedWhat,
        string? intentId,
        string instrument,
        ExecutionOverlayBlockReason blockReason,
        string? detail,
        DateTimeOffset utcNow,
        string? correlationId = null,
        string? authorityFrameId = null)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", instrument, "EXECUTION_BLOCKED_OVERLAY",
            new
            {
                authority_frame_id = authorityFrameId,
                instrument,
                block_reason = blockReason.ToString(),
                detail,
                blocked_what = blockedWhat,
                correlation_id = correlationId
            }));
    }

    private static bool IsAuthorityLayerStructuralDenial(string? reason) =>
        reason is "authority_unknown" or "authority_recovery" or "authority_not_real";

    private void EmitExecutionBlockedEpa(string blockedWhat, string? intentId, string instrument, string denyReason, DateTimeOffset utcNow)
    {
        var cause = denyReason switch
        {
            "GLOBAL_KILL_SWITCH_ACTIVE" => "operator_or_config_kill_switch_enabled",
            "MISMATCH_EXECUTION_BLOCK" => "mismatch_execution_block_authority",
            "INSTRUMENT_FROZEN_OR_EPA_BLOCKED" => "instrument_frozen_or_supervisory_block",
            _ => denyReason
        };
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", instrument, "EXECUTION_BLOCKED_EPA",
            new
            {
                blocked_what = blockedWhat,
                deny_reason = denyReason,
                transition_cause = cause,
                transition_decision = "DENY_ORDER_SUBMIT",
                transition_effect = "NO_BROKER_SUBMIT"
            }));

        if (_keyEventWriter != null && _keyEventWriter.TryShouldEmitEpaBlock(instrument, denyReason, utcNow))
        {
            var reasonBucket = denyReason switch
            {
                "GLOBAL_KILL_SWITCH_ACTIVE" => "KILL",
                "MISMATCH_EXECUTION_BLOCK" => "MISMATCH",
                "INSTRUMENT_FROZEN_OR_EPA_BLOCKED" => "FROZEN",
                _ => denyReason
            };
            var stream = "";
            if (!string.IsNullOrEmpty(intentId))
            {
                var (_, s, _, _, _, _, _) = GetIntentInfo(intentId);
                stream = s;
            }

            _keyEventWriter.AppendKeyEvent(utcNow, "EXECUTION_BLOCKED", instrument?.Trim(),
                string.IsNullOrEmpty(stream) ? null : stream, reasonBucket,
                new Dictionary<string, object?> { ["epa_deny"] = denyReason, ["blocked_what"] = blockedWhat });
        }
    }

    private bool TryExecutionSafetyGateForOrderSubmit(string intentId, string instrument, string blockedWhat, DateTimeOffset utcNow,
        out OrderSubmissionResult? failure)
    {
        failure = null;
#if !NINJATRADER
        return true;
#else
        var preflightAuthority = SampleExecutionPreflightAuthority(instrument, blockedWhat);
        if (FeatureFlags.UnifiedExecutionAuthorityEnabled && _unifiedAuthority != null)
        {
            var ueaReq = BuildUeaRequest(intentId, instrument, blockedWhat, utcNow, preflightAuthority: preflightAuthority);
            var ueaDecision = _unifiedAuthority.Evaluate(ueaReq);
            if (!ueaDecision.Allowed)
            {
                failure = OrderSubmissionResult.FailureResult(
                    $"UEA_DENIED:{ueaDecision.DenyGate}:{ueaDecision.DenyReason}", utcNow);
                _log?.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument?.Trim() ?? "", "UEA_ACTIVE_DENY",
                    new
                    {
                        authority_frame_id = ueaDecision.AuthorityFrame?.FrameId,
                        gate = ueaDecision.DenyGate,
                        reason = ueaDecision.DenyReason,
                        blocked_what = blockedWhat
                    }));
                return false;
            }
            return true;
        }

        var protectiveStructuralFirst = string.Equals(blockedWhat, "SUBMIT_PROTECTIVE_STOP", StringComparison.Ordinal);
        Func<string, string?, bool>? sampledMismatchForSubmit = preflightAuthority.MismatchExecutionBlockedForSubmit.HasValue
            ? (_, _) => preflightAuthority.MismatchExecutionBlockedForSubmit.Value
            : null;
        if (!ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(
                () => preflightAuthority.GlobalKillSwitchActive,
                _ => preflightAuthority.MismatchExecutionBlocked,
                sampledMismatchForSubmit,
                (_, _) => preflightAuthority.InstrumentFrozenOrEpaBlocked,
                instrument,
                blockedWhat,
                out var epaDeny,
                skipMismatchExecutionBlock: protectiveStructuralFirst))
        {
            var inst = instrument?.Trim() ?? "";
            EmitExecutionBlockedEpa(blockedWhat, intentId, inst, epaDeny, utcNow);
            failure = OrderSubmissionResult.FailureResult($"EXECUTION_BLOCKED_EPA:{epaDeny}", utcNow);
            RunUeaShadowEval(intentId, instrument, blockedWhat, utcNow, oldAllowed: false, oldDenyReason: $"EPA:{epaDeny}", preflightAuthority: preflightAuthority);
            return false;
        }

        ExecutionSafetyEvaluationRequest req;
        try
        {
            req = BuildExecutionSafetyEvaluationRequest(instrument, intentId, utcNow, blockedWhat, preflightAuthority);
        }
        catch
        {
            TryEmitCriticalUnsafeStateDetected(instrument, "mismatch", utcNow);
            var bad = new ExecutionSafetySnapshot
            {
                Instrument = instrument?.Trim() ?? "",
                Reason = "account_snapshot_failed",
                InstrumentOperationalState = "ACCOUNT_SNAPSHOT_UNAVAILABLE"
            };
            EmitExecutionBlockedUnsafeState(blockedWhat, intentId, bad, utcNow);
            failure = OrderSubmissionResult.FailureResult("EXECUTION_BLOCKED_UNSAFE_STATE:account_snapshot_failed", utcNow);
            RunUeaShadowEval(intentId, instrument, blockedWhat, utcNow, oldAllowed: false, oldDenyReason: "account_snapshot_failed", preflightAuthority: preflightAuthority);
            return false;
        }

        EmitPositionAuthorityEvaluated(req, utcNow);

        if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, blockedWhat, flattenAuthorityBypass: false, out var structSnap))
        {
            if (IsAuthorityLayerStructuralDenial(structSnap.Reason))
            {
                EmitExecutionBlockedPositionAuthority(blockedWhat, intentId, structSnap, utcNow);
                failure = OrderSubmissionResult.FailureResult($"EXECUTION_BLOCKED_POSITION_AUTHORITY:{structSnap.AuthorityState}", utcNow);
                RunUeaShadowEval(intentId, instrument, blockedWhat, utcNow, oldAllowed: false, oldDenyReason: $"POSITION_AUTHORITY:{structSnap.AuthorityState}", prebuiltSafetyRequest: req);
                return false;
            }

            if (!string.Equals(structSnap.Reason, ExecutionStructuralLayer.StructuralBlocker.RepairActive, StringComparison.Ordinal))
                TryEmitCriticalUnsafeStateDetected(instrument, structSnap.Reason ?? "", utcNow);
            EmitExecutionBlockedUnsafeState(blockedWhat, intentId, structSnap, utcNow);
            failure = OrderSubmissionResult.FailureResult($"EXECUTION_BLOCKED_UNSAFE_STATE:{structSnap.Reason}", utcNow);
            RunUeaShadowEval(intentId, instrument, blockedWhat, utcNow, oldAllowed: false, oldDenyReason: $"UNSAFE_STATE:{structSnap.Reason}", prebuiltSafetyRequest: req);
            return false;
        }

        if (!ExecutionSafetyGate.TryEvaluateExecutionOverlay(req, out var overlay) || overlay.IsBlocked)
        {
            EmitExecutionBlockedOverlay(blockedWhat, intentId, structSnap.Instrument, overlay.BlockReason, overlay.Detail, utcNow,
                authorityFrameId: req.AuthorityFrame?.FrameId);
            failure = OrderSubmissionResult.FailureResult($"EXECUTION_BLOCKED_OVERLAY:{overlay.BlockReason}", utcNow);
            RunUeaShadowEval(intentId, instrument, blockedWhat, utcNow, oldAllowed: false, oldDenyReason: $"OVERLAY:{overlay.BlockReason}", prebuiltSafetyRequest: req);
            return false;
        }

        RunUeaShadowEval(intentId, instrument, blockedWhat, utcNow, oldAllowed: true, oldDenyReason: null, prebuiltSafetyRequest: req);
        return true;
#endif
    }

    private AuthorityEvaluationRequest BuildUeaRequest(string intentId, string instrument, string blockedWhat, DateTimeOffset utcNow,
        ExecutionSafetyEvaluationRequest? prebuiltSafetyRequest = null,
        ExecutionPreflightAuthoritySample? preflightAuthority = null)
    {
        var frame = prebuiltSafetyRequest?.AuthorityFrame;
        var hasFramePreflight = frame?.PreflightAuthoritySampled == true;
        var submitIntent = blockedWhat switch
        {
            "SUBMIT_PROTECTIVE_STOP" or "SUBMIT_TARGET" => SubmitIntent.RiskCoverage,
            "SUBMIT_ENTRY" or "SUBMIT_ENTRY_STOP" or "SUBMIT_MARKET_REENTRY" => SubmitIntent.OpeningEntry,
            _ => SubmitIntent.RiskIncreasing
        };

        return new AuthorityEvaluationRequest
        {
            Instrument = instrument?.Trim() ?? "",
            IntentId = intentId,
            SubmitIntent = submitIntent,
            SubmitPath = blockedWhat,
            UtcNow = utcNow,
            GlobalKillSwitchActive = _isGlobalKillSwitchActive,
            MismatchExecutionBlocked = _isMismatchExecutionBlocked,
            MismatchExecutionBlockedForSubmit = _isMismatchExecutionBlockedForSubmit,
            InstrumentFrozenOrEpaBlocked = _isInstrumentFrozenOrEpaBlocked,
            PreflightGlobalKillSwitchActive = preflightAuthority?.GlobalKillSwitchActive ??
                                              (hasFramePreflight ? frame!.PreflightGlobalKillSwitchActive : (bool?)null),
            PreflightMismatchExecutionBlocked = preflightAuthority?.MismatchExecutionBlocked ??
                                                (hasFramePreflight ? frame!.PreflightMismatchExecutionBlocked : (bool?)null),
            PreflightMismatchExecutionBlockedForSubmit = preflightAuthority?.MismatchExecutionBlockedForSubmit ??
                                                         (hasFramePreflight ? frame!.PreflightMismatchExecutionBlockedForSubmit : null),
            PreflightInstrumentFrozenOrEpaBlocked = preflightAuthority?.InstrumentFrozenOrEpaBlocked ??
                                                    (hasFramePreflight ? frame!.PreflightInstrumentFrozenOrEpaBlocked : (bool?)null),
            PrebuiltSafetyRequest = prebuiltSafetyRequest,
            AuthorityFrame = prebuiltSafetyRequest?.AuthorityFrame,
            BuildSafetyRequest = (inst, iid, t) => BuildExecutionSafetyEvaluationRequest(inst, iid, t, blockedWhat, preflightAuthority),
            OwnershipLedger = _ownershipLedger,
            AccountName = GetLedgerAccountName()
        };
    }

    private void RunUeaShadowEval(string intentId, string instrument, string blockedWhat, DateTimeOffset utcNow,
        bool oldAllowed, string? oldDenyReason, ExecutionSafetyEvaluationRequest? prebuiltSafetyRequest = null,
        ExecutionPreflightAuthoritySample? preflightAuthority = null)
    {
        if (!FeatureFlags.UnifiedExecutionAuthorityShadowEnabled || _unifiedAuthority == null) return;

        try
        {
            var ueaReq = BuildUeaRequest(intentId, instrument, blockedWhat, utcNow, prebuiltSafetyRequest, preflightAuthority);
            var ueaDecision = _unifiedAuthority.Evaluate(ueaReq);

            _log?.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument?.Trim() ?? "", "UEA_SHADOW_EVALUATED",
                new
                {
                    authority_frame_id = ueaDecision.AuthorityFrame?.FrameId,
                    old_allowed = oldAllowed,
                    old_deny_reason = oldDenyReason,
                    uea_allowed = ueaDecision.Allowed,
                    uea_deny_gate = ueaDecision.DenyGate,
                    uea_deny_reason = ueaDecision.DenyReason,
                    blocked_what = blockedWhat,
                    account_name = ueaReq.AccountName
                }));

            if (oldAllowed != ueaDecision.Allowed)
            {
                _log?.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument?.Trim() ?? "", "UEA_SHADOW_DISAGREEMENT",
                    new
                    {
                        authority_frame_id = ueaDecision.AuthorityFrame?.FrameId,
                        old_allowed = oldAllowed,
                        old_deny_reason = oldDenyReason,
                        uea_allowed = ueaDecision.Allowed,
                        uea_deny_gate = ueaDecision.DenyGate,
                        uea_deny_reason = ueaDecision.DenyReason,
                        blocked_what = blockedWhat,
                        note = "Old path and UEA disagree on order submission decision"
                    }));
            }
        }
        catch (Exception ex)
        {
            _log?.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument?.Trim() ?? "", "UEA_SHADOW_ERROR",
                new { error = ex.Message, blocked_what = blockedWhat }));
        }
    }

    /// <summary>
    /// Integration tests: same implementation as the private order-submit safety gate (EPA preflight + structural + overlay).
    /// Use <see cref="SetExecutionSafetyTestAccountSnapshotFactory"/> when NT context is not set.
    /// </summary>
    public bool TryExecutionSafetyGateForOrderSubmitIntegration(
        string intentId,
        string instrument,
        string blockedWhat,
        DateTimeOffset utcNow,
        out OrderSubmissionResult? failure) =>
        TryExecutionSafetyGateForOrderSubmit(intentId, instrument, blockedWhat, utcNow, out failure);

    /// <summary>
    /// Integration tests: structural layer only using the same <see cref="BuildExecutionSafetyEvaluationRequest"/> as the gate
    /// (for asserting <see cref="ExecutionSafetySnapshot"/> fields without duplicating request wiring).
    /// </summary>
    public bool TryExecutionSafetyStructuralEvaluationFromAdapterRequest(
        string intentId,
        string instrument,
        string blockedWhat,
        DateTimeOffset utcNow,
        out ExecutionSafetySnapshot snapshot)
    {
        snapshot = new ExecutionSafetySnapshot();
#if !NINJATRADER
        return true;
#else
        try
        {
            var req = BuildExecutionSafetyEvaluationRequest(instrument, intentId, utcNow);
            return ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, blockedWhat, false, out snapshot);
        }
        catch
        {
            return false;
        }
#endif
    }

    /// <summary>Unmapped fill / ghost path: hard kill-switch + critical alert (called from NT execution ingress).</summary>
    public void ApplyUnmappedExecutionKillSwitchAndAlert(string instrument, string unmappedReason, DateTimeOffset utcNow)
    {
        ExecutionSafetyGate.ApplyUnmappedExecutionKillSwitch(instrument, unmappedReason, utcNow);
        TryEmitCriticalUnsafeStateDetected(instrument, "unsafe_locked_kill_switch", utcNow);
    }

    /// <summary>
    /// REAL authority + structural order-submit path (parity, repair, exposure). Does not evaluate overlay; unsafe lock may still be set (manual unlock).
    /// Shared by <see cref="TryValidateExplicitUnfreezeConditions"/> and <see cref="TryManualClearExecutionSafetyUnsafeLock"/>.
    /// </summary>
    private bool TryValidateStructuralExecutionSafetyBaseline(ExecutionSafetyEvaluationRequest req, out string denyReason,
        out ExecutionSafetySnapshot structuralSnapshot)
    {
        structuralSnapshot = new ExecutionSafetySnapshot();
        denyReason = "";
        var inst = req.Instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst))
        {
            denyReason = "missing_instrument";
            return false;
        }

        var canonical = ResolveCanonicalInstrumentForExecutionSafety(inst, null);
        var auth = PositionAuthorityInstrumentEvaluator.Derive(req.AccountSnapshot, req.Journal, inst, canonical);
        if (auth != DerivedPositionAuthority.REAL)
        {
            denyReason = "authority_not_real";
            return false;
        }

        if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", flattenAuthorityBypass: false,
                out structuralSnapshot))
        {
            denyReason = string.IsNullOrEmpty(structuralSnapshot.Reason) ? "structural_block" : structuralSnapshot.Reason;
            if (!string.IsNullOrEmpty(structuralSnapshot.Detail))
                denyReason = denyReason + ":" + structuralSnapshot.Detail;
            return false;
        }

        if (!string.IsNullOrEmpty(structuralSnapshot.Reason))
        {
            denyReason = structuralSnapshot.Reason;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Manual operator reset for <see cref="ExecutionSafetyGate.InstrumentStateUnsafeLocked"/>.
    /// Requires the same structural baseline as explicit unfreeze (REAL + structural layer), hard-flatten lock clear, then fresh snapshot per <see cref="ExecutionSafetyGate.TryManualClearUnsafeLockWhenOverlayAllows"/>.
    /// </summary>
    public bool TryManualClearExecutionSafetyUnsafeLock(string instrument, DateTimeOffset utcNow, out string denyReason)
    {
        denyReason = "";
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst))
        {
            denyReason = "missing_instrument";
            return false;
        }

        ExecutionSafetyEvaluationRequest req;
        try
        {
            req = BuildExecutionSafetyEvaluationRequest(inst, null, utcNow);
        }
        catch
        {
            denyReason = "account_snapshot_failed";
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "EXECUTION_SAFETY_MANUAL_UNLOCK_DENIED",
                new { instrument = inst, deny_reason = denyReason }));
            return false;
        }

        if (!TryValidateStructuralExecutionSafetyBaseline(req, out var structuralDeny, out _))
        {
            denyReason = structuralDeny;
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "EXECUTION_SAFETY_MANUAL_UNLOCK_DENIED",
                new { instrument = inst, deny_reason = denyReason, gate = "structural_baseline" }));
            return false;
        }

        if (HardFailClosedExecutionModel.IsHardExecutionLocked(inst))
        {
            denyReason = "hard_execution_lock";
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "EXECUTION_SAFETY_MANUAL_UNLOCK_DENIED",
                new { instrument = inst, deny_reason = denyReason, gate = "hard_flatten_model" }));
            return false;
        }

        if (!ExecutionSafetyGate.TryManualClearUnsafeLockWhenOverlayAllows(req, out var unlockDenyReason))
        {
            denyReason = unlockDenyReason;
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "EXECUTION_SAFETY_MANUAL_UNLOCK_DENIED",
                new { instrument = inst, deny_reason = unlockDenyReason }));
            return false;
        }

        _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "EXECUTION_SAFETY_MANUAL_UNLOCK", new { instrument = inst }));
        return true;
    }

    /// <summary>Same as overload with <c>out denyReason</c>; discards denial reason.</summary>
    public bool TryManualClearExecutionSafetyUnsafeLock(string instrument, DateTimeOffset utcNow) =>
        TryManualClearExecutionSafetyUnsafeLock(instrument, utcNow, out _);

    /// <summary>
    /// Validates explicit unfreeze preconditions for <see cref="RobotEngine.TryUnfreezeInstrument"/>:
    /// position authority REAL, structural layer clear (non-directional submit path), overlay clear, fresh snapshot via overlay rules.
    /// Does not clear engine freeze or risk latch.
    /// </summary>
    public bool TryValidateExplicitUnfreezeConditions(string instrument, DateTimeOffset utcNow, out string denyReason)
    {
        denyReason = "";
#if !NINJATRADER
        return true;
#else
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst))
        {
            denyReason = "missing_instrument";
            return false;
        }

        ExecutionSafetyEvaluationRequest req;
        try
        {
            req = BuildExecutionSafetyEvaluationRequest(inst, null, utcNow);
        }
        catch
        {
            denyReason = "account_snapshot_failed";
            return false;
        }

        if (!TryValidateStructuralExecutionSafetyBaseline(req, out denyReason, out _))
            return false;

        if (!ExecutionSafetyGate.TryEvaluateExecutionOverlay(req, out var ov) || ov.IsBlocked)
        {
            denyReason = "overlay:" + ov.BlockReason;
            if (!string.IsNullOrEmpty(ov.Detail))
                denyReason = denyReason + ":" + ov.Detail;
            return false;
        }

        return true;
#endif
    }

    /// <summary>Shared flatten guard (enqueue + execute + emergency). Fail-closed.</summary>
    public bool TryExecutionSafetyFlattenGuard(
        string instrument,
        string? intentId,
        DateTimeOffset utcNow,
        string blockedWhat,
        string? correlationId,
        out ExecutionSafetySnapshot snap)
    {
        snap = new ExecutionSafetySnapshot();
#if !NINJATRADER
        return true;
#else
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return true;
        ExecutionSafetyEvaluationRequest req;
        try
        {
            req = BuildExecutionSafetyEvaluationRequest(inst, intentId, utcNow);
        }
        catch
        {
            snap = new ExecutionSafetySnapshot { Instrument = inst, Reason = "account_snapshot_failed" };
            EmitExecutionBlockedUnsafeState(blockedWhat, intentId, snap, utcNow);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", inst, "FLATTEN_BLOCKED_UNSAFE_STATE",
                new { instrument = inst, reason = "account_snapshot_failed", correlation_id = correlationId }));
            return false;
        }

        EmitPositionAuthorityEvaluated(req, utcNow);

        if (!ExecutionStructuralLayer.TryEvaluateFlattenStructure(req, out snap, out var flatReason))
        {
            var reason = string.IsNullOrEmpty(flatReason) ? snap.Reason : flatReason;
            if (string.Equals(reason, "broker_flat", StringComparison.OrdinalIgnoreCase))
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", inst, "FLATTEN_SKIPPED_BROKER_FLAT",
                    new
                    {
                        instrument = inst,
                        reason,
                        correlation_id = correlationId,
                        broker_qty = snap.BrokerQty,
                        journal_qty = snap.JournalQty,
                        authority_state = snap.AuthorityState
                    }));
                return false;
            }

            TryEmitCriticalUnsafeStateDetected(inst, snap.Reason ?? flatReason, utcNow);
            EmitExecutionBlockedUnsafeState(blockedWhat, intentId, snap, utcNow);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", inst, "FLATTEN_BLOCKED_UNSAFE_STATE",
                new
                {
                    instrument = inst,
                    reason,
                    correlation_id = correlationId,
                    broker_qty = snap.BrokerQty,
                    journal_qty = snap.JournalQty,
                    authority_state = snap.AuthorityState
                }));
            return false;
        }

        if (!ExecutionSafetyGate.TryEvaluateExecutionOverlay(req, out var overlay) || overlay.IsBlocked)
        {
            EmitExecutionBlockedOverlay(blockedWhat, intentId, inst, overlay.BlockReason, overlay.Detail, utcNow, correlationId);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", inst, "FLATTEN_BLOCKED_OVERLAY",
                new
                {
                    instrument = inst,
                    block_reason = overlay.BlockReason.ToString(),
                    detail = overlay.Detail,
                    correlation_id = correlationId
                }));
            return false;
        }

        return true;
#endif
    }

    private bool TryExecutionSafetyGateFlattenEnqueue(NtFlattenInstrumentCommand cmd, DateTimeOffset utcNow) =>
        TryExecutionSafetyFlattenGuard(cmd.Instrument ?? "", cmd.IntentId, utcNow, "FLATTEN_ENQUEUE", cmd.CorrelationId,
            out _);

    /// <summary>
    /// STEP 2: Implement Entry Order Submission (REAL NT API)
    /// </summary>
    private OrderSubmissionResult SubmitEntryOrderCore(
        string intentId,
        string instrument,
        string direction,
        decimal? entryPrice,
        int quantity,
        string? entryOrderType,
        string? ocoGroup,
        DateTimeOffset utcNow,
        string submitPath)
    {
        if (_isPlaybackStallNtCallBlockedCallback != null && _isPlaybackStallNtCallBlockedCallback())
        {
            const string error = "NT_CALL_BLOCKED:PLAYBACK_STALL_QUIESCENCE";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                reason = "PLAYBACK_STALL_QUIESCENCE",
                submit_path = submitPath,
                note = "Playback stall quiescence is armed; blocking NT-touching submit path."
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        if (!TryIntentIdConsistencyGuard(intentId, instrument, "entry", utcNow, out var intentIdFail))
            return intentIdFail!;
        if (!TrySessionIdentityGate(intentId, instrument, "entry", utcNow, null, out var sessionFail))
            return sessionFail!;

        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "ENTRY",
            direction,
            entry_price = entryPrice,
            entry_order_type = entryOrderType,
            quantity,
            account = "SIM"
        }));

        // Hard safety: Verify Sim account (should already be verified, but double-check)
        if (!_simAccountVerified)
        {
            var error = "SIM account not verified - not placing orders";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                account = "SIM"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
        {
            error,
            reason = "NINJATRADER_NOT_DEFINED"
        }));
        return OrderSubmissionResult.FailureResult(error, utcNow);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                reason = "NT_CONTEXT_NOT_SET"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        // Invariant 1: When IEA enabled, IEA must be bound (SetNTContext with engineExecutionInstrument)
        if (_useInstrumentExecutionAuthority && _iea == null)
        {
            var error = "CRITICAL: IEA enabled but not bound - order submission blocked (IEA_BYPASS_ATTEMPTED)";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "IEA_BYPASS_ATTEMPTED", state: "ENGINE",
                new { intent_id = intentId, instrument, reason = "IEA_ENABLED_BUT_NOT_BOUND", error }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        if (!TryExecutionSafetyGateForOrderSubmit(intentId, instrument, submitPath, utcNow, out var safetyFailEntry))
            return safetyFailEntry!;

        try
        {
            return SubmitEntryOrderReal(intentId, instrument, direction, entryPrice, quantity, entryOrderType, ocoGroup, utcNow);
        }
        catch (Exception ex)
        {
            // Journal: ENTRY_SUBMIT_FAILED
            // Get Intent info for journal logging
            string tradingDate = "";
            string stream = "";
            if (IntentMap.TryGetValue(intentId, out var entryIntent))
            {
                tradingDate = entryIntent.TradingDate;
                stream = entryIntent.Stream;
            }
            _executionJournal.RecordRejection(intentId, tradingDate, stream, $"ENTRY_SUBMIT_FAILED: {ex.Message}", utcNow, 
                orderType: "ENTRY", rejectedPrice: entryPrice, rejectedQuantity: quantity);
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error = ex.Message,
                account = "SIM",
                exception_type = ex.GetType().Name
            }));
            
            return OrderSubmissionResult.FailureResult($"Entry order submission failed: {ex.Message}", utcNow);
        }
    }

    public OrderSubmissionResult SubmitEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal? entryPrice,
        int quantity,
        string? entryOrderType,
        string? ocoGroup,
        DateTimeOffset utcNow) =>
        SubmitEntryOrderCore(intentId, instrument, direction, entryPrice, quantity, entryOrderType, ocoGroup, utcNow,
            "SUBMIT_ENTRY");

    public OrderSubmissionResult SubmitMarketReentryOrder(
        string intentId,
        string instrument,
        string direction,
        int quantity,
        DateTimeOffset utcNow) =>
        SubmitEntryOrderCore(intentId, instrument, direction, null, quantity, "MARKET", null, utcNow,
            "SUBMIT_MARKET_REENTRY");

    /// <summary>
    /// STEP 2b: Submit stop-market entry order (breakout stop).
    /// Used to place stop entries immediately after RANGE_LOCKED (before breakout occurs).
    /// </summary>
    public OrderSubmissionResult SubmitStopEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        if (!TryIntentIdConsistencyGuard(intentId, instrument, "entry_stop", utcNow, out var intentIdFailStop))
            return intentIdFailStop!;
        if (!TrySessionIdentityGate(intentId, instrument, "entry_stop", utcNow, null, out var sessionFailStop))
            return sessionFailStop!;

        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "ENTRY_STOP",
            direction,
            stop_price = stopPrice,
            quantity,
            oco_group = ocoGroup,
            account = "SIM"
        }));

        // Hard safety: Verify Sim account (should already be verified, but double-check)
        if (!_simAccountVerified)
        {
            var error = "SIM account not verified - not placing stop entry orders";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                order_type = "ENTRY_STOP",
                account = "SIM"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
        {
            error,
            reason = "NINJATRADER_NOT_DEFINED",
            order_type = "ENTRY_STOP"
        }));
        return OrderSubmissionResult.FailureResult(error, utcNow);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                reason = "NT_CONTEXT_NOT_SET",
                order_type = "ENTRY_STOP"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        // Invariant 1: When IEA enabled, IEA must be bound
        if (_useInstrumentExecutionAuthority && _iea == null)
        {
            var error = "CRITICAL: IEA enabled but not bound - order submission blocked (IEA_BYPASS_ATTEMPTED)";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "IEA_BYPASS_ATTEMPTED", state: "ENGINE",
                new { intent_id = intentId, instrument, reason = "IEA_ENABLED_BUT_NOT_BOUND", error }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        if (!TryExecutionSafetyGateForOrderSubmit(intentId, instrument, "SUBMIT_ENTRY_STOP", utcNow, out var safetyFailStop))
            return safetyFailStop!;

        try
        {
            return SubmitStopEntryOrderReal(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
        }
        catch (Exception ex)
        {
            // Get Intent info for journal logging
            string tradingDate = "";
            string stream = "";
            if (IntentMap.TryGetValue(intentId, out var stopEntryIntent))
            {
                tradingDate = stopEntryIntent.TradingDate;
                stream = stopEntryIntent.Stream;
            }
            _executionJournal.RecordRejection(intentId, tradingDate, stream, $"ENTRY_STOP_SUBMIT_FAILED: {ex.Message}", utcNow, 
                orderType: "ENTRY_STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error = ex.Message,
                order_type = "ENTRY_STOP",
                account = "SIM",
                exception_type = ex.GetType().Name
            }));

            return OrderSubmissionResult.FailureResult($"Stop entry order submission failed: {ex.Message}", utcNow);
        }
    }

}
