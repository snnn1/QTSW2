using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Single entry point for execution permission. Replaces the procedural gate chain in
/// <see cref="NinjaTraderSimAdapter"/> (<c>TryExecutionSafetyGateForOrderSubmit</c>).
/// Phase 1 (facade): delegates to existing gate classes internally.
/// Phase 2: inline gate logic and remove standalone static classes.
/// 
/// Hard rule: UEA must NEVER derive ownership independently. It consumes a single
/// <see cref="InstrumentOwnershipSnapshot"/> obtained at the start of Evaluate().
/// </summary>
public sealed class UnifiedExecutionAuthority
{
    private readonly RobotLogger _log;
    private readonly InstrumentOwnershipLedger? _ownershipLedger;

    public UnifiedExecutionAuthority(RobotLogger log, InstrumentOwnershipLedger? ownershipLedger = null)
    {
        _log = log;
        _ownershipLedger = ownershipLedger;
    }

    private static bool ResolveGlobalKillSwitchActive(AuthorityEvaluationRequest request) =>
        request.PreflightGlobalKillSwitchActive ?? (request.GlobalKillSwitchActive?.Invoke() == true);

    private static bool ResolveMismatchExecutionBlocked(AuthorityEvaluationRequest request, string instrument)
    {
        if (string.IsNullOrEmpty(instrument))
            return false;
        if (request.PreflightMismatchExecutionBlockedForSubmit.HasValue)
            return request.PreflightMismatchExecutionBlockedForSubmit.Value;
        if (request.PreflightMismatchExecutionBlocked.HasValue)
            return request.PreflightMismatchExecutionBlocked.Value;
        if (request.MismatchExecutionBlockedForSubmit != null)
            return request.MismatchExecutionBlockedForSubmit(instrument, request.SubmitPath);
        return request.MismatchExecutionBlocked?.Invoke(instrument) == true;
    }

    private static bool ResolveInstrumentFrozenOrEpaBlocked(AuthorityEvaluationRequest request, string instrument) =>
        !string.IsNullOrEmpty(instrument) &&
        (request.PreflightInstrumentFrozenOrEpaBlocked ??
         (request.InstrumentFrozenOrEpaBlocked?.Invoke(instrument, request.SubmitPath) == true));

