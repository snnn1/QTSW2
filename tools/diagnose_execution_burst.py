#!/usr/bin/env python3
"""
Execution-burst root-cause diagnostic: read robot JSONL (and Robot.Core for ENGINE_CPU_PROFILE fields).

Default calendar day is CME trading date (America/Chicago, roll at 18:00 local).

Recommended:
  python tools/diagnose_execution_burst.py --auto-cme-date --window-seconds 60
"""

from __future__ import annotations

import argparse
import bisect
import json
import sys
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from collections import defaultdict
from typing import Any, Dict, Iterable, List, Optional, Tuple

try:
    from zoneinfo import ZoneInfo
except Exception:  # pragma: no cover
    ZoneInfo = None  # type: ignore

from log_audit import NormEvent, normalize_event, resolve_log_dir

CHICAGO = "America/Chicago"


def resolve_cme_trading_date(now_utc: datetime) -> date:
    """CME-style session date: Chicago local; if hour >= 18:00, trading date is next calendar day."""
    if ZoneInfo is None:
        return now_utc.date()
    z = ZoneInfo(CHICAGO)
    local = now_utc.astimezone(z)
    if local.hour >= 18:
        return local.date() + timedelta(days=1)
    return local.date()


# Policy mirror (MismatchEscalationPolicy.GATE_HARD_STOP_NO_PROGRESS_ITERATIONS)
GATE_HARD_STOP_NO_PROGRESS_ITERATIONS = 5

BURST_ANCHOR_EVENTS = frozenset({"ENTRY_FILLED", "EXECUTION_FILLED", "ORDER_FILLED"})

CONTROL_EVENTS = {
    "RECONCILIATION_EXECUTION_CAP_REACHED",
    "RECONCILIATION_HARD_STOPPED",
    "RECONCILIATION_REENTRY_BLOCKED",
    "RECONCILIATION_THROTTLED",
    "RECONCILIATION_THROTTLE_PRESERVED",
}

GATE_RESULT_EVENT = "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT"
CPU_EVENT = "ENGINE_CPU_PROFILE"

# Timeline: control-relevant events only (plus anchor types already in stream)
TIMELINE_EXTRA_EVENTS = frozenset(
    {
        "RECONCILIATION_THROTTLE_BACKOFF_UPDATED",
        "RECONCILIATION_PROGRESS_OBSERVED",
        "RECONCILIATION_NO_PROGRESS_DETECTED",
        "STATE_CONSISTENCY_GATE_ENGAGED",
        "STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED",
    }
)


def severity_label(score: int) -> str:
    if score >= 90:
        return "CRITICAL"
    if score >= 70:
        return "HIGH"
    if score >= 40:
        return "MEDIUM"
    if score >= 20:
        return "LOW"
    return "MINIMAL"


def _offset_ms(anchor: datetime, t: datetime) -> int:
    return int((t - anchor).total_seconds() * 1000.0)


def _gate_result_line(d: Dict[str, Any], _cycle_index: int) -> str:
    ec = _intish(_get_field(d, "execution_cycle_count", "ExecutionCycleCount"), 0)
    expensive = _boolish(_get_field(d, "expensive_invoked", "ExpensiveInvoked"))
    throttled = _boolish(_get_field(d, "gate_reconciliation_throttled", "GateReconciliationThrottled"))
    suppressed = _boolish(_get_field(d, "throttle_suppressed_expensive", "ThrottleSuppressedExpensive"))
    skip = _get_field(d, "skip_reason", "SkipReason")
    parts = [f"cycle {ec}"]
    if throttled or suppressed:
        parts.append("skip (throttled)")
    elif expensive:
        parts.append("(expensive)")
    else:
        parts.append("(cheap)")
    if skip and isinstance(skip, str) and skip.strip():
        parts.append(f"skip_reason={skip.strip()}")
    return " ".join(parts)


def _event_signature(e: NormEvent) -> str:
    ev = (e.event or "").strip()
    if ev == GATE_RESULT_EVENT:
        d = e.data if isinstance(e.data, dict) else {}
        return f"{GATE_RESULT_EVENT}|{_gate_result_line(d, 0)}"
    return ev


def _timeline_label(e: NormEvent, cycle_index: int) -> str:
    ev = (e.event or "").strip()
    if ev == GATE_RESULT_EVENT:
        d = e.data if isinstance(e.data, dict) else {}
        return _gate_result_line(d, cycle_index)
    if ev in CONTROL_EVENTS:
        return ev.lower().replace("reconciliation_", "")
    if ev in TIMELINE_EXTRA_EVENTS:
        return ev
    if ev in BURST_ANCHOR_EVENTS:
        return ev
    return ev or "?"


