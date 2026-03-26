#!/usr/bin/env python3
"""
CPU spike root-cause analyzer: correlate ENGINE_CPU_PROFILE with execution activity.

Read-only. Uses normalize_event from log_audit (same as daily_audit / burst tools).
Spike detection (temporary diagnostic): ``process_cpu_pct`` or derived ``sum/wall`` cpu_pct > 20,
or any ENGINE_CPU_PROFILE subsystem ``total_ms`` > 5.

Usage:
  python tools/diagnose_cpu_spike.py --date 2026-03-25 --tz utc
  python tools/diagnose_cpu_spike.py --date 2026-03-25 --tz chicago --instrument MNQ --only-spikes-above 50
"""

from __future__ import annotations

import argparse
import bisect
import json
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Tuple

try:
    from zoneinfo import ZoneInfo
except Exception:  # pragma: no cover
    ZoneInfo = None  # type: ignore

from log_audit import NormEvent, normalize_event, parse_ts, resolve_log_dir

CHICAGO = "America/Chicago"

CPU_EVENT = "ENGINE_CPU_PROFILE"
GATE_RESULT = "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT"

COUNT_EVENTS = frozenset(
    {
        GATE_RESULT,
        "RECONCILIATION_THROTTLED",
        "RECONCILIATION_HARD_STOPPED",
        "EXECUTION_DEDUP_SKIPPED_PERMANENT",
        "ORDER_UPDATE_DEDUP_SKIPPED",
        "EXECUTION_TRACE",
    }
)

# Subsystem name -> logical bucket (matches RuntimeAuditSubsystem.cs)
RECON_NAMES = frozenset(
    {
        "RECONCILIATION",
        "RECONCILIATION_THROTTLE",
        "REGISTRY_VERIFY",
        "ASSEMBLE_MISMATCH",
        "MISMATCH_DIAGNOSTICS",
    }
)
STREAM_NAMES = frozenset({"ENGINE_TICK", "STREAM_LOOP"})
# ENGINE_TICK_TOTAL overlaps children; use only if stream components are zero
ENGINE_TICK_TOTAL = "ENGINE_TICK_TOTAL"
COORD_NAMES = frozenset(
    {
        "PROTECTIVE_TIMER_TOTAL",
        "MISMATCH_TIMER_TOTAL",
        "IEA_WORK_TOTAL",
        "IEA_SCAN",
        "NT_ACTION_DRAIN",
        "EMIT_METRICS_PROTECTIVE",
        "EMIT_METRICS_MISMATCH",
    }
)
LOCK_NAME = "ENGINE_LOCK_REGION"

# Fast pre-scan before json.loads (large JSONL files)
_INTERESTING_SUBSTR = (
    '"event":"ENGINE_CPU_PROFILE"',
    '"event": "ENGINE_CPU_PROFILE',
    "ENGINE_CPU_PROFILE",
    "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT",
    "RECONCILIATION_THROTTLED",
    "RECONCILIATION_HARD_STOPPED",
    "EXECUTION_DEDUP_SKIPPED_PERMANENT",
    "ORDER_UPDATE_DEDUP_SKIPPED",
    '"event":"EXECUTION_TRACE"',
    '"event": "EXECUTION_TRACE',
)


def _line_maybe_relevant(line: str) -> bool:
    return any(s in line for s in _INTERESTING_SUBSTR)


def day_window_utc(d: date, tz_name: str) -> Tuple[datetime, datetime]:
    if tz_name == "utc":
        start = datetime(d.year, d.month, d.day, tzinfo=timezone.utc)
        return start, start + timedelta(days=1)
    if ZoneInfo is None:
        start = datetime(d.year, d.month, d.day, tzinfo=timezone.utc)
        return start, start + timedelta(days=1)
    tz = ZoneInfo(CHICAGO)
    start_local = datetime(d.year, d.month, d.day, 0, 0, 0, tzinfo=tz)
    end_local = start_local + timedelta(days=1)
    return start_local.astimezone(timezone.utc), end_local.astimezone(timezone.utc)