    /// <summary>
    /// Evaluate all four gates in order. Short-circuits on first deny.
    /// A single <see cref="InstrumentOwnershipSnapshot"/> is captured once and passed to all gates.
    /// </summary>
    public AuthorityDecision Evaluate(AuthorityEvaluationRequest request)
    {
        var trail = new List<GateEvaluation>();
        var utcNow = request.UtcNow;
        var instrument = request.Instrument?.Trim() ?? "";
        ExecutionAuthorityFrame? authorityFrame = request.AuthorityFrame ?? request.PrebuiltSafetyRequest?.AuthorityFrame;

        InstrumentOwnershipSnapshot? ownershipSnapshot = authorityFrame?.OwnershipSnapshot;
        if (FeatureFlags.CanonicalOwnershipLedgerEnabled && _ownershipLedger != null && !string.IsNullOrEmpty(instrument))
        {
            ownershipSnapshot = _ownershipLedger.GetOwnershipSnapshot(
                request.AccountName ?? "default", instrument);
            if (authorityFrame == null)
            {
                authorityFrame = new ExecutionAuthorityFrame
                {
                    FrameId = ExecutionAuthorityFrame.CreateFrameId(utcNow),
                    Source = "uea_ownership_preflight",
                    Instrument = instrument,
                    CanonicalInstrument = request.CanonicalInstrument,
                    IntentId = request.IntentId,
                    SubmitPath = request.SubmitPath,
                    DecisionUtc = utcNow,
                    FrameCreatedUtc = DateTimeOffset.UtcNow,
                    LedgerAccountName = request.AccountName,
                    LedgerOwnershipVersion = ownershipSnapshot.OwnershipVersion,
                    LedgerSignedNetQty = ownershipSnapshot.LedgerSignedNetQty,
                    LedgerActiveSlotCount = ownershipSnapshot.ActiveSlotCount,
                    LedgerOrphanSlotCount = ownershipSnapshot.OrphanSlotCount,
                    OwnershipSnapshot = ownershipSnapshot
                };
            }
        }

        var globalKillSwitchActive = ResolveGlobalKillSwitchActive(request);
        var mismatchBlocked = ResolveMismatchExecutionBlocked(request, instrument);
        var instrumentFrozenOrEpaBlocked = ResolveInstrumentFrozenOrEpaBlocked(request, instrument);

        // Gate 1: KillSwitch + Recovery (EPA preflight)
        var skipMismatch = request.SubmitIntent == SubmitIntent.RiskCoverage;
        string? epaDeny = null;
        if (globalKillSwitchActive)
            epaDeny = "GLOBAL_KILL_SWITCH_ACTIVE";
        else if (!skipMismatch && mismatchBlocked)
            epaDeny = "MISMATCH_EXECUTION_BLOCK";
        else if (instrumentFrozenOrEpaBlocked)
            epaDeny = "INSTRUMENT_FROZEN_OR_EPA_BLOCKED";

        if (!string.IsNullOrEmpty(epaDeny))
        {
            trail.Add(new GateEvaluation { GateName = "Gate1_KillSwitch_Recovery", Passed = false, DenyReason = epaDeny });
            return AuthorityDecision.Deny("Gate1_KillSwitch_Recovery", epaDeny, ownershipSnapshot, authorityFrame, trail, utcNow);
        }
        trail.Add(new GateEvaluation { GateName = "Gate1_KillSwitch_Recovery", Passed = true });

        // Gate 2: Explicit Mismatch Block (first-class authority input)
        // Gate1 checks mismatch via EPA preflight, but this gate is the explicit mismatch authority
        // for non-coverage submits. When Gate1+Gate2 merge in Phase 2, this becomes the single check.
        if (mismatchBlocked
            && request.SubmitIntent != SubmitIntent.RiskCoverage)
        {
            trail.Add(new GateEvaluation { GateName = "Gate2_MismatchBlock", Passed = false, DenyReason = "MISMATCH_EXECUTION_BLOCKED" });
            return AuthorityDecision.Deny("Gate2_MismatchBlock", "MISMATCH_EXECUTION_BLOCKED", ownershipSnapshot, authorityFrame, trail, utcNow);
        }
        trail.Add(new GateEvaluation { GateName = "Gate2_MismatchBlock", Passed = true });

        // Gate 3: Quant Phase + Parity + Ownership Authority (Structural Layer)
        if (request.BuildSafetyRequest == null)
        {
            trail.Add(new GateEvaluation { GateName = "Gate3_Structural", Passed = true, Detail = "no_safety_request_builder" });
        }
        else
        {
            ExecutionSafetyEvaluationRequest safetyReq;
            try
            {
                safetyReq = request.PrebuiltSafetyRequest ??
                            request.BuildSafetyRequest(instrument, request.IntentId, utcNow);
                authorityFrame = safetyReq.AuthorityFrame ?? authorityFrame;
                ownershipSnapshot = authorityFrame?.OwnershipSnapshot ?? ownershipSnapshot;
            }
            catch
            {
                trail.Add(new GateEvaluation { GateName = "Gate3_Structural", Passed = false, DenyReason = "account_snapshot_failed" });
                return AuthorityDecision.Deny("Gate3_Structural", "account_snapshot_failed", ownershipSnapshot, authorityFrame, trail, utcNow);
            }

            var flattenBypass = request.SubmitIntent == SubmitIntent.Emergency;
            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(safetyReq, request.SubmitPath, flattenBypass, out var structSnap))
            {
                trail.Add(new GateEvaluation
                {
                    GateName = "Gate3_Structural",
                    Passed = false,
                    DenyReason = structSnap.Reason,
                    Detail = structSnap.Detail
                });
                return AuthorityDecision.Deny("Gate3_Structural", structSnap.Reason ?? "structural_deny", ownershipSnapshot, authorityFrame, trail, utcNow);
            }
            trail.Add(new GateEvaluation { GateName = "Gate3_Structural", Passed = true, Detail = structSnap.Detail });

            // Gate 4: Overlay (snapshot freshness, unsafe lock)
            if (!ExecutionSafetyGate.TryEvaluateExecutionOverlay(safetyReq, out var overlay) || overlay.IsBlocked)
            {
                trail.Add(new GateEvaluation
                {
                    GateName = "Gate4_Overlay",
                    Passed = false,
                    DenyReason = overlay.BlockReason.ToString(),
                    Detail = overlay.Detail
                });
                return AuthorityDecision.Deny("Gate4_Overlay", $"OVERLAY:{overlay.BlockReason}", ownershipSnapshot, authorityFrame, trail, utcNow);
            }
            trail.Add(new GateEvaluation { GateName = "Gate4_Overlay", Passed = true });
        }

        return AuthorityDecision.Allow(ownershipSnapshot, authorityFrame, trail, utcNow);
    }
}