def _collect_timeline_events(
    events: List[NormEvent],
    event_times: List[datetime],
    burst_start: datetime,
    burst_end: datetime,
    instrument: str,
) -> List[NormEvent]:
    inst_u = instrument.upper()
    lo = bisect.bisect_left(event_times, burst_start)
    hi = bisect.bisect_left(event_times, burst_end)
    out: List[NormEvent] = []
    for e in events[lo:hi]:
        if _event_instrument(e).upper() != inst_u:
            continue
        ev = (e.event or "").strip()
        if ev not in BURST_ANCHOR_EVENTS | {GATE_RESULT_EVENT} | CONTROL_EVENTS | TIMELINE_EXTRA_EVENTS:
            continue
        out.append(e)
    out.sort(key=lambda x: (x.ts_utc, x.event or ""))
    return out


def build_control_timeline(
    anchor_ts: datetime,
    anchor_event: str,
    timeline_events: List[NormEvent],
) -> List[str]:
    lines: List[str] = []
    lines.append(f"[+0ms] {anchor_event}")
    cycle_i = 0
    events = sorted(
        [e for e in timeline_events if e.ts_utc > anchor_ts],
        key=lambda e: (e.ts_utc, e.event or ""),
    )
    i = 0
    while i < len(events):
        sig = _event_signature(events[i])
        start = i
        j = i + 1
        while j < len(events):
            if _event_signature(events[j]) != sig:
                break
            gap_ms = (events[j].ts_utc - events[j - 1].ts_utc).total_seconds() * 1000.0
            if gap_ms >= 10:
                break
            j += 1
        run_len = j - start
        if run_len > 10:
            off0 = _offset_ms(anchor_ts, events[start].ts_utc)
            dur = (events[j - 1].ts_utc - events[start].ts_utc).total_seconds() * 1000.0
            lines.append(
                f"[+{off0}ms] LOOP DETECTED (event={sig[:160]}, repeats={run_len}, duration={dur:.0f}ms)"
            )
        for k in range(start, j):
            e = events[k]
            ev = (e.event or "").strip()
            off = _offset_ms(anchor_ts, e.ts_utc)
            if ev == GATE_RESULT_EVENT:
                cycle_i += 1
                d = e.data if isinstance(e.data, dict) else {}
                desc = _gate_result_line(d, cycle_i)
                lines.append(f"[+{off}ms] {desc}")
            else:
                desc = _timeline_label(e, cycle_i)
                lines.append(f"[+{off}ms] {desc}")
        i = j
    return lines


@dataclass
class FirstViolation:
    vtype: str
    ts_utc: datetime
    cycle: Optional[int]
    offset_ms: int


def find_first_violation(
    control_failure_type: str,
    anchor_ts: datetime,
    timeline_events: List[NormEvent],
) -> Optional[FirstViolation]:
    if control_failure_type in ("HEALTHY", "NOT_CLASSIFIED"):
        return None

    throttle_seen = False
    cycle_seq = 0
    noise_seq = 0
    hard_stop_seen = False

    for e in sorted(timeline_events, key=lambda x: (x.ts_utc, x.event or "")):
        ev = (e.event or "").strip()
        off = _offset_ms(anchor_ts, e.ts_utc)
        if ev == "RECONCILIATION_THROTTLED":
            throttle_seen = True
        if ev == "RECONCILIATION_HARD_STOPPED":
            hard_stop_seen = True

        if ev != GATE_RESULT_EVENT:
            continue

        d = e.data if isinstance(e.data, dict) else {}
        ec = _intish(_get_field(d, "execution_cycle_count", "ExecutionCycleCount"), 0)
        te = _intish(
            _get_field(d, "total_expensive_since_gate_engaged", "TotalExpensiveSinceGateEngaged"),
            0,
        )
        npi = _intish(_get_field(d, "no_progress_iterations", "NoProgressIterations"), 0)
        is_noise = is_non_gate_noise_result(d)

        if control_failure_type in ("EXTERNAL_EXECUTION_LOOP", "EXECUTION_LOOP_CONFIRMED"):
            if not is_noise:
                continue
            noise_seq += 1
            if noise_seq == 21 and ec == 0:
                return FirstViolation("EXTERNAL_EXECUTION_LOOP", e.ts_utc, noise_seq, off)
            continue

        if is_noise:
            continue

        cycle_seq += 1

        if control_failure_type == "EXECUTION_CAP_BREACHED" and ec > 5:
            return FirstViolation("EXECUTION_CAP_BREACHED", e.ts_utc, ec, off)
        if control_failure_type == "ABSOLUTE_CAP_BREACHED" and te > 10:
            return FirstViolation("ABSOLUTE_CAP_BREACHED", e.ts_utc, ec, off)
        if control_failure_type == "THROTTLE_NOT_ENGAGED" and cycle_seq == 51 and not throttle_seen:
            return FirstViolation("THROTTLE_NOT_ENGAGED", e.ts_utc, ec, off)
        if (
            control_failure_type == "HARD_STOP_NOT_TRIGGERED"
            and npi >= GATE_HARD_STOP_NO_PROGRESS_ITERATIONS
            and not hard_stop_seen
        ):
            return FirstViolation("HARD_STOP_NOT_TRIGGERED", e.ts_utc, ec, off)
        if (
            control_failure_type == "CONTROL_INSUFFICIENT"
            and cycle_seq == 51
            and caps_respected_from_payload(d)
        ):
            return FirstViolation("CONTROL_INSUFFICIENT", e.ts_utc, ec, off)

    return None


