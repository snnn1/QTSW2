// Forensic audits: TSV-friendly snapshots and gate progress classification.

using System;
using System.Text;

namespace QTSW2.Robot.Core.Execution;

internal static class StateConsistencyAuditFormat
{
    /// <summary>
    /// Single-line TSV forgrep/Excel. Registry/runtime position and runtime_orders are often not tracked — "na".
    /// Columns: timestamp, instrument, intent_id, broker_position_qty, registry_position_qty, journal_position_qty,
    /// runtime_position_qty, broker_orders, registry_orders, journal_orders, runtime_orders, mismatch_type
    /// </summary>
    public static string BuildFourStateTsvLine(
        DateTimeOffset utcNow,
        string instrument,
        MismatchObservation? obs,
        MismatchType mismatchType)
    {
        var inst = (instrument ?? "").Replace('\t', ' ');
        var o = obs;
        var intent = (o?.IntentIdsCsv ?? "").Replace('\t', ' ');
        var regPos = o?.RegistryPositionQty;
        var rtPos = o?.RuntimePositionQty;
        var regPosS = regPos.HasValue ? regPos.Value.ToString() : "na";
        var rtPosS = rtPos.HasValue ? rtPos.Value.ToString() : "na";
        var rtOrd = o?.RuntimeOrderCount;
        var rtOrdS = rtOrd.HasValue ? rtOrd.Value.ToString() : "na";

        var sb = new StringBuilder(256);
        sb.Append(utcNow.ToString("o")).Append('\t');
        sb.Append(inst).Append('\t');
        sb.Append(intent).Append('\t');
        sb.Append(o?.BrokerQty ?? 0).Append('\t');
        sb.Append(regPosS).Append('\t');
        sb.Append(o?.LocalQty ?? 0).Append('\t');
        sb.Append(rtPosS).Append('\t');
        sb.Append(o?.BrokerWorkingOrderCount ?? 0).Append('\t');
        sb.Append(o?.LocalWorkingOrderCount ?? 0).Append('\t');
        sb.Append(o?.JournalOpenEntryCount ?? 0).Append('\t');
        sb.Append(rtOrdS).Append('\t');
        sb.Append(mismatchType.ToString());
        return sb.ToString();
    }

    public static string DescribeProgressChange(GateProgressSignature pre, GateProgressSignature post)
    {
        if (pre == post)
            return "none";
        var order = pre.BrokerWorking != post.BrokerWorking || pre.LocalOwnedWorking != post.LocalOwnedWorking;
        var gap = pre.UnexplainedWorkingGap != post.UnexplainedWorkingGap;
        var cls = pre.MismatchType != post.MismatchType;
        var life = pre.GatePhase != post.GatePhase;
        var rel = pre.ReleaseReady != post.ReleaseReady;
        if (order && gap)
            return "order,working_gap";
        if (order)
            return "order";
        if (gap)
            return "working_gap";
        if (cls)
            return "mismatch_class";
        if (life)
            return "lifecycle";
        if (rel)
            return "release_ready";
        return "mixed";
    }
}
