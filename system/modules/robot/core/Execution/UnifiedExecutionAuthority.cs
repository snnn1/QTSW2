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

    /// <summary>
    /// Evaluate all four gates in order. Short-circuits on first deny.
    /// A single <see cref="InstrumentOwnershipSnapshot"/> is captured once and passed to all gates.
    /// </summary>
    public AuthorityDecision Evaluate(AuthorityEvaluationRequest request)
    {
        var trail = new List<GateEvaluation>();
        var utcNow = request.UtcNow;
        var instrument = request.Instrument?.Trim() ?? "";

        InstrumentOwnershipSnapshot? ownershipSnapshot = null;
        if (FeatureFlags.CanonicalOwnershipLedgerEnabled && _ownershipLedger != null && !string.IsNullOrEmpty(instrument))
        {
            ownershipSnapshot = _ownershipLedger.GetOwnershipSnapshot(
                request.AccountName ?? "default", instrument);
        }

        // Gate 1: KillSwitch + Recovery (EPA preflight)
        var skipMismatch = request.SubmitIntent == SubmitIntent.RiskCoverage;
        if (!ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(
                request.GlobalKillSwitchActive,
                request.MismatchExecutionBlocked,
                request.InstrumentFrozenOrEpaBlocked,
                instrument,
                request.SubmitPath,
                out var epaDeny,
                skipMismatchExecutionBlock: skipMismatch))
        {
            trail.Add(new GateEvaluation { GateName = "Gate1_KillSwitch_Recovery", Passed = false, DenyReason = epaDeny });
            return AuthorityDecision.Deny("Gate1_KillSwitch_Recovery", epaDeny, ownershipSnapshot, trail, utcNow);
        }
        trail.Add(new GateEvaluation { GateName = "Gate1_KillSwitch_Recovery", Passed = true });

        // Gate 2: Explicit Mismatch Block (first-class authority input)
        // Gate1 checks mismatch via EPA preflight, but this gate is the explicit mismatch authority
        // for non-coverage submits. When Gate1+Gate2 merge in Phase 2, this becomes the single check.
        if (request.MismatchExecutionBlocked?.Invoke(instrument) == true
            && request.SubmitIntent != SubmitIntent.RiskCoverage)
        {
            trail.Add(new GateEvaluation { GateName = "Gate2_MismatchBlock", Passed = false, DenyReason = "MISMATCH_EXECUTION_BLOCKED" });
            return AuthorityDecision.Deny("Gate2_MismatchBlock", "MISMATCH_EXECUTION_BLOCKED", ownershipSnapshot, trail, utcNow);
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
                safetyReq = request.BuildSafetyRequest(instrument, request.IntentId, utcNow);
            }
            catch
            {
                trail.Add(new GateEvaluation { GateName = "Gate3_Structural", Passed = false, DenyReason = "account_snapshot_failed" });
                return AuthorityDecision.Deny("Gate3_Structural", "account_snapshot_failed", ownershipSnapshot, trail, utcNow);
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
                return AuthorityDecision.Deny("Gate3_Structural", structSnap.Reason ?? "structural_deny", ownershipSnapshot, trail, utcNow);
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
                return AuthorityDecision.Deny("Gate4_Overlay", $"OVERLAY:{overlay.BlockReason}", ownershipSnapshot, trail, utcNow);
            }
            trail.Add(new GateEvaluation { GateName = "Gate4_Overlay", Passed = true });
        }

        return AuthorityDecision.Allow(ownershipSnapshot, trail, utcNow);
    }
}