def caps_respected_from_payload(d: Dict[str, Any]) -> bool:
    ec = _intish(_get_field(d, "execution_cycle_count", "ExecutionCycleCount"), 0)
    te = _intish(
        _get_field(d, "total_expensive_since_gate_engaged", "TotalExpensiveSinceGateEngaged"),
        0,
    )
    return ec <= 5 and te <= 10


def format_first_violation(fv: Optional[FirstViolation]) -> str:
    if fv is None:
        return "none"
    c = fv.cycle if fv.cycle is not None else "?"
    return f"cycle {c} @ +{fv.offset_ms}ms"


def status_from_classification(control_failure_type: str, legacy_fail: bool, legacy_warn: bool) -> str:
    if control_failure_type in (
        "EXECUTION_CAP_BREACHED",
        "ABSOLUTE_CAP_BREACHED",
        "THROTTLE_NOT_ENGAGED",
        "CONTROL_INSUFFICIENT",
    ):
        return "FAIL"
    if control_failure_type == "EXECUTION_LOOP_CONFIRMED":
        return "FAIL"
    if control_failure_type == "EXTERNAL_EXECUTION_LOOP":
        return "WARNING"
    if control_failure_type == "HARD_STOP_NOT_TRIGGERED":
        return "WARNING"
    if control_failure_type == "HEALTHY":
        return "PASS"
    if legacy_fail:
        return "FAIL"
    if legacy_warn:
        return "WARNING"
    return "PASS"


def reason_from_classification(control_failure_type: str, legacy_reason: str) -> str:
    msg = {
        "EXECUTION_CAP_BREACHED": "execution cap not enforced -> runaway reconciliation risk",
        "ABSOLUTE_CAP_BREACHED": "absolute expensive cap not enforced",
        "EXTERNAL_EXECUTION_LOOP": (
            "reconciliation cycles without gate participation -> likely execution/update loop or telemetry mismatch"
        ),
        "THROTTLE_NOT_ENGAGED": "throttle never engaged despite high cycle count",
        "HARD_STOP_NOT_TRIGGERED": "hard stop not observed while no-progress threshold reached",
        "CONTROL_INSUFFICIENT": "caps/throttle engaged but cycle load remains high",
        "HEALTHY": "controls engaged within expected envelope",
        "EXECUTION_LOOP_CONFIRMED": "Execution/update loop confirmed as primary CPU source",
        "NOT_CLASSIFIED": "pattern does not match a single root-cause bucket",
    }.get(control_failure_type, legacy_reason)
    if control_failure_type == "NOT_CLASSIFIED" and legacy_reason and legacy_reason != "all checks ok":
        return f"{msg} ({legacy_reason})"
    return msg


def refine_with_cpu(base_failure: str, peak_cpu: Optional[float]) -> str:
    if base_failure == "EXTERNAL_EXECUTION_LOOP" and peak_cpu is not None and peak_cpu >= 70:
        return "EXECUTION_LOOP_CONFIRMED"
    return base_failure


def final_reason_text(cft: str, peak_cpu: Optional[float], legacy_reason: str) -> str:
    if cft == "EXECUTION_LOOP_CONFIRMED":
        return reason_from_classification("EXECUTION_LOOP_CONFIRMED", legacy_reason)
    if cft == "EXTERNAL_EXECUTION_LOOP" and peak_cpu is not None and peak_cpu >= 40:
        return "High reconciliation activity without gate participation -> execution/update loop likely"
    return reason_from_classification(cft, legacy_reason)


def peak_burst_cpu(m: BurstMetrics) -> Optional[float]:
    if m.cpu_process_pcts:
        return max(m.cpu_process_pcts)
    if m.cpu_utils:
        return max(m.cpu_utils)
    return None