def collect_robot_jsonl_paths(log_dir: Path) -> List[Path]:
    """All robot_*.jsonl under log_dir and log_dir/archive (read-only audit)."""
    paths: List[Path] = []
    for p in sorted(log_dir.glob("robot_*.jsonl")):
        paths.append(p)
    arch = log_dir / "archive"
    if arch.is_dir():
        for p in sorted(arch.glob("robot_*.jsonl")):
            paths.append(p)
    seen: set = set()
    out: List[Path] = []
    for p in paths:
        rp = p.resolve()
        if rp not in seen:
            seen.add(rp)
            out.append(p)
    return out


def iter_jsonl(path: Path) -> Iterable[Tuple[int, str]]:
    try:
        with path.open("r", encoding="utf-8", errors="replace") as f:
            for i, line in enumerate(f, 1):
                yield i, line.strip()
    except OSError:
        return


def _floatish(v: Any) -> Optional[float]:
    if v is None:
        return None
    if isinstance(v, (int, float)):
        return float(v)
    try:
        return float(str(v).strip())
    except Exception:
        return None


def _get(d: Dict[str, Any], *keys: str) -> Any:
    for k in keys:
        if k in d and d[k] is not None:
            return d[k]
    return None


def parse_subsystems(data: Dict[str, Any]) -> Dict[str, float]:
    """name -> total_ms from ENGINE_CPU_PROFILE data."""
    out: Dict[str, float] = {}
    subs = _get(data, "subsystems", "Subsystems")
    if not isinstance(subs, list):
        return out
    for item in subs:
        if not isinstance(item, dict):
            continue
        name = str(_get(item, "name", "Name") or "").strip()
        if not name:
            continue
        ms = _floatish(_get(item, "total_ms", "totalMs"))
        if ms is not None:
            out[name] = ms
    return out


def bucket_ms(sub: Dict[str, float]) -> Tuple[float, float, float, float]:
    """reconciliation, stream_tick, coordinators, lock (ms for this profile row)."""
    recon = sum(sub.get(n, 0.0) for n in RECON_NAMES)
    stream = sum(sub.get(n, 0.0) for n in STREAM_NAMES)
    if stream <= 0 and sub.get(ENGINE_TICK_TOTAL, 0) > 0:
        stream = float(sub[ENGINE_TICK_TOTAL])
    coord = sum(sub.get(n, 0.0) for n in COORD_NAMES)
    lock = float(sub.get(LOCK_NAME, 0.0))
    return recon, stream, coord, lock


def cpu_pct_from_data(data: Dict[str, Any]) -> Optional[float]:
    wall = _floatish(_get(data, "wall_window_ms", "wallWindowMs"))
    summed = _get(data, "sum_subsystem_ms", "sumSubsystemMs")
    if wall is None or wall <= 0:
        return None
    if isinstance(summed, (int, float)):
        return max(0.0, min(100.0, 100.0 * float(summed) / wall))
    return None


def process_cpu_pct_from_data(data: Dict[str, Any]) -> Optional[float]:
    v = _floatish(_get(data, "process_cpu_pct", "processCpuPct"))
    if v is None:
        return None
    return max(0.0, min(100.0, v))


def event_instrument(e: NormEvent) -> str:
    inst = (e.instrument or "").strip()
    if inst:
        return inst
    d = e.data if isinstance(e.data, dict) else {}
    return str(_get(d, "instrument", "Instrument") or "").strip()


def is_spike(
    cpu_pct: Optional[float],
    sub: Dict[str, float],
    process_cpu_pct: Optional[float] = None,
) -> bool:
    """Diagnostic mode: sensitive thresholds to surface real spikes in sparse or low-signal profiles."""
    if process_cpu_pct is not None and process_cpu_pct > 20:
        return True
    if cpu_pct is not None and cpu_pct > 20:
        return True
    for _n, ms in sub.items():
        if ms > 5:
            return True
    return False


@dataclass
class SpikeAnchor:
    ts_utc: datetime
    instrument: str
    file: str
    wall_ms: float
    cpu_pct: Optional[float]
    process_cpu_pct: Optional[float]
    recon: float
    stream: float
    coord: float
    lock: float
    sub_raw: Dict[str, float] = field(default_factory=dict)


