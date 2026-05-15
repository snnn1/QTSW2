using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace QTSW2.Robot.Core.Execution;

public sealed class ExecutionAuthorityFrameBuilderInput
{
    public string FrameId { get; init; } = "";
    public string Source { get; init; } = "";
    public string Account { get; init; } = "";
    public string Instrument { get; init; } = "";
    public string? CanonicalInstrument { get; init; }
    public string? ExecutionInstrumentKey { get; init; }
    public string TradingDate { get; init; } = "";
    public string StreamId { get; init; } = "";
    public string IntentId { get; init; } = "";
    public string OrderRole { get; init; } = "";
    public string SubmitPath { get; init; } = "";
    public string ExecutionMode { get; init; } = "";
    public DateTimeOffset DecisionUtc { get; init; }
    public DateTimeOffset FrameCreatedUtc { get; init; }
    public DateTimeOffset? BrokerSnapshotCapturedUtc { get; init; }
    public string? SnapshotError { get; init; }

    public int BrokerPositionQty { get; init; }
    public int BrokerWorkingOrdersCount { get; init; }
    public int BrokerStopQty { get; init; }
    public int BrokerTargetQty { get; init; }
    public IReadOnlyList<string>? BrokerOrderIds { get; init; }
    public IReadOnlyList<string>? BrokerOrderTags { get; init; }
    public IReadOnlyList<string>? BrokerEntryOrderIds { get; init; }
    public IReadOnlyList<string>? BrokerStopOrderIds { get; init; }
    public IReadOnlyList<string>? BrokerTargetOrderIds { get; init; }
    public IReadOnlyList<string>? BrokerFlattenOrderIds { get; init; }
    public int JournalOpenQty { get; init; }
    public long JournalOpenIntentSetHash { get; init; }
    public int RealOpenQty { get; init; }
    public int RecoveryOpenQty { get; init; }
    public string AuthorityState { get; init; } = "";

    public bool UseInstrumentExecutionAuthority { get; init; }
    public int IeaOwnedPlusAdoptedWorking { get; init; }
    public int IeaRegistryWorkingCount { get; init; }
    public int IeaMismatchTrustedWorkingCount { get; init; }
    public bool IeaSupervisoryBlock { get; init; }
    public bool RecoveryExecutionDisallowed { get; init; }
    public bool JournalIntegrityOrReconciliationRepairActive { get; init; }
    public bool PreflightAuthoritySampled { get; init; }
    public bool PreflightGlobalKillSwitchActive { get; init; }
    public bool PreflightMismatchExecutionBlocked { get; init; }
    public bool? PreflightMismatchExecutionBlockedForSubmit { get; init; }
    public bool PreflightInstrumentFrozenOrEpaBlocked { get; init; }
    public string? PreflightInstrumentFrozenOrEpaBlockReason { get; init; }

    public string? LedgerAccountName { get; init; }
    public long? LedgerOwnershipVersion { get; init; }
    public int? LedgerSignedNetQty { get; init; }
    public int? LedgerActiveSlotCount { get; init; }
    public int? LedgerOrphanSlotCount { get; init; }
    public int? OwnershipOpenQty { get; init; }
    public int? OwnershipSignedQty { get; init; }
    public int? OwnershipActiveSlots { get; init; }
    public int? OwnershipOrphanSlots { get; init; }
    public InstrumentOwnershipSnapshot? OwnershipSnapshot { get; init; }

    public string StreamLifecycleState { get; init; } = "";
    public bool StreamCommitted { get; init; }
    public int NonTerminalStreamsCount { get; init; }
    public int ActiveIntentsCount { get; init; }
    public IReadOnlyList<string>? ActiveIntentIds { get; init; }
    public string ActiveReentryState { get; init; } = "";
    public DateTimeOffset? ScheduledExitTimeUtc { get; init; }

    public string ProtectiveCoverageState { get; init; } = "";
    public int ProtectiveMissingQty { get; init; }
    public bool ProtectivePending { get; init; }
    public string QecPhase { get; init; } = "";
    public bool QecPendingAlignment { get; init; }
    public int QecRecoveryOpenQty { get; init; }