def avg_burst_cpu(m: BurstMetrics) -> Optional[float]:
    if m.cpu_process_pcts:
        return sum(m.cpu_process_pcts) / len(m.cpu_process_pcts)
    if m.cpu_utils:
        return sum(m.cpu_utils) / len(m.cpu_utils)
    return None


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
    paths: List[Path] = []
    if (log_dir / "robot_ENGINE.jsonl").is_file():
        paths.append(log_dir / "robot_ENGINE.jsonl")
    for p in sorted(log_dir.glob("robot_ENGINE_*.jsonl")):
        if p not in paths:
            paths.append(p)
    arch = log_dir / "archive"
    if arch.is_dir():
        for p in sorted(arch.glob("robot_ENGINE*.jsonl")):
            paths.append(p)
    for p in sorted(log_dir.glob("robot_*.jsonl")):
        if p.name.startswith("robot_") and p not in paths:
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
    except Exception:
        return


def ingest_jsonl_file(
    path: Path,
    day_start: datetime,
    day_end: datetime,
    stats: Dict[str, Any],
) -> List[NormEvent]:
    out: List[NormEvent] = []
    for line_no, line in iter_jsonl(path):
        if not line:
            continue
        try:
            obj = json.loads(line)
            if not isinstance(obj, dict):
                stats["parse_errors_count"] = stats.get("parse_errors_count", 0) + 1
                continue
        except Exception:
            stats["parse_errors_count"] = stats.get("parse_errors_count", 0) + 1
            continue
        ev = normalize_event(obj, path.name)
        if ev is None:
            stats["parse_errors_count"] = stats.get("parse_errors_count", 0) + 1
            continue
        if ev.ts_utc < day_start or ev.ts_utc >= day_end:
            continue
        out.append(ev)
        stats["lines_read"] = stats.get("lines_read", 0) + 1
    stats["files_read"] = stats.get("files_read", 0) + 1
    return out


def _norm_instrument(s: str) -> str:
    return (s or "").strip()


def _event_instrument(e: NormEvent) -> str:
    inst = _norm_instrument(e.instrument)
    if inst:
        return inst
    d = e.data if isinstance(e.data, dict) else {}
    return _norm_instrument(str(d.get("instrument") or ""))


def _get_field(d: Dict[str, Any], *names: str) -> Any:
    for n in names:
        if n in d and d[n] is not None:
            return d[n]
    return None


def _boolish(v: Any) -> bool:
    if isinstance(v, bool):
        return v
    if isinstance(v, (int, float)):
        return v != 0
    if isinstance(v, str):
        return v.lower() in ("true", "1", "yes")
    return False


def is_non_gate_noise_result(d: Dict[str, Any]) -> bool:
    """
    cycle-0 RESULT lines with no skip/throttle fields — excluded from gate cap/throttle validation;
    counted for EXTERNAL_EXECUTION_LOOP only.
    """
    ec = _intish(_get_field(d, "execution_cycle_count", "ExecutionCycleCount"), 0)
    if ec != 0:
        return False
    skip = _get_field(d, "skip_reason", "SkipReason")
    if skip is not None and str(skip).strip():
        return False
    if _boolish(_get_field(d, "gate_reconciliation_throttled", "GateReconciliationThrottled")):
        return False
    if _boolish(_get_field(d, "throttle_suppressed_expensive", "ThrottleSuppressedExpensive")):
        return False
    return True


def _intish(v: Any, default: int = 0) -> int:
    if v is None:
        return default
    if isinstance(v, bool):
        return int(v)
    if isinstance(v, int):
        return v
    if isinstance(v, float):
        return int(v)
    try:
        return int(str(v).strip())
    except Exception:
        return default


def _floatish(v: Any) -> Optional[float]:
    if v is None:
        return None
    if isinstance(v, (int, float)):
        return float(v)
    try:
        return float(str(v).strip())
    except Exception:
        return None


def _cpu_utilization_pct(data: Dict[str, Any]) -> Optional[float]:
    """Engine subsystem proxy (0..100) when process_cpu_pct is absent."""
    wall = _floatish(_get_field(data, "wall_window_ms", "wallWindowMs"))
    summed = _get_field(data, "sum_subsystem_ms", "sumSubsystemMs")
    if isinstance(summed, (int, float)) and wall and wall > 0:
        return max(0.0, min(100.0, 100.0 * float(summed) / wall))
    return None


def _process_cpu_pct(data: Dict[str, Any]) -> Optional[float]:
    v = _floatish(_get_field(data, "process_cpu_pct", "processCpuPct"))
    if v is None:
        return None
    return max(0.0, min(100.0, float(v)))


@dataclass
class BurstMetrics:
    anchor_ts: datetime
    anchor_event: str
    instrument: str
    gate_reconciliation_cycles: int = 0
    non_gate_reconciliation_cycles: int = 0
    total_expensive_invoked: int = 0
    total_throttled_cycles: int = 0
    max_execution_cycle_count: int = 0
    max_total_expensive_since_gate_engaged: int = 0
    max_no_progress_iterations: int = 0
    counts: Dict[str, int] = field(default_factory=dict)
    cap_triggered: bool = False
    hard_stop_triggered: bool = False
    throttle_signal: bool = False
    cpu_utils: List[float] = field(default_factory=list)
    cpu_process_pcts: List[float] = field(default_factory=list)