def ingest_robot_day(
    paths: List[Path],
    day_start: datetime,
    day_end: datetime,
    instrument_filter: Optional[str],
    stats: Dict[str, Any],
) -> Tuple[List[SpikeAnchor], List[Tuple[datetime, str, str]]]:
    """
    Single pass over robot_*.jsonl (excludes execution_trace.jsonl): CPU profiles + counter events.
    Returns (all ENGINE_CPU_PROFILE anchors in day sorted by ts, sorted counter rows (ts, event, instrument)).
    """
    spikes: List[SpikeAnchor] = []
    counters: List[Tuple[datetime, str, str]] = []
    inst_f = instrument_filter.strip().upper() if instrument_filter else None

    for path in paths:
        if path.name == "execution_trace.jsonl":
            continue
        for _ln, line in iter_jsonl(path):
            if not line:
                continue
            if not _line_maybe_relevant(line):
                continue
            try:
                obj = json.loads(line)
                if not isinstance(obj, dict):
                    continue
            except json.JSONDecodeError:
                stats["parse_errors"] = stats.get("parse_errors", 0) + 1
                continue
            ev = normalize_event(obj, path.name)
            if ev is None:
                continue
            if ev.ts_utc < day_start or ev.ts_utc >= day_end:
                continue
            en = ev.event or ""
            inst = event_instrument(ev)

            if en in COUNT_EVENTS:
                if inst_f and inst.upper() != inst_f:
                    pass
                else:
                    counters.append((ev.ts_utc, en, inst))

            if en != CPU_EVENT:
                continue

            d = ev.data if isinstance(ev.data, dict) else {}
            sub = parse_subsystems(d)
            cpu_pct = cpu_pct_from_data(d)
            proc_pct = process_cpu_pct_from_data(d)
            if inst_f and inst.upper() != inst_f:
                continue
            if not is_spike(cpu_pct, sub, proc_pct):
                continue
            r, st, c, lk = bucket_ms(sub)
            wall = _floatish(_get(d, "wall_window_ms", "wallWindowMs")) or 0.0
            spikes.append(
                SpikeAnchor(
                    ts_utc=ev.ts_utc,
                    instrument=inst or "?",
                    file=path.name,
                    wall_ms=wall,
                    cpu_pct=cpu_pct,
                    process_cpu_pct=proc_pct,
                    recon=r,
                    stream=st,
                    coord=c,
                    lock=lk,
                    sub_raw=dict(sub),
                )
            )
        stats["files_scanned"] = stats.get("files_scanned", 0) + 1

    spikes.sort(key=lambda s: s.ts_utc)
    counters.sort(key=lambda x: x[0])
    return spikes, counters


def count_counters_in_window(
    counters: List[Tuple[datetime, str, str]], t0: datetime, t1: datetime
) -> Tuple[int, int, int, int, int, int]:
    ts_only = [c[0] for c in counters]
    lo = bisect.bisect_left(ts_only, t0)
    hi = bisect.bisect_right(ts_only, t1)
    cycles = throttled = hard_stop = dedup_perm = dedup_order = exec_trace = 0
    for i in range(lo, hi):
        _, en, _ = counters[i]
        if en == GATE_RESULT:
            cycles += 1
        elif en == "RECONCILIATION_THROTTLED":
            throttled += 1
        elif en == "RECONCILIATION_HARD_STOPPED":
            hard_stop += 1
        elif en == "EXECUTION_DEDUP_SKIPPED_PERMANENT":
            dedup_perm += 1
        elif en == "ORDER_UPDATE_DEDUP_SKIPPED":
            dedup_order += 1
        elif en == "EXECUTION_TRACE":
            exec_trace += 1
    return cycles, throttled, hard_stop, dedup_perm, dedup_order, exec_trace


def cpu_profiles_in_window(anchors: List[SpikeAnchor], t0: datetime, t1: datetime) -> List[SpikeAnchor]:
    ts_only = [a.ts_utc for a in anchors]
    lo = bisect.bisect_left(ts_only, t0)
    hi = bisect.bisect_right(ts_only, t1)
    return anchors[lo:hi]


@dataclass
class WindowStats:
    t0: datetime
    t1: datetime
    anchor: SpikeAnchor
    cpu_in_window: List[SpikeAnchor] = field(default_factory=list)
    avg_cpu_pct: Optional[float] = None
    peak_cpu_pct: Optional[float] = None
    avg_recon: float = 0.0
    avg_stream: float = 0.0
    avg_coord: float = 0.0
    avg_lock: float = 0.0
    cycles: int = 0
    throttled: int = 0
    hard_stop: int = 0
    dedup_perm: int = 0
    dedup_order: int = 0
    exec_trace_robot: int = 0
    on_exec_update: int = 0
    on_order_update: int = 0
    max_per_10ms_trace: int = 0


