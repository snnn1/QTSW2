using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Adapter-side EPA preflight for <b>risk-increasing (RI)</b> and <b>coverage (RC)</b> <b>order submits</b> only
/// (docs/authority/02_decision_authority.md §2, §5.2): mirrors <see cref="RiskGate"/> gate −1 (instrument block)
/// and gate 1 (global kill switch). Does not apply to <b>risk-reducing (RR)</b> paths — flatten uses
/// <c>TryExecutionSafetyFlattenGuard</c>; cancel / BE modify use separate RR rules.
/// Transition contract: explicit cause → decision (allow/deny) → effect (broker submit or not).
/// </summary>
public static class ExecutionPermissionAuthority
{
    /// <summary>
    /// Adapter order-submit path only: deny before structural/overlay evaluation when RiskGate-equivalent blocks apply.
    /// </summary>
    /// <param name="globalKillSwitchActive">When non-null and returns true, mirrors RiskGate kill-switch deny.</param>
    /// <param name="mismatchExecutionBlocked">G1: EPA-owned mismatch execution block only — when true, deny <c>MISMATCH_EXECUTION_BLOCK</c> (mirrors RiskGate gate −1a).</param>
    /// <param name="instrumentFrozenOrEpaBlocked">
    /// Gate −1b: engine execution lock + optional protective/coverage policy by <paramref name="submitPath"/> (e.g. exempt
    /// <c>SUBMIT_PROTECTIVE_STOP</c> from protective-coordinator latch). Signature receives <c>(instrument, submit_path)</c>.
    /// </param>
    /// <param name="submitPath">Adapter submit bucket (<c>SUBMIT_ENTRY_STOP</c>, <c>SUBMIT_PROTECTIVE_STOP</c>, etc.).</param>
    /// <param name="denyReason">Stable machine-oriented code for logs and tests.</param>
    /// <param name="skipMismatchExecutionBlock">
    /// When true, mismatch execution block is not evaluated (used for <c>SUBMIT_PROTECTIVE_STOP</c> only after
    /// <see cref="ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure"/> succeeds in the adapter).
    /// </param>
    public static bool TryAdapterOrderSubmitPreflight(
        Func<bool>? globalKillSwitchActive,
        Func<string, bool>? mismatchExecutionBlocked,
        Func<string, string?, bool>? instrumentFrozenOrEpaBlocked,
        string instrument,
        string submitPath,
        out string denyReason,
        bool skipMismatchExecutionBlock = false)
    {
        denyReason = "";
        if (globalKillSwitchActive != null && globalKillSwitchActive())
        {
            denyReason = "GLOBAL_KILL_SWITCH_ACTIVE";
            return false;
        }

        var inst = instrument?.Trim() ?? "";
        if (!skipMismatchExecutionBlock &&
            !string.IsNullOrEmpty(inst) && mismatchExecutionBlocked != null && mismatchExecutionBlocked(inst))
        {
            denyReason = "MISMATCH_EXECUTION_BLOCK";
            return false;
        }

        if (!string.IsNullOrEmpty(inst) && instrumentFrozenOrEpaBlocked != null &&
            instrumentFrozenOrEpaBlocked(inst, submitPath))
        {
            denyReason = "INSTRUMENT_FROZEN_OR_EPA_BLOCKED";
            return false;
        }

        return true;
    }
}