def caps_respected(m: BurstMetrics) -> bool:
    return m.max_execution_cycle_count <= 5 and m.max_total_expensive_since_gate_engaged <= 10


def classify_control_failure(m: BurstMetrics) -> str:
    """
    Single primary label; order matches strict precedence (first match wins).

    EXTERNAL_EXECUTION_LOOP: many STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT lines but
    execution_cycle_count never > 0 and no throttle — load outside gate participation
    (execution/update loop, telemetry mismatch, or non-gate reconciliation spam).
    """
    if m.max_execution_cycle_count > 5:
        return "EXECUTION_CAP_BREACHED"
    if m.max_total_expensive_since_gate_engaged > 10:
        return "ABSOLUTE_CAP_BREACHED"
    if (
        m.non_gate_reconciliation_cycles > 20
        and m.max_execution_cycle_count == 0
        and m.total_throttled_cycles == 0
    ):
        return "EXTERNAL_EXECUTION_LOOP"
    if m.gate_reconciliation_cycles > 50 and m.total_throttled_cycles == 0:
        return "THROTTLE_NOT_ENGAGED"
    if (
        m.max_no_progress_iterations >= GATE_HARD_STOP_NO_PROGRESS_ITERATIONS
        and m.counts.get("RECONCILIATION_HARD_STOPPED", 0) == 0
    ):
        return "HARD_STOP_NOT_TRIGGERED"
    if (
        caps_respected(m)
        and m.total_throttled_cycles > 0
        and m.gate_reconciliation_cycles > 50
    ):
        return "CONTROL_INSUFFICIENT"
    if (
        m.gate_reconciliation_cycles <= 20
        and caps_respected(m)
        and m.total_throttled_cycles > 0
    ):
        return "HEALTHY"
    return "NOT_CLASSIFIED"


def severity_score(m: BurstMetrics) -> int:
    raw = (
        m.gate_reconciliation_cycles
        + m.non_gate_reconciliation_cycles
        + m.total_expensive_invoked * 2
        + m.max_no_progress_iterations * 3
    )
    return min(100, raw)


def _load_day_events(log_dir: Path, audit_date: date, tz_name: str) -> List[NormEvent]:
    day_start, day_end = day_window_utc(audit_date, tz_name)
    stats: Dict[str, Any] = {}
    raw: List[NormEvent] = []
    for path in collect_robot_jsonl_paths(log_dir):
        raw.extend(ingest_jsonl_file(path, day_start, day_end, stats))
    raw.sort(key=lambda e: e.ts_utc)
    return raw


def _instrument_matches(filter_inst: Optional[str], inst: str) -> bool:
    if not filter_inst:
        return True
    return inst.upper() == filter_inst.strip().upper()


def _collect_bursts(
    events: List[NormEvent],
    window: timedelta,
    filter_inst: Optional[str],
) -> List[Tuple[datetime, str, str]]:
    """Returns list of (burst_start, anchor_event, instrument)."""
    out: List[Tuple[datetime, str, str]] = []
    for e in events:
        ev = (e.event or "").strip()
        if ev not in BURST_ANCHOR_EVENTS:
            continue
        inst = _event_instrument(e)
        if not _instrument_matches(filter_inst, inst):
            continue
        out.append((e.ts_utc, ev, inst))
    return out


def _apply_event_to_burst_metrics(m: BurstMetrics, e: NormEvent) -> None:
    ev = (e.event or "").strip()
    inst_u = m.instrument.upper()
    if _event_instrument(e).upper() != inst_u:
        return

    if ev == GATE_RESULT_EVENT:
        d = e.data if isinstance(e.data, dict) else {}
        if is_non_gate_noise_result(d):
            m.non_gate_reconciliation_cycles += 1
            return
        m.gate_reconciliation_cycles += 1
        if _boolish(_get_field(d, "expensive_invoked", "ExpensiveInvoked")):
            m.total_expensive_invoked += 1
        ec = _intish(_get_field(d, "execution_cycle_count", "ExecutionCycleCount"), 0)
        te = _intish(
            _get_field(d, "total_expensive_since_gate_engaged", "TotalExpensiveSinceGateEngaged"),
            0,
        )
        npi = _intish(_get_field(d, "no_progress_iterations", "NoProgressIterations"), 0)
        m.max_execution_cycle_count = max(m.max_execution_cycle_count, ec)
        m.max_total_expensive_since_gate_engaged = max(m.max_total_expensive_since_gate_engaged, te)
        m.max_no_progress_iterations = max(m.max_no_progress_iterations, npi)
        return

    if ev in CONTROL_EVENTS:
        m.counts[ev] = m.counts.get(ev, 0) + 1
        if ev == "RECONCILIATION_THROTTLED":
            m.total_throttled_cycles += 1
        if ev == "RECONCILIATION_EXECUTION_CAP_REACHED":
            m.cap_triggered = True
        if ev == "RECONCILIATION_HARD_STOPPED":
            m.hard_stop_triggered = True
        if ev in ("RECONCILIATION_THROTTLED", "RECONCILIATION_THROTTLE_PRESERVED"):
            m.throttle_signal = True