def aggregate_cpu_in_window(
    all_cpu: List[SpikeAnchor], t0: datetime, t1: datetime, anchor: SpikeAnchor
) -> WindowStats:
    ws = WindowStats(t0=t0, t1=t1, anchor=anchor)
    inside = cpu_profiles_in_window(all_cpu, t0, t1)
    ws.cpu_in_window = inside
    if not inside:
        inside = [anchor]

    cpu_vals = [a.cpu_pct for a in inside if a.cpu_pct is not None]
    if cpu_vals:
        ws.avg_cpu_pct = sum(cpu_vals) / len(cpu_vals)
        ws.peak_cpu_pct = max(cpu_vals)

    n = len(inside)
    ws.avg_recon = sum(a.recon for a in inside) / n
    ws.avg_stream = sum(a.stream for a in inside) / n
    ws.avg_coord = sum(a.coord for a in inside) / n
    ws.avg_lock = sum(a.lock for a in inside) / n
    return ws


def load_execution_trace_day(
    path: Path,
    day_start: datetime,
    day_end: datetime,
    instrument_filter: Optional[str],
) -> List[Tuple[datetime, str, str, str]]:
    """Sorted rows: (ts_utc, instrument, source, call_stage) for EXECUTION_TRACE only."""
    if not path.is_file():
        return []
    inst_f = instrument_filter.strip().upper() if instrument_filter else None
    rows: List[Tuple[datetime, str, str, str]] = []
    for _ln, line in iter_jsonl(path):
        if not line or "EXECUTION_TRACE" not in line:
            continue
        try:
            obj = json.loads(line)
            if not isinstance(obj, dict):
                continue
        except json.JSONDecodeError:
            continue
        if str(obj.get("event") or "") != "EXECUTION_TRACE":
            continue
        ts_raw = obj.get("ts_utc") or obj.get("timestamp")
        if not isinstance(ts_raw, str):
            continue
        dt = parse_ts(ts_raw)
        if dt is None or dt < day_start or dt >= day_end:
            continue
        inst = str(obj.get("instrument") or "").strip()
        if inst_f and inst.upper() != inst_f:
            continue
        src = str(obj.get("source") or "")
        stage = str(obj.get("call_stage") or "")
        rows.append((dt, inst, src, stage))
    rows.sort(key=lambda x: x[0])
    return rows


def trace_stats_in_window(
    trace_rows: List[Tuple[datetime, str, str, str]], t0: datetime, t1: datetime
) -> Tuple[int, int, int, int]:
    """on_exec, on_order, max_per_10ms all raw_callback, onexec_max_10ms_bucket."""
    ts_only = [r[0] for r in trace_rows]
    lo = bisect.bisect_left(ts_only, t0)
    hi = bisect.bisect_right(ts_only, t1)
    times_ms: List[int] = []
    exec_update = 0
    order_update = 0
    exec_ms: List[int] = []
    for i in range(lo, hi):
        _dt, _inst, src, stage = trace_rows[i]
        if stage != "raw_callback":
            continue
        ms = int(trace_rows[i][0].timestamp() * 1000)
        times_ms.append(ms)
        if "OnExecutionUpdate" in src:
            exec_update += 1
            exec_ms.append(ms)
        elif "OnOrderUpdate" in src:
            order_update += 1
    max_bucket = max_per_10ms_bucket(times_ms)
    onexec_burst = max_per_10ms_bucket(exec_ms)
    return exec_update, order_update, max_bucket, onexec_burst


def max_per_10ms_bucket(times_ms: List[int]) -> int:
    if not times_ms:
        return 0
    times_ms.sort()
    best = 0
    j = 0
    for i, t in enumerate(times_ms):
        while j < len(times_ms) and times_ms[j] <= t + 10:
            j += 1
        best = max(best, j - i)
    return best