    public bool DurableLatchActive { get; init; }
    public string DurableLatchReason { get; init; } = "";
    public bool MismatchBlockActive { get; init; }
    public string MismatchBlockReason { get; init; } = "";
    public string StructuralDeny { get; init; } = "";
    public bool OverlayLock { get; init; }
    public bool KillSwitchActive { get; init; }
    public bool TimetableAllowed { get; init; }
    public string SessionCloseState { get; init; } = "";
    public DateTimeOffset? SessionCloseSweepAnchorUtc { get; init; }
    public long? SessionCloseSweepWindowAgeMs { get; init; }
    public bool SessionCloseSweepWindowFresh { get; init; } = true;

    public bool IsPlayback { get; init; }
    public bool IsMultiDayScenario { get; init; }
    public string PlaybackScenarioId { get; init; } = "";
    public string ProofLevel { get; init; } = "";
    public string RuntimeSignatureHash { get; init; } = "";
}

public static class ExecutionAuthorityFrameBuilder
{
    private static readonly object RuntimeProofLock = new();
    private static string _cachedRuntimeProofPath = "";
    private static DateTime _cachedRuntimeProofWriteUtc;
    private static long _cachedRuntimeProofLength = -1;
    private static string _cachedRuntimeProofHash = "";

    public static ExecutionAuthorityFrame Build(ExecutionAuthorityFrameBuilderInput input)
    {
        var decisionUtc = input.DecisionUtc == default ? DateTimeOffset.UtcNow : input.DecisionUtc;
        var frameCreatedUtc = input.FrameCreatedUtc == default ? DateTimeOffset.UtcNow : input.FrameCreatedUtc;
        var brokerWorking = Math.Max(0, input.BrokerWorkingOrdersCount);
        var brokerAbsQty = Math.Abs(input.BrokerPositionQty);
        var ownershipSignedQty = input.OwnershipSignedQty ??
                                 input.LedgerSignedNetQty ??
                                 input.OwnershipSnapshot?.LedgerSignedNetQty ??
                                 0;
        var ownershipOpenQty = input.OwnershipOpenQty ?? Math.Abs(ownershipSignedQty);
        var ownershipActiveSlots = input.OwnershipActiveSlots ??
                                   input.LedgerActiveSlotCount ??
                                   input.OwnershipSnapshot?.ActiveSlotCount ??
                                   0;
        var ownershipOrphanSlots = input.OwnershipOrphanSlots ??
                                   input.LedgerOrphanSlotCount ??
                                   input.OwnershipSnapshot?.OrphanSlotCount ??
                                   0;
        var activeIntentIds = input.ActiveIntentIds ?? Array.Empty<string>();
        var activeIntentsCount = Math.Max(input.ActiveIntentsCount, activeIntentIds.Count);
        var accountAgeMs = input.BrokerSnapshotCapturedUtc.HasValue
            ? Math.Max(0L, (long)(decisionUtc - input.BrokerSnapshotCapturedUtc.Value).TotalMilliseconds)
            : (long?)null;

        var failedPredicates = DeriveFailedPredicates(
            brokerAbsQty,
            brokerWorking,
            input.JournalOpenQty,
            ownershipOpenQty,
            activeIntentsCount,
            input.RealOpenQty,
            input.RecoveryOpenQty,
            input.QecRecoveryOpenQty,
            input.StreamLifecycleState,
            input.StreamCommitted,
            input.ActiveReentryState,
            input.BrokerStopQty,
            input.ProtectivePending,
            input.QecPendingAlignment,
            input.UseInstrumentExecutionAuthority,
            input.IeaOwnedPlusAdoptedWorking,
            input.IeaRegistryWorkingCount,
            input.IeaMismatchTrustedWorkingCount,
            input.NonTerminalStreamsCount,
            input.SnapshotError);
        var trackedExposure = HasTrackedExposure(
            input.JournalOpenQty,
            ownershipOpenQty,
            activeIntentsCount,
            input.RealOpenQty,
            input.RecoveryOpenQty,
            input.QecRecoveryOpenQty,
            input.IeaOwnedPlusAdoptedWorking,
            input.IeaRegistryWorkingCount,
            input.IeaMismatchTrustedWorkingCount,
            input.ActiveReentryState);
        var hasProtectedExposure = brokerAbsQty > 0 &&
                                    (input.BrokerStopQty >= brokerAbsQty ||
                                     input.ProtectivePending ||
                                     input.QecPendingAlignment);
        var proofLevel = string.IsNullOrWhiteSpace(input.ProofLevel)
            ? ResolveRuntimeProofLevel()
            : input.ProofLevel.Trim();
        var runtimeSignatureHash = string.IsNullOrWhiteSpace(input.RuntimeSignatureHash)
            ? ResolveRuntimeSignatureHash()
            : input.RuntimeSignatureHash.Trim();
        var executionMode = string.IsNullOrWhiteSpace(input.ExecutionMode)
            ? "UNKNOWN"
            : input.ExecutionMode.Trim();
        var sessionCloseSweepWindowAgeMs = input.SessionCloseSweepWindowAgeMs;
        if (!sessionCloseSweepWindowAgeMs.HasValue && input.SessionCloseSweepAnchorUtc.HasValue)
            sessionCloseSweepWindowAgeMs = Math.Max(0L, (long)(decisionUtc - input.SessionCloseSweepAnchorUtc.Value).TotalMilliseconds);

        return new ExecutionAuthorityFrame
        {
            FrameId = string.IsNullOrWhiteSpace(input.FrameId)
                ? ExecutionAuthorityFrame.CreateFrameId(decisionUtc)
                : input.FrameId,
            Source = input.Source,
            Account = input.Account,
            Instrument = input.Instrument,
            CanonicalInstrument = input.CanonicalInstrument,
            ExecutionInstrumentKey = input.ExecutionInstrumentKey,
            TradingDate = input.TradingDate,
            StreamId = input.StreamId,
            IntentId = input.IntentId,
            OrderRole = input.OrderRole,
            SubmitPath = input.SubmitPath,
            ExecutionMode = executionMode,
            DecisionUtc = decisionUtc,
            FrameCreatedUtc = frameCreatedUtc,
            BrokerSnapshotCapturedUtc = input.BrokerSnapshotCapturedUtc,
            AccountSnapshotAgeMs = accountAgeMs,
            SnapshotError = input.SnapshotError,
            BrokerPositionQty = input.BrokerPositionQty,
            BrokerWorkingOrderCount = brokerWorking,
            BrokerWorkingOrdersCount = brokerWorking,
            BrokerStopQty = input.BrokerStopQty,
            BrokerTargetQty = input.BrokerTargetQty,
            BrokerOrderIds = input.BrokerOrderIds ?? Array.Empty<string>(),
            BrokerOrderTags = input.BrokerOrderTags ?? Array.Empty<string>(),
            BrokerEntryOrderIds = input.BrokerEntryOrderIds ?? Array.Empty<string>(),
            BrokerStopOrderIds = input.BrokerStopOrderIds ?? Array.Empty<string>(),
            BrokerTargetOrderIds = input.BrokerTargetOrderIds ?? Array.Empty<string>(),
            BrokerFlattenOrderIds = input.BrokerFlattenOrderIds ?? Array.Empty<string>(),
            JournalOpenQty = input.JournalOpenQty,
            JournalOpenIntentSetHash = input.JournalOpenIntentSetHash,
            RealOpenQty = input.RealOpenQty,
            RecoveryOpenQty = input.RecoveryOpenQty,
            AuthorityState = input.AuthorityState,
            UseInstrumentExecutionAuthority = input.UseInstrumentExecutionAuthority,
            IeaOwnedPlusAdoptedWorking = input.IeaOwnedPlusAdoptedWorking,
            IeaRegistryWorkingCount = input.IeaRegistryWorkingCount,
            IeaMismatchTrustedWorkingCount = input.IeaMismatchTrustedWorkingCount,
            IeaSupervisoryBlock = input.IeaSupervisoryBlock,
            RecoveryExecutionDisallowed = input.RecoveryExecutionDisallowed,
            JournalIntegrityOrReconciliationRepairActive = input.JournalIntegrityOrReconciliationRepairActive,
            PreflightAuthoritySampled = input.PreflightAuthoritySampled,
            PreflightGlobalKillSwitchActive = input.PreflightGlobalKillSwitchActive,
            PreflightMismatchExecutionBlocked = input.PreflightMismatchExecutionBlocked,
            PreflightMismatchExecutionBlockedForSubmit = input.PreflightMismatchExecutionBlockedForSubmit,
            PreflightInstrumentFrozenOrEpaBlocked = input.PreflightInstrumentFrozenOrEpaBlocked,
            PreflightInstrumentFrozenOrEpaBlockReason = input.PreflightInstrumentFrozenOrEpaBlockReason,
            LedgerAccountName = input.LedgerAccountName,
            LedgerOwnershipVersion = input.LedgerOwnershipVersion,
            LedgerSignedNetQty = input.LedgerSignedNetQty,
            LedgerActiveSlotCount = input.LedgerActiveSlotCount,
            LedgerOrphanSlotCount = input.LedgerOrphanSlotCount,
            OwnershipOpenQty = ownershipOpenQty,
            OwnershipSignedQty = ownershipSignedQty,
            OwnershipActiveSlots = ownershipActiveSlots,
            OwnershipOrphanSlots = ownershipOrphanSlots,
            OwnershipSnapshot = input.OwnershipSnapshot,
            StreamLifecycleState = input.StreamLifecycleState,
            StreamCommitted = input.StreamCommitted,
            NonTerminalStreamsCount = Math.Max(0, input.NonTerminalStreamsCount),
            ActiveIntentsCount = activeIntentsCount,
            ActiveIntentIds = activeIntentIds,
            ActiveReentryState = input.ActiveReentryState,
            ScheduledExitTimeUtc = input.ScheduledExitTimeUtc,
            ProtectiveCoverageState = input.ProtectiveCoverageState,
            ProtectiveMissingQty = input.ProtectiveMissingQty,
            ProtectivePending = input.ProtectivePending,
            QecPhase = input.QecPhase,
            QecPendingAlignment = input.QecPendingAlignment,
            QecRecoveryOpenQty = input.QecRecoveryOpenQty,
            DurableLatchActive = input.DurableLatchActive,
            DurableLatchReason = input.DurableLatchReason,
            MismatchBlockActive = input.MismatchBlockActive,
            MismatchBlockReason = input.MismatchBlockReason,
            StructuralDeny = input.StructuralDeny,
            OverlayLock = input.OverlayLock,
            KillSwitchActive = input.KillSwitchActive,
            TimetableAllowed = input.TimetableAllowed,
            SessionCloseState = input.SessionCloseState,
            SessionCloseSweepAnchorUtc = input.SessionCloseSweepAnchorUtc,
            SessionCloseSweepWindowAgeMs = sessionCloseSweepWindowAgeMs,
            SessionCloseSweepWindowFresh = input.SessionCloseSweepWindowFresh,
            IsPlayback = input.IsPlayback,
            IsMultiDayScenario = input.IsMultiDayScenario,
            PlaybackScenarioId = input.PlaybackScenarioId,
            ProofLevel = proofLevel,
            RuntimeSignatureHash = runtimeSignatureHash,
            IsCleanFlat = failedPredicates.Count == 0,
            HasTrackedExposure = trackedExposure,
            HasUntrackedExposure = (brokerAbsQty > 0 || brokerWorking > 0) && !trackedExposure,
            HasProtectedExposure = hasProtectedExposure,
            HasContradiction = HasContradiction(failedPredicates),
            FailedPredicates = failedPredicates
        };
    }