def _analyze_bursts_one_pass(
    events: List[NormEvent],
    burst_specs: List[Tuple[datetime, str, str]],
    window: timedelta,
) -> List[BurstMetrics]:
    """
    Single chronological pass: each event updates burst windows that contain it.
    Active bursts are tracked per instrument so we do not scan all overlapping windows
    on every event when fill volume is high.
    """
    metrics: List[BurstMetrics] = []
    jobs: List[Tuple[datetime, datetime, int]] = []
    for i, (bs, anchor_ev, inst) in enumerate(burst_specs):
        be = bs + window
        metrics.append(
            BurstMetrics(anchor_ts=bs, anchor_event=anchor_ev, instrument=inst),
        )
        jobs.append((bs, be, i))
    jobs.sort(key=lambda x: x[0])

    active_by_inst: Dict[str, List[int]] = defaultdict(list)
    job_ptr = 0
    for e in events:
        t = e.ts_utc
        while job_ptr < len(jobs) and jobs[job_ptr][0] <= t:
            _, _, bi = jobs[job_ptr]
            inst_key = burst_specs[bi][2].strip().upper()
            active_by_inst[inst_key].append(job_ptr)
            job_ptr += 1

        for ik in list(active_by_inst.keys()):
            active_by_inst[ik] = [j for j in active_by_inst[ik] if t < jobs[j][1]]
            if not active_by_inst[ik]:
                del active_by_inst[ik]

        ev = (e.event or "").strip()
        if ev == CPU_EVENT:
            dcpu = e.data if isinstance(e.data, dict) else {}
            pp = _process_cpu_pct(dcpu)
            if pp is not None:
                for jlist in active_by_inst.values():
                    for j in jlist:
                        burst_i = jobs[j][2]
                        metrics[burst_i].cpu_process_pcts.append(pp)
                continue
            u = _cpu_utilization_pct(dcpu)
            if u is None:
                continue
            for jlist in active_by_inst.values():
                for j in jlist:
                    burst_i = jobs[j][2]
                    metrics[burst_i].cpu_utils.append(u)
            continue

        ei = _event_instrument(e).strip().upper()
        if not ei:
            continue
        for j in active_by_inst.get(ei, []):
            burst_i = jobs[j][2]
            _apply_event_to_burst_metrics(metrics[burst_i], e)

    return metrics


def _evaluate(m: BurstMetrics) -> Tuple[str, str, List[str]]:
    """
    Returns (status, reason, detail_lines).
    status: PASS | WARNING | FAIL
    """
    fails: List[str] = []
    warns: List[str] = []

    if m.max_execution_cycle_count > 5:
        fails.append("A: max_execution_cycle_count>5")
    if m.max_total_expensive_since_gate_engaged > 10:
        fails.append("B: max_total_expensive_since_gate_engaged>10")
    if m.gate_reconciliation_cycles > 20 and m.total_throttled_cycles == 0:
        fails.append("C: cycles>20 but no RECONCILIATION_THROTTLED")
    if m.gate_reconciliation_cycles > 200 and m.total_throttled_cycles == 0:
        fails.append("E: cycles>200 and throttled==0 (unbounded loop signal)")

    if (
        m.max_no_progress_iterations >= GATE_HARD_STOP_NO_PROGRESS_ITERATIONS
        and m.counts.get("RECONCILIATION_HARD_STOPPED", 0) == 0
    ):
        warns.append(
            f"D: no_progress_iterations>={GATE_HARD_STOP_NO_PROGRESS_ITERATIONS} but no RECONCILIATION_HARD_STOPPED"
        )

    if fails:
        return "FAIL", "; ".join(fails), fails + warns
    if warns:
        return "WARNING", "; ".join(warns), warns
    return "PASS", "all checks ok", []


def _format_time(dt: datetime) -> str:
    return dt.astimezone(timezone.utc).strftime("%Y-%m-%d %H:%M:%S.%f")[:-3] + "Z"


def _format_burst_header(ts: datetime, instrument: str, tz_name: str) -> str:
    if tz_name == "chicago" and ZoneInfo is not None:
        z = ZoneInfo(CHICAGO)
        t = ts.astimezone(z)
        return f"BURST @ {t.strftime('%H:%M:%S')} {instrument} ({t.strftime('%Y-%m-%d')} {CHICAGO})"
    t = ts.astimezone(timezone.utc)
    return f"BURST @ {t.strftime('%H:%M:%S')} {instrument} ({t.strftime('%Y-%m-%d')} UTC)"