def dominant_bucket(recon: float, stream: float, coord: float, lock: float) -> str:
    total = recon + stream + coord + lock
    if total <= 0:
        return "none"
    m = max(recon, stream, coord, lock)
    if m == recon:
        return "recon"
    if m == stream:
        return "stream"
    if m == coord:
        return "coord"
    return "lock"


def classify_spike(ws: WindowStats, onexec_burst: int) -> Tuple[str, str]:
    """
    Ordered rules (spec): RECONCILIATION_LOOP, EXECUTION_CALLBACK_STORM, STREAM_LOGIC_CHURN,
    THREAD_CONTENTION, UNKNOWN.
    """
    r, s, c, lk = ws.avg_recon, ws.avg_stream, ws.avg_coord, ws.avg_lock
    total = r + s + c + lk
    cycles = ws.cycles

    lock_high = lk >= 25 or (total > 0 and lk / total >= 0.35)
    recon_dom = total > 0 and r >= max(s, c, lk) * 1.05 and r / total >= 0.30
    stream_dom = total > 0 and s >= max(r, c, lk) * 1.05 and s / total >= 0.30
    recon_low = total > 0 and r / total < 0.22 and cycles < 18

    trace_dense = (ws.on_exec_update + ws.on_order_update) >= 80 or ws.max_per_10ms_trace >= 15
    callback_storm = onexec_burst > 5 or (trace_dense and recon_low)

    if recon_dom and cycles > 20:
        return (
            "RECONCILIATION_LOOP",
            f"reconciliation_bucket dominant ({r:.1f} ms avg) with cycles={cycles}",
        )

    if callback_storm:
        return (
            "EXECUTION_CALLBACK_STORM",
            f"high callback density (OnExec burst max/10ms={onexec_burst}, trace_max_10ms={ws.max_per_10ms_trace}) "
            f"with low recon share or few gate cycles",
        )

    if stream_dom and recon_low and not callback_storm:
        moderate_cb = 5 <= (ws.on_exec_update + ws.on_order_update) < 80
        if moderate_cb or ws.exec_trace_robot > 0:
            return (
                "STREAM_LOGIC_CHURN",
                f"stream_tick dominant ({s:.1f} ms avg), reconciliation relatively low",
            )

    if lock_high:
        return (
            "THREAD_CONTENTION",
            f"ENGINE_LOCK_REGION high ({lk:.1f} ms avg, {100 * lk / total:.0f}% of buckets)" if total > 0 else f"ENGINE_LOCK_REGION high ({lk:.1f} ms)",
        )

    dom = dominant_bucket(r, s, c, lk)
    return (
        "UNKNOWN",
        f"no single bucket matched (dominant={dom}, cycles={cycles}, trace_lines={ws.on_exec_update + ws.on_order_update})",
    )


def format_time(dt: datetime, tz_name: str) -> str:
    if tz_name == "utc":
        return dt.astimezone(timezone.utc).strftime("%H:%M:%S")
    if ZoneInfo is None:
        return dt.strftime("%H:%M:%S")
    return dt.astimezone(ZoneInfo(CHICAGO)).strftime("%H:%M:%S")


