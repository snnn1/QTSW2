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
    /// <param name="instrumentFrozenOrEpaBlocked">Frozen / protective / IEA supervisory — excludes mismatch authority (gate −1b).</param>
    /// <param name="denyReason">Stable machine-oriented code for logs and tests.</param>
    public static bool TryAdapterOrderSubmitPreflight(
        Func<bool>? globalKillSwitchActive,
        Func<string, bool>? mismatchExecutionBlocked,
        Func<string, bool>? instrumentFrozenOrEpaBlocked,
        string instrument,
        out string denyReason)
    {
        denyReason = "";
        if (globalKillSwitchActive != null && globalKillSwitchActive())
        {
            denyReason = "GLOBAL_KILL_SWITCH_ACTIVE";
            return false;
        }

        var inst = instrument?.Trim() ?? "";
        if (!string.IsNullOrEmpty(inst) && mismatchExecutionBlocked != null && mismatchExecutionBlocked(inst))
        {
            denyReason = "MISMATCH_EXECUTION_BLOCK";
            return false;
        }

        if (!string.IsNullOrEmpty(inst) && instrumentFrozenOrEpaBlocked != null && instrumentFrozenOrEpaBlocked(inst))
        {
            denyReason = "INSTRUMENT_FROZEN_OR_EPA_BLOCKED";
            return false;
        }

        return true;
    }
}