def main() -> int:
    ap = argparse.ArgumentParser(description="Execution burst reconciliation diagnostic (logs only)")
    ap.add_argument(
        "--date",
        default=None,
        help="Calendar date YYYY-MM-DD (default: CME trading date from now UTC)",
    )
    ap.add_argument(
        "--auto-cme-date",
        action="store_true",
        help="Resolve CME trading date from current time (Chicago; roll at 18:00 local); overrides --date",
    )
    ap.add_argument("--instrument", default=None, help="Only bursts for this instrument (optional)")
    ap.add_argument("--window-seconds", type=int, default=30, help="Burst window length (default 30)")
    ap.add_argument("--tz", default="chicago", choices=["chicago", "utc"], help="Day boundary for log ingest")
    ap.add_argument(
        "--log-dir",
        default=None,
        help="Robot JSONL directory (default: QTSW2_LOG_DIR, QTSW2_PROJECT_ROOT/logs/robot, or ./logs/robot)",
    )
    ap.add_argument(
        "--max-timeline-lines",
        type=int,
        default=0,
        help="Cap CONTROL TIMELINE lines after the [+0ms] anchor (0 = no cap)",
    )
    ap.add_argument(
        "--allow-missing-cpu-profile",
        action="store_true",
        help="Do not fail when no ENGINE_CPU_PROFILE rows exist for the ingested day",
    )
    args = ap.parse_args()

    print("Recommended usage:")
    print("  python tools/diagnose_execution_burst.py --auto-cme-date --window-seconds 60")
    print("")

    now_utc = datetime.now(timezone.utc)
    use_cme = bool(args.auto_cme_date or args.date is None)
    if use_cme:
        audit_date = resolve_cme_trading_date(now_utc)
        ingest_tz = "chicago"
        print(f"CME trading date resolved to: {audit_date.isoformat()} ({CHICAGO})")
        if args.date is not None and args.auto_cme_date:
            print(f"NOTE: --auto-cme-date overrides explicit --date; using {audit_date.isoformat()}")
    else:
        if not args.date:
            print("ERROR: --date is required when not using CME default.", file=sys.stderr)
            return 2
        audit_date = date.fromisoformat(args.date)
        ingest_tz = args.tz
        if args.tz != "chicago":
            print(
                "WARNING: Using non-CME timezone may produce inconsistent diagnostics "
                "(day boundary is not CME session).",
                file=sys.stderr,
            )

    log_dir = resolve_log_dir(args.log_dir)
    if not log_dir.is_dir():
        print(f"ERROR: log_dir not found: {log_dir}", file=sys.stderr)
        return 2

    window = timedelta(seconds=max(1, args.window_seconds))
    events = _load_day_events(log_dir, audit_date, ingest_tz)

    cpu_day_count = sum(1 for e in events if (e.event or "").strip() == CPU_EVENT)
    if cpu_day_count == 0 and not args.allow_missing_cpu_profile:
        print(
            "ERROR: No ENGINE_CPU_PROFILE events found in selected window\n"
            "CPU correlation unavailable — diagnostic incomplete",
            file=sys.stderr,
        )
        return 2

    bursts = _collect_bursts(events, window, args.instrument)

    if not bursts:
        print(
            f"No burst anchors ({', '.join(sorted(BURST_ANCHOR_EVENTS))}) for date {audit_date.isoformat()}",
        )
        if args.instrument:
            print(f"  (instrument filter: {args.instrument})")
        print(f"log_dir: {log_dir}")
        print(f"CPU_PROFILING_PRESENT: {'YES' if cpu_day_count > 0 else 'NO'}")
        return 0

    pass_n = warn_n = fail_n = 0
    blocks: List[str] = []
    all_primary: List[str] = []
    all_peaks: List[float] = []
    any_gate = False

    event_times = [e.ts_utc for e in events]
    all_metrics = _analyze_bursts_one_pass(events, bursts, window)
    for (burst_start, anchor_ev, inst), m in zip(bursts, all_metrics):
        burst_end = burst_start + window
        legacy_status, legacy_reason, _ = _evaluate(m)
        legacy_fail = legacy_status == "FAIL"
        legacy_warn = legacy_status == "WARNING"

        timeline_events = _collect_timeline_events(
            events, event_times, burst_start, burst_end, inst,
        )
        base_cft = classify_control_failure(m)
        peak = peak_burst_cpu(m)
        cft = refine_with_cpu(base_cft, peak)
        fv = find_first_violation(cft, burst_start, timeline_events)
        sev = severity_score(m)
        sev_lbl = severity_label(sev)

        status = status_from_classification(cft, legacy_fail, legacy_warn)
        reason = final_reason_text(cft, peak, legacy_reason)

        if status == "PASS":
            pass_n += 1
        elif status == "WARNING":
            warn_n += 1
        else:
            fail_n += 1

        all_primary.append(cft)
        if peak is not None:
            all_peaks.append(peak)
        if m.max_execution_cycle_count > 0 or m.gate_reconciliation_cycles > 0:
            any_gate = True

        tl_raw = build_control_timeline(burst_start, anchor_ev, timeline_events)
        tl_lines = tl_raw
        if args.max_timeline_lines > 0 and len(tl_lines) > 1 + args.max_timeline_lines:
            cap = 1 + args.max_timeline_lines
            rest = len(tl_lines) - cap
            tl_lines = tl_lines[:cap] + [f"... ({rest} more lines truncated)"]

        lines: List[str] = []
        lines.append(_format_burst_header(burst_start, inst, ingest_tz))
        lines.append("--------------------------------")
        lines.append(f"gate_cycles: {m.gate_reconciliation_cycles}")
        lines.append(f"non_gate_cycles: {m.non_gate_reconciliation_cycles}")
        lines.append(f"expensive: {m.total_expensive_invoked}")
        lines.append(f"throttled: {m.total_throttled_cycles}")
        lines.append("")
        lines.append(f"max_execution_cycle_count: {m.max_execution_cycle_count}")
        lines.append(f"max_total_expensive: {m.max_total_expensive_since_gate_engaged}")
        lines.append(f"max_no_progress_iterations: {m.max_no_progress_iterations}")
        lines.append("")
        lines.append(f"control_failure: {cft}")
        fv_line = format_first_violation(fv)
        lines.append(f"first_violation: {fv_line}")
        if fv is not None:
            lines.append(f"first_violation_ts: {_format_time(fv.ts_utc)}")
        lines.append("")
        lines.append(f"severity: {sev} ({sev_lbl})")
        lines.append("")
        lines.append(f"STATUS: {status}")
        lines.append(f"reason: {reason}")
        lines.append("")
        lines.append("CONTROL TIMELINE:")
        lines.extend(tl_lines)
        avg_p = avg_burst_cpu(m)
        peak_p = peak
        if avg_p is not None and peak_p is not None:
            src = "process_cpu_pct" if m.cpu_process_pcts else "engine_proxy"
            lines.append("")
            lines.append(f"avg_process_cpu_pct: {avg_p:.2f}% ({src})")
            lines.append(f"peak_process_cpu_pct: {peak_p:.2f}% ({src})")
        blocks.append("\n".join(lines))

    print("\n\n".join(blocks))
    print("")
    print(f"TOTAL BURSTS: {len(bursts)}")
    print(f"PASS: {pass_n}")
    print(f"WARNING: {warn_n}")
    print(f"FAIL: {fail_n}")
    print(f"CPU_PROFILING_PRESENT: {'YES' if cpu_day_count > 0 else 'NO'}")

    _prio = (
        "EXECUTION_LOOP_CONFIRMED",
        "EXECUTION_CAP_BREACHED",
        "ABSOLUTE_CAP_BREACHED",
        "CONTROL_INSUFFICIENT",
        "THROTTLE_NOT_ENGAGED",
        "EXTERNAL_EXECUTION_LOOP",
        "HARD_STOP_NOT_TRIGGERED",
        "HEALTHY",
        "NOT_CLASSIFIED",
    )
    worst = "NOT_CLASSIFIED"
    for w in _prio:
        if w in all_primary:
            worst = w
            break
    peak_all = max(all_peaks) if all_peaks else None
    print("")
    print("ROOT CAUSE SUMMARY")
    print("------------------")
    print(f"Primary classification: {worst}")
    print(f"CPU correlated: {'YES' if cpu_day_count > 0 else 'NO'}")
    print(f"Peak CPU during burst: {peak_all:.2f}%" if peak_all is not None else "Peak CPU during burst: n/a")
    print(f"Gate involved: {'YES' if any_gate else 'NO'}")
    print("")
    if worst == "EXECUTION_LOOP_CONFIRMED":
        concl = "Execution/update loop is the primary CPU driver (confirmed with process CPU)."
    elif worst == "EXTERNAL_EXECUTION_LOOP":
        concl = "No gate participation pattern in non-gate reconciliation spam; check execution loop / telemetry."
    elif worst == "HEALTHY" and all(x in ("HEALTHY", "NOT_CLASSIFIED") for x in all_primary):
        concl = "No dominant failure pattern; gate metrics within envelope or not classified."
    else:
        concl = "See per-burst control_failure and STATUS above."
    print(f"Conclusion:\n{concl}")

    if fail_n > 0:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