def main() -> int:
    ap = argparse.ArgumentParser(description="Diagnose CPU spikes via ENGINE_CPU_PROFILE + activity.")
    ap.add_argument("--date", required=True, help="YYYY-MM-DD")
    ap.add_argument("--tz", choices=("chicago", "utc"), default="chicago")
    ap.add_argument("--instrument", default="", help="Optional filter (e.g. MNQ)")
    ap.add_argument("--window-seconds", type=float, default=10.0)
    ap.add_argument("--log-dir", default=None)
    ap.add_argument("--only-spikes-above", type=float, default=None, help="Keep spikes with peak cpu_pct >= N (subsystem proxy)")
    args = ap.parse_args()

    try:
        d = date.fromisoformat(args.date)
    except ValueError:
        print("ERROR: --date must be YYYY-MM-DD", file=sys.stderr)
        return 2

    log_dir = resolve_log_dir(args.log_dir)
    day_start, day_end = day_window_utc(d, args.tz)
    paths = collect_robot_jsonl_paths(log_dir)

    stats: Dict[str, Any] = {}
    all_cpu, counters = ingest_robot_day(paths, day_start, day_end, args.instrument or None, stats)
    exec_trace_path = log_dir / "execution_trace.jsonl"
    trace_rows = load_execution_trace_day(
        exec_trace_path, day_start, day_end, args.instrument or None
    )

    half = timedelta(seconds=args.window_seconds / 2.0)

    reports: List[Tuple[WindowStats, int, str, str]] = []
    classify_counts: Dict[str, int] = defaultdict(int)

    for anchor in all_cpu:
        t0 = anchor.ts_utc - half
        t1 = anchor.ts_utc + half
        ws = aggregate_cpu_in_window(all_cpu, t0, t1, anchor)
        cyc, thr, hs, dp, do, et = count_counters_in_window(counters, t0, t1)
        ws.cycles = cyc
        ws.throttled = thr
        ws.hard_stop = hs
        ws.dedup_perm = dp
        ws.dedup_order = do
        ws.exec_trace_robot = et

        oe, oo, mx, onexec_burst = trace_stats_in_window(trace_rows, t0, t1)
        ws.on_exec_update = oe
        ws.on_order_update = oo
        ws.max_per_10ms_trace = mx

        cls, reason = classify_spike(ws, onexec_burst)

        if args.only_spikes_above is not None:
            peak = ws.peak_cpu_pct
            if peak is None or peak < args.only_spikes_above:
                continue

        reports.append((ws, onexec_burst, cls, reason))
        classify_counts[cls] += 1

    # Print report
    print(f"log_dir: {log_dir.resolve()}")
    print(f"day: {args.date} ({args.tz} calendar window -> UTC [{day_start.isoformat()}, {day_end.isoformat()}))")
    print(f"files_scanned: {stats.get('files_scanned', 0)}")
    print(f"ENGINE_CPU_PROFILE spike anchors: {len(all_cpu)}")
    print(f"reported spikes (after filters): {len(reports)}")
    print()

    for ws, onexec_burst, cls, reason in reports:
        a = ws.anchor
        ts_label = format_time(a.ts_utc, args.tz)
        pct_note = ""
        if ws.avg_cpu_pct is not None:
            pct_note = f"avg_cpu: {ws.avg_cpu_pct:.1f}%  peak_cpu: {ws.peak_cpu_pct:.1f}%"
        else:
            pct_note = "avg_cpu: n/a  peak_cpu: n/a"

        print(f"SPIKE @ {ts_label}  instrument={a.instrument or '?'}")

        print(f"  {pct_note}  (subsystem proxy from sum_subsystem_ms/wall)")

        print("  CPU breakdown (avg ms in window):")

        print(f"    reconciliation: {ws.avg_recon:.1f} ms")
        print(f"    stream_tick: {ws.avg_stream:.1f} ms")
        print(f"    coordinators: {ws.avg_coord:.1f} ms")
        print(f"    lock: {ws.avg_lock:.1f} ms")

        print("  event_counts:")
        print(f"    cycles (STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT): {ws.cycles}")
        print(f"    throttled: {ws.throttled}")
        print(f"    hard_stop: {ws.hard_stop}")
        print(f"    EXECUTION_DEDUP_SKIPPED_PERMANENT: {ws.dedup_perm}")
        print(f"    ORDER_UPDATE_DEDUP_SKIPPED: {ws.dedup_order}")
        print(f"    EXECUTION_TRACE (robot jsonl): {ws.exec_trace_robot}")

        print("  execution_activity (execution_trace.jsonl):")
        print(f"    OnExecutionUpdate raw_callback: {ws.on_exec_update}")
        print(f"    OnOrderUpdate raw_callback: {ws.on_order_update}")
        print(f"    max_events_per_10ms (all EXECUTION_TRACE raw_callback): {ws.max_per_10ms_trace}")
        print(f"    OnExecutionUpdate max/10ms_bucket: {onexec_burst}")

        print(f"  CLASSIFICATION: {cls}")
        print(f"  reason: {reason}")
        print()

    print("--- SUMMARY ---")
    print(f"TOTAL_SPIKES: {len(reports)}")
    print("By classification:")
    for label in (
        "RECONCILIATION_LOOP",
        "EXECUTION_CALLBACK_STORM",
        "STREAM_LOGIC_CHURN",
        "THREAD_CONTENTION",
        "UNKNOWN",
    ):
        print(f"  {label}: {classify_counts.get(label, 0)}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