    private static string ResolveRuntimeProofLevel()
    {
        var hash = ResolveRuntimeSignatureHash();
        return string.IsNullOrWhiteSpace(hash) || hash.StartsWith("unavailable", StringComparison.OrdinalIgnoreCase) ||
               hash.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
            ? "loaded-assembly-hash-unavailable"
            : "loaded-assembly-hash";
    }

    private static string ResolveRuntimeSignatureHash()
    {
        try
        {
            var assembly = typeof(ExecutionAuthorityFrameBuilder).Assembly;
            var path = assembly.Location;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return "unavailable";

            var fullPath = Path.GetFullPath(path.Trim());
            var writeUtc = File.GetLastWriteTimeUtc(fullPath);
            var length = new FileInfo(fullPath).Length;
            lock (RuntimeProofLock)
            {
                if (string.Equals(_cachedRuntimeProofPath, fullPath, StringComparison.OrdinalIgnoreCase) &&
                    _cachedRuntimeProofWriteUtc == writeUtc &&
                    _cachedRuntimeProofLength == length &&
                    !string.IsNullOrWhiteSpace(_cachedRuntimeProofHash))
                    return _cachedRuntimeProofHash;

                var hash = ComputeSha256(fullPath);
                _cachedRuntimeProofPath = fullPath;
                _cachedRuntimeProofWriteUtc = writeUtc;
                _cachedRuntimeProofLength = length;
                _cachedRuntimeProofHash = hash;
                return hash;
            }
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(stream);
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    public static Dictionary<string, object?> ToLogPayload(
        ExecutionAuthorityFrame frame,
        string? action = null,
        string? decision = null,
        string? denyReason = null)
    {
        return new Dictionary<string, object?>
        {
            ["authority_frame_id"] = frame.FrameId,
            ["source"] = frame.Source,
            ["action"] = action,
            ["decision"] = decision,
            ["deny_reason"] = denyReason,
            ["account"] = frame.Account,
            ["instrument"] = frame.Instrument,
            ["canonical_instrument"] = frame.CanonicalInstrument,
            ["execution_instrument_key"] = frame.ExecutionInstrumentKey,
            ["trading_date"] = frame.TradingDate,
            ["stream"] = frame.StreamId,
            ["intent_id"] = frame.IntentId,
            ["order_role"] = frame.OrderRole,
            ["submit_path"] = frame.SubmitPath,
            ["execution_mode"] = frame.ExecutionMode,
            ["broker_snapshot_captured_at"] = frame.BrokerSnapshotCapturedUtc,
            ["account_snapshot_age_ms"] = frame.AccountSnapshotAgeMs,
            ["snapshot_error"] = frame.SnapshotError,
            ["broker_qty"] = frame.BrokerPositionQty,
            ["broker_working_count"] = frame.BrokerWorkingOrdersCount,
            ["broker_stop_qty"] = frame.BrokerStopQty,
            ["broker_target_qty"] = frame.BrokerTargetQty,
            ["broker_order_ids_count"] = frame.BrokerOrderIds?.Count ?? 0,
            ["broker_order_ids"] = JoinValues(frame.BrokerOrderIds),
            ["broker_order_tags_count"] = frame.BrokerOrderTags?.Count ?? 0,
            ["broker_order_tags"] = JoinValues(frame.BrokerOrderTags),
            ["broker_entry_order_ids_count"] = frame.BrokerEntryOrderIds?.Count ?? 0,
            ["broker_entry_order_ids"] = JoinValues(frame.BrokerEntryOrderIds),
            ["broker_stop_order_ids_count"] = frame.BrokerStopOrderIds?.Count ?? 0,
            ["broker_stop_order_ids"] = JoinValues(frame.BrokerStopOrderIds),
            ["broker_target_order_ids_count"] = frame.BrokerTargetOrderIds?.Count ?? 0,
            ["broker_target_order_ids"] = JoinValues(frame.BrokerTargetOrderIds),
            ["broker_flatten_order_ids_count"] = frame.BrokerFlattenOrderIds?.Count ?? 0,
            ["broker_flatten_order_ids"] = JoinValues(frame.BrokerFlattenOrderIds),
            ["journal_open_qty"] = frame.JournalOpenQty,
            ["journal_open_intent_hash"] = frame.JournalOpenIntentSetHash,
            ["ownership_open_qty"] = frame.OwnershipOpenQty,
            ["ownership_signed_qty"] = frame.OwnershipSignedQty,
            ["ownership_active_slots"] = frame.OwnershipActiveSlots,
            ["ownership_orphan_slots"] = frame.OwnershipOrphanSlots,
            ["real_open_qty"] = frame.RealOpenQty,
            ["recovery_open_qty"] = frame.RecoveryOpenQty,
            ["authority_state"] = frame.AuthorityState,
            ["stream_lifecycle_state"] = frame.StreamLifecycleState,
            ["stream_committed"] = frame.StreamCommitted,
            ["nonterminal_streams_count"] = frame.NonTerminalStreamsCount,
            ["active_intents_count"] = frame.ActiveIntentsCount,
            ["active_intent_ids_count"] = frame.ActiveIntentIds?.Count ?? 0,
            ["active_intent_ids"] = JoinValues(frame.ActiveIntentIds),
            ["active_reentry_state"] = frame.ActiveReentryState,
            ["scheduled_exit_time_utc"] = frame.ScheduledExitTimeUtc,
            ["use_iea"] = frame.UseInstrumentExecutionAuthority,
            ["iea_owned_plus_adopted_working"] = frame.IeaOwnedPlusAdoptedWorking,
            ["iea_registry_working_count"] = frame.IeaRegistryWorkingCount,
            ["iea_mismatch_trusted_working_count"] = frame.IeaMismatchTrustedWorkingCount,
            ["iea_supervisory_block"] = frame.IeaSupervisoryBlock,
            ["recovery_execution_disallowed"] = frame.RecoveryExecutionDisallowed,
            ["journal_integrity_or_reconciliation_repair_active"] = frame.JournalIntegrityOrReconciliationRepairActive,
            ["protective_coverage_state"] = frame.ProtectiveCoverageState,
            ["protective_missing_qty"] = frame.ProtectiveMissingQty,
            ["protective_pending"] = frame.ProtectivePending,
            ["qec_phase"] = frame.QecPhase,
            ["qec_pending_alignment"] = frame.QecPendingAlignment,
            ["qec_recovery_open_qty"] = frame.QecRecoveryOpenQty,
            ["durable_latch_active"] = frame.DurableLatchActive,
            ["durable_latch_reason"] = frame.DurableLatchReason,
            ["mismatch_block_active"] = frame.MismatchBlockActive,
            ["mismatch_block_reason"] = frame.MismatchBlockReason,
            ["structural_deny"] = frame.StructuralDeny,
            ["overlay_lock"] = frame.OverlayLock,
            ["kill_switch_active"] = frame.KillSwitchActive,
            ["timetable_allowed"] = frame.TimetableAllowed,
            ["session_close_state"] = frame.SessionCloseState,
            ["session_close_sweep_anchor_utc"] = frame.SessionCloseSweepAnchorUtc,
            ["session_close_sweep_window_age_ms"] = frame.SessionCloseSweepWindowAgeMs,
            ["session_close_sweep_window_fresh"] = frame.SessionCloseSweepWindowFresh,
            ["is_playback"] = frame.IsPlayback,
            ["is_multi_day_scenario"] = frame.IsMultiDayScenario,
            ["playback_scenario_id"] = frame.PlaybackScenarioId,
            ["proof_level"] = frame.ProofLevel,
            ["runtime_signature_hash"] = frame.RuntimeSignatureHash,
            ["is_clean_flat"] = frame.IsCleanFlat,
            ["has_tracked_exposure"] = frame.HasTrackedExposure,
            ["has_untracked_exposure"] = frame.HasUntrackedExposure,
            ["has_protected_exposure"] = frame.HasProtectedExposure,
            ["has_contradiction"] = frame.HasContradiction,
            ["failed_predicates_count"] = frame.FailedPredicates?.Count ?? 0,
            ["failed_predicates"] = JoinValues(frame.FailedPredicates)
        };
    }

    private static string JoinValues(IEnumerable<string>? values)
    {
        if (values == null) return "";
        var normalized = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToArray();
        return normalized.Length == 0 ? "" : string.Join(",", normalized);
    }

    private static List<string> DeriveFailedPredicates(
        int brokerAbsQty,
        int brokerWorkingOrdersCount,
        int journalOpenQty,
        int ownershipOpenQty,
        int activeIntentsCount,
        int realOpenQty,
        int recoveryOpenQty,
        int qecRecoveryOpenQty,
        string streamLifecycleState,
        bool streamCommitted,
        string activeReentryState,
        int brokerStopQty,
        bool protectivePending,
        bool qecPendingAlignment,
        bool useInstrumentExecutionAuthority,
        int ieaOwnedPlusAdoptedWorking,
        int ieaRegistryWorkingCount,
        int ieaMismatchTrustedWorkingCount,
        int nonTerminalStreamsCount,
        string? snapshotError)
    {
        var failed = new List<string>();
        if (brokerAbsQty != 0) failed.Add("BROKER_POSITION_OPEN");
        if (brokerWorkingOrdersCount != 0) failed.Add("BROKER_WORKING_ORDERS_OPEN");
        if (journalOpenQty != 0) failed.Add("JOURNAL_OPEN_QTY");
        if (ownershipOpenQty != 0) failed.Add("OWNERSHIP_OPEN_QTY");
        if (activeIntentsCount != 0) failed.Add("ACTIVE_INTENTS_OPEN");
        if (recoveryOpenQty != 0 || qecRecoveryOpenQty != 0) failed.Add("RECOVERY_OPEN_QTY");
        if (nonTerminalStreamsCount > 0 || IsNonTerminalStream(streamLifecycleState, streamCommitted))
            failed.Add("NONTERMINAL_STREAM");
        if (HasOpenReentryState(activeReentryState)) failed.Add("ACTIVE_REENTRY_OPEN");
        if (!string.IsNullOrWhiteSpace(snapshotError)) failed.Add("ACCOUNT_SNAPSHOT_ERROR");

        var trackedExposure = HasTrackedExposure(
            journalOpenQty,
            ownershipOpenQty,
            activeIntentsCount,
            realOpenQty,
            recoveryOpenQty,
            qecRecoveryOpenQty,
            ieaOwnedPlusAdoptedWorking,
            ieaRegistryWorkingCount,
            ieaMismatchTrustedWorkingCount,
            activeReentryState);
        if ((brokerAbsQty > 0 || brokerWorkingOrdersCount > 0) && !trackedExposure)
            failed.Add("UNTRACKED_BROKER_EXPOSURE");
        if (brokerAbsQty == 0 &&
            (journalOpenQty > 0 || ownershipOpenQty > 0 || recoveryOpenQty > 0 || qecRecoveryOpenQty > 0))
            failed.Add("BROKER_NET_FLAT_BUT_ROBOT_OPEN");
        if (brokerAbsQty > 0 &&
            brokerStopQty < brokerAbsQty &&
            !protectivePending &&
            !qecPendingAlignment)
            failed.Add("UNPROTECTED_BROKER_EXPOSURE");
        if (useInstrumentExecutionAuthority &&
            brokerWorkingOrdersCount > 0 &&
            ieaOwnedPlusAdoptedWorking == 0 &&
            ieaRegistryWorkingCount == 0 &&
            ieaMismatchTrustedWorkingCount == 0)
            failed.Add("BROKER_WORKING_WITH_EMPTY_IEA_REGISTRY");
        return failed;
    }

    private static bool HasTrackedExposure(
        int journalOpenQty,
        int ownershipOpenQty,
        int activeIntentsCount,
        int realOpenQty,
        int recoveryOpenQty,
        int qecRecoveryOpenQty,
        int ieaOwnedPlusAdoptedWorking,
        int ieaRegistryWorkingCount,
        int ieaMismatchTrustedWorkingCount,
        string activeReentryState)
    {
        return journalOpenQty > 0 ||
               ownershipOpenQty > 0 ||
               activeIntentsCount > 0 ||
               realOpenQty > 0 ||
               recoveryOpenQty > 0 ||
               qecRecoveryOpenQty > 0 ||
               ieaOwnedPlusAdoptedWorking > 0 ||
               ieaRegistryWorkingCount > 0 ||
               ieaMismatchTrustedWorkingCount > 0 ||
               HasOpenReentryState(activeReentryState);
    }

    private static bool HasContradiction(IReadOnlyList<string> failedPredicates)
    {
        foreach (var p in failedPredicates)
        {
            if (p == "UNTRACKED_BROKER_EXPOSURE" ||
                p == "BROKER_NET_FLAT_BUT_ROBOT_OPEN" ||
                p == "BROKER_WORKING_WITH_EMPTY_IEA_REGISTRY" ||
                p == "ACCOUNT_SNAPSHOT_ERROR")
                return true;
        }
        return false;
    }

    private static bool HasOpenReentryState(string state)
    {
        if (string.IsNullOrWhiteSpace(state)) return false;
        var s = state.Trim();
        return !s.Equals("NONE", StringComparison.OrdinalIgnoreCase) &&
               !s.Equals("CLOSED", StringComparison.OrdinalIgnoreCase) &&
               !s.Equals("COMPLETE", StringComparison.OrdinalIgnoreCase) &&
               !s.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) &&
               !s.Equals("TERMINAL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonTerminalStream(string state, bool committed)
    {
        if (string.IsNullOrWhiteSpace(state)) return false;
        var s = state.Trim();
        return !committed &&
               !s.Equals("DONE", StringComparison.OrdinalIgnoreCase) &&
               !s.Equals("FAILED", StringComparison.OrdinalIgnoreCase) &&
               !s.Equals("FAILED_RUNTIME", StringComparison.OrdinalIgnoreCase) &&
               !s.Equals("NO_TRADE", StringComparison.OrdinalIgnoreCase) &&
               !s.Equals("EXPIRED", StringComparison.OrdinalIgnoreCase) &&
               !s.Equals("COMPLETE", StringComparison.OrdinalIgnoreCase) &&
               !s.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase);
    }
}
