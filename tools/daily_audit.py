#!/usr/bin/env python3
"""
Full-system daily audit (Robot + Watchdog) per docs/robot/audits/DAILY_AUDIT_FRAMEWORK_ROBOT_WATCHDOG.md v1.6.

Example:
  python tools/daily_audit.py --date 2026-03-24 --tz chicago
  python tools/daily_audit.py --date 2026-03-25 --tz utc --last-minutes 10 --json-out reports/daily_audit/recent_10m.json
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
from dataclasses import replace
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

try:
    from zoneinfo import ZoneInfo
except Exception:  # pragma: no cover
    ZoneInfo = None  # type: ignore

from log_audit import NormEvent, normalize_event, resolve_log_dir as resolve_robot_log_dir

from daily_audit_convergence import analyze_convergence_failure_explanations, render_convergence_markdown
from daily_audit_engine_load import compute_engine_load_analysis
from daily_audit_iea_integrity import analyze_iea_integrity

SCHEMA_VERSION = "1.0"
CHICAGO = "America/Chicago"


def resolve_project_root() -> Path:
    env = os.environ.get("QTSW2_PROJECT_ROOT")
    if env:
        return Path(env)
    return Path(__file__).resolve().parent.parent


def resolve_reports_dir(cli: Optional[str]) -> Path:
    if cli:
        return Path(cli)
    env = os.environ.get("QTSW2_REPORTS_DIR")
    if env:
        return Path(env)
    return resolve_project_root() / "reports" / "daily_audit"


def load_thresholds(project_root: Path) -> Dict[str, Any]:
    p = project_root / "configs" / "robot" / "daily_audit_thresholds.json"
    defaults: Dict[str, Any] = {
        "min_engine_lines_for_full_audit": 50,
        "min_engine_full_history_days_for_drift": 5,
        "chain_window_ms": 600000,
        "chain_quiet_gap_ms": 300000,
        "timeline_collapse_window_ms": 120000,
        "mismatch_warn_per_instrument_day": 20,
        "disconnect_warn_count": 3,
        "max_downtime_warn_s": 120,
        "queue_wait_warn_ms": 5000,
        "cpu_normalized_warn": 0.5,
        "cpu_normalized_critical": 0.7,
        "t_queue_warn_ms": 2000,
        "d_queue_warn": 20,
        "drift_window_days": 14,
        "watchdog_max_count_delta": 2,
        "silent_t_silent_min": 20,
        "silent_failure_group_window_ms": 180000,
        "engine_load_bucket_seconds": 5,
        "engine_load_storm_eps_threshold": 35,
        "engine_load_storm_min_consecutive_windows": 3,
        "engine_load_loop_window_sec": 60,
        "engine_load_loop_min_repeats_gate": 20,
        "engine_load_loop_min_repeats_mismatch": 15,
        "engine_load_loop_min_repeats_adoption": 8,
        "engine_load_peak_low": 8,
        "engine_load_peak_moderate": 20,
        "engine_load_peak_high": 45,
        "engine_load_peak_critical": 90,
        "engine_load_max_dense_windows_export": 200,
        "engine_load_subwindow_seconds": 1,
        "engine_load_reconciliation_ratio_severity_upgrade": 0.5,
        "engine_load_total_storm_time_escalate_seconds": 120,
        "engine_load_reconciliation_storm_duration_critical_seconds": 15,
        "iea_integrity_adoption_followup_window_ms": 2000,
        "iea_integrity_cross_iea_window_ms": 400,
        "iea_integrity_resolution_suspect_critical": 25,
        "iea_integrity_lifecycle_critical": 50,
        "iea_integrity_cross_iea_duplicate_critical": 30,
        "iea_integrity_efficiency_critical": 0.05,
        "iea_integrity_stuck_no_progress_duration_ms": 120_000,
        "iea_integrity_stuck_min_cycles": 30,
        "iea_integrity_max_episodes_in_report": 50,
        "iea_integrity_progress_efficiency_warn": 0.05,
        "iea_integrity_wasted_work_warn": 0.8,
        "iea_integrity_throttle_no_progress_cycle_threshold": 3,
        "iea_integrity_throttle_no_progress_time_ms": 12000,
        "iea_integrity_throttle_warn_missed": 1,
        "iea_integrity_progress_revert_window_ms": 10000,
        "iea_integrity_progress_quality_warn": 0.2,
        "iea_root_cause_min_expected_throttle": 5,
        "iea_root_cause_min_storms_for_throughput": 2,
        "iea_integrity_observability_gap_warn_seconds": 300,
        "iea_integrity_correlation_mismatch_spike_min": 15,
        "convergence_significant_min_cycles": 5,
        "convergence_failure_max_episodes_export": 25,
    }
    if p.is_file():
        try:
            with p.open("r", encoding="utf-8") as f:
                merged = {**defaults, **json.load(f)}
                return merged
        except Exception:
            return defaults
    return defaults


def parse_audit_date(s: str) -> date:
    return date.fromisoformat(s)


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
    # de-dup preserve order
    seen = set()
    out: List[Path] = []
    for p in paths:
        rp = p.resolve()
        if rp not in seen:
            seen.add(rp)
            out.append(p)
    return out


def collect_watchdog_paths(log_dir: Path) -> List[Path]:
    out: List[Path] = []
    for name in ("frontend_feed.jsonl", "incidents.jsonl"):
        p = log_dir / name
        if p.is_file():
            out.append(p)
    return out


def iter_jsonl(path: Path) -> Iterable[Tuple[int, str]]:
    try:
        with path.open("r", encoding="utf-8", errors="replace") as f:
            for i, line in enumerate(f, 1):
                yield i, line.strip()
    except Exception:
        return


def stable_data_hash(data: Dict[str, Any]) -> str:
    skip = {"wall_clock", "metrics", "note"}
    keys = sorted(k for k in data.keys() if k not in skip)
    parts: List[str] = []
    for k in keys:
        try:
            parts.append(f"{k}:{json.dumps(data[k], sort_keys=True, default=str)}")
        except Exception:
            parts.append(f"{k}:?")
    raw = "|".join(parts).encode("utf-8", errors="replace")
    return hashlib.sha256(raw).hexdigest()[:24]


def dedupe_key(e: NormEvent) -> str:
    rid = str(e.data.get("run_id") or "")
    idem = e.data.get("idempotency_key") or e.data.get("event_id") or e.data.get("idempotencyKey")
    if idem:
        return f"id:{idem}"
    ex = e.data.get("execution_sequence")
    if ex is not None and rid:
        return f"ex:{e.instrument}|{rid}|{ex}|{e.event}"
    h = stable_data_hash(e.data if isinstance(e.data, dict) else {})
    ts_ms = int(e.ts_utc.timestamp() * 1000)
    return f"row:{ts_ms}|{e.event}|{e.instrument}|{rid}|{h}"


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


def ingest_journal_snapshots(
    log_dir: Path,
    audit_date: date,
    day_start: datetime,
    day_end: datetime,
    stats: Dict[str, Any],
) -> List[NormEvent]:
    jdir = log_dir / "journal"
    if not jdir.is_dir():
        return []
    prefix = audit_date.isoformat() + "_"
    out: List[NormEvent] = []
    for p in sorted(jdir.glob(prefix + "*.json")):
        try:
            with p.open("r", encoding="utf-8") as f:
                obj = json.load(f)
        except Exception:
            stats["parse_errors_count"] = stats.get("parse_errors_count", 0) + 1
            continue
        if not isinstance(obj, dict):
            continue
        ts_raw = obj.get("LastUpdateUtc") or obj.get("TradingDate")
        dt: Optional[datetime] = None
        if isinstance(ts_raw, str):
            try:
                t = ts_raw
                if t.endswith("Z"):
                    dt = datetime.fromisoformat(t[:-1]).replace(tzinfo=timezone.utc)
                else:
                    dt = datetime.fromisoformat(t.replace("Z", "+00:00"))
                    if dt.tzinfo is None:
                        dt = dt.replace(tzinfo=timezone.utc)
                    dt = dt.astimezone(timezone.utc)
            except Exception:
                dt = None
        if dt is None:
            try:
                dt = datetime(audit_date.year, audit_date.month, audit_date.day, 17, 0, 0, tzinfo=timezone.utc)
            except Exception:
                continue
        if dt < day_start or dt >= day_end:
            continue
        stream = str(obj.get("Stream") or "")
        reason = str(obj.get("CommitReason") or "")
        pseudo = {
            "ts_utc": dt.isoformat().replace("+00:00", "Z"),
            "level": "INFO",
            "source": "JournalSnapshot",
            "instrument": stream,
            "event": "EXECUTION_JOURNAL_SNAPSHOT",
            "message": reason or "journal_snapshot",
            "data": {"journal_file": p.name, "committed": obj.get("Committed"), "reason": reason},
        }
        ev = normalize_event(pseudo, p.name)
        if ev:
            out.append(ev)
            stats["journal_files_used"] = stats.get("journal_files_used", 0) + 1
    return out


def dedupe_events(events: Sequence[NormEvent]) -> Tuple[List[NormEvent], int]:
    seen: Dict[str, NormEvent] = {}
    order: List[str] = []
    for e in sorted(events, key=lambda x: (x.ts_utc, x.event, x.instrument, x.file)):
        k = dedupe_key(e)
        if k not in seen:
            seen[k] = e
            order.append(k)
    return [seen[k] for k in order], len(events) - len(seen)


# --- Domain classification helpers ---

FAIL_CLOSED_SUB = "FAIL_CLOSED"
TIER1_ADOPTION = (
    "ADOPTION_NON_CONVERGENCE_ESCALATED",
    "IEA_ADOPTION_SCAN_GATE_ANOMALY",
    "IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS",
)


def is_engine_activity_event(ev_name: str) -> bool:
    """Canonical engine-activity signals (heartbeat, CPU, stall family, bar acceptance)."""
    if not ev_name:
        return False
    if ev_name in ("ENGINE_TIMER_HEARTBEAT", "ENGINE_CPU_PROFILE", "BAR_ACCEPTED"):
        return True
    if ev_name.startswith("ENGINE_TICK_STALL"):
        return True
    return False


def rollup_overall(
    safety: str,
    connectivity: str,
    execution: str,
    reconciliation: str,
    protection: str,
    performance: str,
    supervisory: str,
    execution_has_timeout: bool,
) -> str:
    def sev(s: str) -> int:
        u = s.upper()
        if u == "UNKNOWN":
            return 1
        if "CRITICAL" in u or u == "BACKLOGGED" and execution_has_timeout:
            return 3
        if "CRITICAL_LOAD" in u or u == "CRITICAL":
            return 3
        if "ISSUES" in u and "DETECTED" in u:
            return 3
        if u in ("BACKLOGGED",):
            return 3 if execution_has_timeout else 2
        if u in ("UNSTABLE", "DEGRADED", "WARNING", "HIGH_LOAD", "ISSUES DETECTED"):
            return 2
        return 1

    scores = [
        sev(safety),
        2 if connectivity == "UNSTABLE" else 1,
        sev(execution),
        2 if reconciliation == "UNSTABLE" else 1,
        sev(protection),
        sev(performance),
        2 if supervisory == "UNSTABLE" else 1,
    ]
    m = max(scores)
    if m >= 3:
        return "CRITICAL"
    if m >= 2:
        return "WARNING"
    return "OK"


def overall_from_domains(domains: Dict[str, Any], scratch: Dict[str, Any]) -> str:
    et = int(scratch.get("iea_timeout", 0) or 0) + int(scratch.get("queue_overflow", 0) or 0) > 0
    return rollup_overall(
        domains["safety"]["status"],
        domains["connectivity"]["status"],
        domains["execution"]["status"],
        domains["reconciliation"]["status"],
        domains["protection"]["status"],
        domains["performance"]["status"],
        domains["supervisory"]["status"],
        et,
    )


def evaluate_engine_full(scratch: Dict[str, Any], min_engine_lines: int) -> bool:
    """
    ENGINE_FULL iff: volume + activity + at least one heartbeat/CPU/pass signature.
    """
    lines = int(scratch.get("engine_stream_lines") or 0)
    hb = int(scratch.get("engine_heartbeat_lines") or 0)
    cpu = int(scratch.get("cpu_profiles") or 0)
    passes = int(scratch.get("reconciliation_pass") or 0)
    engine_active = bool(scratch.get("engine_activity_detected"))
    coverage_triple = hb > 0 or cpu > 0 or passes > 0
    return lines >= min_engine_lines and engine_active and coverage_triple


def apply_partial_mode_gating(domains: Dict[str, Any], summary: Dict[str, Any], audit_mode: str) -> None:
    """Hard capability gating for PARTIAL audits (enforced)."""
    if audit_mode != "PARTIAL":
        return
    perf = domains.get("performance") or {}
    perf["status"] = "UNKNOWN"
    perf["metrics"] = None
    perf["timeline_refs"] = perf.get("timeline_refs") or []
    recon = domains.get("reconciliation") or {}
    rmetrics = dict(recon.get("metrics") or {})
    rmetrics["convergence_rate_pct"] = None
    recon["metrics"] = rmetrics
    summary["max_cpu_parallelism_raw"] = None
    summary["max_cpu_parallelism_normalized"] = None
    summary["dominant_subsystem"] = None


def compute_domains(
    events: List[NormEvent],
    thresholds: Dict[str, Any],
    logical_cpu: int,
) -> Tuple[Dict[str, Any], Dict[str, Any], str]:
    """Returns (domains dict, scratch metrics for summary, overall_status)."""
    m: Dict[str, Any] = {
        "mismatch": 0,
        "mismatch_cleared": 0,
        "fail_closed": 0,
        "gate_engaged": 0,
        "adoption_escalated": 0,
        "stall_detected": 0,
        "disconnect_lost": 0,
        "disconnect_recovered": 0,
        "reconciliation_pass": 0,
        "reconciliation_converged": 0,
        "stuck": 0,
        "oscillation": 0,
        "queue_pressure_sig": 0,
        "max_queue_wait_ms": 0,
        "adoption_scan_max_ms": 0,
        "iea_timeout": 0,
        "queue_overflow": 0,
        "supervisory_invalid": 0,
        "supervisory_escalated": 0,
        "protective_failed": 0,
        "protective_ok": 0,
        "registry_divergence": 0,
        "tier1_adoption_strain": 0,
        "bar_accepted": 0,
        "execution_activity": 0,
        "lifecycle_events": 0,
        "cpu_profiles": 0,
        "parallelism_raw_max": 0.0,
        "parallelism_norm_max": 0.0,
        "dominant_subsystem": None,
    }
    sub_acc: Dict[str, List[float]] = {}

    engine_events = 0
    engine_stream_lines = 0
    engine_start = False
    timer_hb = 0
    saw_engine_activity = False

    for e in events:
        ev = e.event or ""
        if is_engine_activity_event(ev):
            saw_engine_activity = True
        if "ENGINE_START" in ev or ev == "ENGINE_TIMER_HEARTBEAT":
            engine_start = True
        if ev == "ENGINE_TIMER_HEARTBEAT":
            timer_hb += 1
        if (e.source or "").lower().find("engine") >= 0 or (e.data.get("stream") == "__engine__"):
            engine_stream_lines += 1
        if e.data.get("stream") == "__engine__" or ev.startswith("RECONCILIATION") or ev.startswith("ENGINE_"):
            engine_events += 1

        if "RECONCILIATION_MISMATCH" in ev and "CLEARED" not in ev and "METRICS" not in ev:
            m["mismatch"] += 1
        if ev in ("RECONCILIATION_MISMATCH_CLEARED", "RECONCILIATION_RESOLVED", "RECONCILIATION_CONVERGED"):
            m["mismatch_cleared"] += 1
        if FAIL_CLOSED_SUB in ev or ev.startswith("DISCONNECT_FAIL_CLOSED"):
            m["fail_closed"] += 1
        if ev == "STATE_CONSISTENCY_GATE_ENGAGED":
            m["gate_engaged"] += 1
        if ev == "ADOPTION_NON_CONVERGENCE_ESCALATED":
            m["adoption_escalated"] += 1
        if ev in TIER1_ADOPTION or ev.startswith("IEA_ADOPTION_SCAN_GATE_ANOMALY"):
            m["tier1_adoption_strain"] += 1
        if ev.startswith("ENGINE_TICK_STALL"):
            m["stall_detected"] += 1
        if ev == "BAR_ACCEPTED":
            m["bar_accepted"] += 1
        if ev in ("ORDER_SUBMITTED", "EXECUTION_FILLED", "ENTRY_FILLED", "EXIT_FILLED"):
            m["execution_activity"] += 1
        if ev == "LIFECYCLE_TRANSITIONED" or "LIFECYCLE" in ev:
            m["lifecycle_events"] += 1
        if ev == "CONNECTION_LOST":
            m["disconnect_lost"] += 1
        if ev in ("CONNECTION_RECOVERED", "CONNECTION_RECOVERED_NOTIFICATION"):
            m["disconnect_recovered"] += 1
        if ev == "RECONCILIATION_PASS_SUMMARY":
            m["reconciliation_pass"] += 1
        if ev == "RECONCILIATION_CONVERGED":
            m["reconciliation_converged"] += 1
        if ev == "RECONCILIATION_STUCK":
            m["stuck"] += 1
        if ev == "RECONCILIATION_OSCILLATION":
            m["oscillation"] += 1
        if "IEA_QUEUE_PRESSURE" in ev:
            age = e.data.get("current_work_item_age_ms")
            try:
                age_i = int(age) if age is not None else 0
            except Exception:
                age_i = 0
            depth = e.data.get("queue_depth") or e.data.get("depth")
            try:
                d_i = int(depth) if depth is not None else 0
            except Exception:
                d_i = 0
            m["max_queue_wait_ms"] = max(m["max_queue_wait_ms"], age_i)
            if age_i >= int(thresholds.get("t_queue_warn_ms", 2000)) or d_i >= int(
                thresholds.get("d_queue_warn", 20)
            ):
                m["queue_pressure_sig"] += 1
        if "ADOPTION_SCAN_SUMMARY" in ev:
            sw = e.data.get("scan_wall_ms")
            try:
                sw_i = int(float(sw)) if sw is not None else 0
            except Exception:
                sw_i = 0
            m["adoption_scan_max_ms"] = max(m["adoption_scan_max_ms"], sw_i)
        if "IEA_ENQUEUE_AND_WAIT_TIMEOUT" in ev or ev == "QUEUE_OVERFLOW":
            m["iea_timeout"] += 1
        if ev == "QUEUE_OVERFLOW":
            m["queue_overflow"] += 1
        if ev == "SUPERVISORY_STATE_TRANSITION_INVALID":
            m["supervisory_invalid"] += 1
        if "SUPERVISORY_ESCALATED" in ev:
            m["supervisory_escalated"] += 1
        if ev in ("PROTECTIVE_AUDIT_FAILED", "PROTECTIVE_MISSING_STOP"):
            m["protective_failed"] += 1
        if ev == "PROTECTIVE_AUDIT_OK":
            m["protective_ok"] += 1
        if ev in ("REGISTRY_BROKER_DIVERGENCE", "STALE_QTSW2_ORDER_DETECTED"):
            m["registry_divergence"] += 1
        if ev == "ENGINE_CPU_PROFILE":
            m["cpu_profiles"] += 1
            d = e.data
            lock_sum = d.get("lock_sum_ms")
            wall = d.get("wall_window_ms") or d.get("wall_ms")
            try:
                ls = float(lock_sum) if lock_sum is not None else 0.0
            except Exception:
                ls = 0.0
            try:
                w = float(wall) if wall is not None else 0.0
            except Exception:
                w = 0.0
            par_raw = (ls / w) if w > 0 else 0.0
            par_norm = par_raw / max(1, logical_cpu)
            m["parallelism_raw_max"] = max(m["parallelism_raw_max"], par_raw)
            m["parallelism_norm_max"] = max(m["parallelism_norm_max"], par_norm)
            for key in (
                "stream_tick_ms",
                "reconciliation_runner_ms",
                "tail_coordinators_ms",
                "onbar_avg_lock_ms",
            ):
                try:
                    v = float(d.get(key) or 0)
                except Exception:
                    v = 0.0
                if ls > 0 and v > 0:
                    pct = 100.0 * v / ls
                    sub_acc.setdefault(key, []).append(pct)

    dominant = None
    mean_pct: Dict[str, float] = {}
    for k, vals in sub_acc.items():
        if vals:
            mean_pct[k] = round(sum(vals) / len(vals), 2)
    if mean_pct:
        dominant = sorted(mean_pct.keys(), key=lambda x: (-mean_pct[x], x))[0]

    # Safety status
    crit_safety = m["fail_closed"] > 0 or m["adoption_escalated"] > 0 or m["registry_divergence"] > 0
    warn_safety = m["mismatch"] > int(thresholds.get("mismatch_warn_per_instrument_day", 20)) or m["stuck"] > 0
    if crit_safety:
        safety_status = "CRITICAL"
    elif warn_safety or m["oscillation"] > 0:
        safety_status = "WARNING"
    else:
        safety_status = "OK"

    # Connectivity
    disc = m["disconnect_lost"]
    max_dt = 0.0  # simplified — pairing done in timeline; metric approx
    if disc >= int(thresholds.get("disconnect_warn_count", 3)):
        conn_status = "UNSTABLE"
    else:
        conn_status = "STABLE"

    # Execution
    exec_timeout = m["iea_timeout"] + m["queue_overflow"] > 0
    if exec_timeout or m["max_queue_wait_ms"] > 30000:
        exec_status = "BACKLOGGED"
    elif (
        m["max_queue_wait_ms"] > int(thresholds.get("queue_wait_warn_ms", 5000))
        or m["adoption_scan_max_ms"] > 30000
        or m["adoption_escalated"] > 0
        or m["tier1_adoption_strain"] > 0
    ):
        exec_status = "DEGRADED"
    else:
        exec_status = "HEALTHY"

    # Reconciliation
    if m["stuck"] > 0 or m["oscillation"] > 0:
        recon_status = "UNSTABLE"
    else:
        recon_status = "STABLE"

    convergence_rate_pct: Optional[float] = None
    if m["reconciliation_pass"] > 0:
        convergence_rate_pct = round(100.0 * m["reconciliation_converged"] / m["reconciliation_pass"], 2)

    # Protection
    if m["protective_failed"] > 0:
        prot_status = "ISSUES DETECTED"
    else:
        prot_status = "OK"

    # Performance
    cn = m["parallelism_norm_max"]
    if cn >= float(thresholds.get("cpu_normalized_critical", 0.7)) and m["cpu_profiles"] > 0:
        perf_status = "CRITICAL_LOAD"
    elif cn >= float(thresholds.get("cpu_normalized_warn", 0.5)) and m["cpu_profiles"] > 0:
        perf_status = "HIGH_LOAD"
    else:
        perf_status = "NORMAL"

    # Supervisory
    sup_status = "UNSTABLE" if m["supervisory_invalid"] > 0 else "STABLE"

    overall = rollup_overall(
        safety_status,
        conn_status,
        exec_status,
        recon_status,
        prot_status,
        perf_status,
        sup_status,
        exec_timeout,
    )

    domains = {
        "safety": {
            "status": safety_status,
            "metrics": {
                "total_mismatch_signals": m["mismatch"],
                "fail_closed_count": m["fail_closed"],
                "gate_engaged_count": m["gate_engaged"],
                "registry_divergence_count": m["registry_divergence"],
            },
            "critical_findings": [],
            "timeline_refs": [],
        },
        "connectivity": {
            "status": conn_status,
            "metrics": {
                "disconnects": disc,
                "max_downtime_s": round(max_dt, 1),
            },
            "timeline_refs": [],
        },
        "execution": {
            "status": exec_status,
            "metrics": {
                "max_queue_wait_ms": m["max_queue_wait_ms"],
                "adoption_scan_max_ms": m["adoption_scan_max_ms"],
                "timeouts_or_overflow": m["iea_timeout"] + m["queue_overflow"],
            },
            "timeline_refs": [],
        },
        "reconciliation": {
            "status": recon_status,
            "metrics": {
                "passes": m["reconciliation_pass"],
                "converged": m["reconciliation_converged"],
                "stuck_events": m["stuck"],
                "oscillation_events": m["oscillation"],
                "convergence_rate_pct": convergence_rate_pct,
            },
            "timeline_refs": [],
        },
        "protection": {
            "status": prot_status,
            "metrics": {"protective_failed": m["protective_failed"], "protective_ok": m["protective_ok"]},
            "timeline_refs": [],
        },
        "performance": {
            "status": perf_status,
            "metrics": {
                "dominant_subsystem": dominant,
                "subsystem_pct_mean": mean_pct or None,
                "parallelism_raw_max": round(m["parallelism_raw_max"], 4) if m["cpu_profiles"] else None,
                "parallelism_normalized_max": round(m["parallelism_norm_max"], 4) if m["cpu_profiles"] else None,
                "cpu_profile_samples": m["cpu_profiles"],
            },
            "timeline_refs": [],
        },
        "supervisory": {
            "status": sup_status,
            "metrics": {"invalid_transitions": m["supervisory_invalid"], "escalated": m["supervisory_escalated"]},
            "timeline_refs": [],
        },
    }

    scratch = {
        **m,
        "engine_stream_lines": engine_stream_lines,
        "engine_heartbeat_lines": timer_hb,
        "engine_start_hint": engine_start,
        "dominant_subsystem": dominant,
        "mean_pct": mean_pct,
        "engine_activity_detected": saw_engine_activity,
    }
    return domains, scratch, overall


def confidence_enforced(confidence: str, overall: str) -> str:
    if confidence == "HIGH":
        return overall
    if confidence == "MEDIUM":
        return "WARNING" if overall == "OK" else overall
    # LOW
    if overall == "OK":
        return "WARNING"
    if overall == "WARNING":
        return "CRITICAL"
    return "CRITICAL"


def trade_readiness_decision(
    overall: str,
    confidence: str,
    enforced: str,
    safety_status: str,
    exec_status: str,
    silent: bool,
    drift_risk: str,
    audit_mode: str,
) -> Tuple[str, str, List[str]]:
    blocking: List[str] = []
    if (
        safety_status == "CRITICAL"
        or exec_status == "BACKLOGGED"
        or silent
        or enforced == "CRITICAL"
        or overall == "CRITICAL"
    ):
        if safety_status == "CRITICAL":
            blocking.append("SAFETY_CRITICAL")
        if exec_status == "BACKLOGGED":
            blocking.append("EXECUTION_BACKLOGGED")
        if silent:
            blocking.append("SILENT_FAILURE")
        if enforced == "CRITICAL":
            blocking.append("CONFIDENCE_ENFORCED_CRITICAL")
        if overall == "CRITICAL":
            blocking.append("OVERALL_CRITICAL")
        return "NO_GO", "Blocking safety, execution, silent failure, or confidence-enforced critical path.", blocking

    caution = False
    if enforced == "WARNING" or exec_status == "DEGRADED" or overall == "WARNING" or drift_risk == "HIGH":
        if enforced == "WARNING":
            blocking.append("CONFIDENCE_ENFORCED_WARNING")
        if exec_status == "DEGRADED":
            blocking.append("EXECUTION_DEGRADED")
        if overall == "WARNING":
            blocking.append("OVERALL_WARNING")
        if drift_risk == "HIGH":
            blocking.append("DRIFT_HIGH")
            caution = True
        if caution or enforced == "WARNING" or exec_status == "DEGRADED" or overall == "WARNING":
            return "CAUTION", "Elevated risk: review enforced status, execution, or drift.", blocking

    if audit_mode == "PARTIAL":
        return (
            "CAUTION",
            "PARTIAL audit mode: GO is not permitted without full engine coverage.",
            ["PARTIAL_AUDIT_MODE"],
        )

    return "GO", "All gates clear.", []


def select_timeline_events(events: List[NormEvent], thresholds: Dict[str, Any]) -> List[NormEvent]:
    tq = int(thresholds.get("t_queue_warn_ms", 2000))
    dq = int(thresholds.get("d_queue_warn", 20))
    cpu_floor = float(thresholds.get("cpu_normalized_warn", 0.5)) * 0.85
    out: List[NormEvent] = []
    for e in events:
        ev = e.event or ""
        if ev in (
            "CONNECTION_LOST",
            "CONNECTION_RECOVERED",
            "DISCONNECT_FAIL_CLOSED_ENTERED",
            "DISCONNECT_RECOVERY_COMPLETE",
        ):
            out.append(e)
            continue
        if "FAIL_CLOSED" in ev or ev.startswith("FORCED_FLATTEN") or ev == "UNOWNED_LIVE_ORDER_DETECTED":
            out.append(e)
            continue
        if ev in (
            "RECONCILIATION_MISMATCH_DETECTED",
            "RECONCILIATION_STUCK",
            "RECONCILIATION_OSCILLATION",
            "RECONCILIATION_CONVERGED",
        ):
            out.append(e)
            continue
        if ev.startswith("STATE_CONSISTENCY_GATE"):
            out.append(e)
            continue
        if ev in (
            "IEA_ADOPTION_SCAN_EXECUTION_STARTED",
            "IEA_ADOPTION_SCAN_EXECUTION_COMPLETED",
        ) or "IEA_QUEUE_PRESSURE" in ev:
            age = e.data.get("current_work_item_age_ms")
            depth = e.data.get("queue_depth") or e.data.get("depth")
            try:
                ai = int(age) if age is not None else 0
            except Exception:
                ai = 0
            try:
                di = int(depth) if depth is not None else 0
            except Exception:
                di = 0
            if ai >= tq or di >= dq:
                out.append(e)
            continue
        if ev in TIER1_ADOPTION or "ADOPTION_NON_CONVERGENCE" in ev:
            out.append(e)
            continue
        if ev == "ENGINE_CPU_PROFILE":
            d = e.data
            lock_sum = d.get("lock_sum_ms")
            wall = d.get("wall_window_ms") or d.get("wall_ms")
            try:
                ls = float(lock_sum) if lock_sum is not None else 0.0
                w = float(wall) if wall is not None else 1.0
                pr = (ls / w) if w > 0 else 0.0
                pn = pr / max(1, os.cpu_count() or 1)
            except Exception:
                pn = 0.0
            if pn >= cpu_floor:
                out.append(e)
            continue
        if ev.startswith("ENGINE_TICK_STALL"):
            out.append(e)
            continue
        if ev in ("ENTRY_FILLED", "EXIT_FILLED", "EXECUTION_JOURNAL_SNAPSHOT"):
            out.append(e)
            continue
    return sorted(out, key=lambda x: (x.ts_utc, x.event, x.instrument))


def collapse_timeline_burst(
    selected: List[NormEvent], collapse_ms: int
) -> List[NormEvent]:
    """Collapse consecutive identical (event, instrument) within collapse_ms (§5.3)."""
    if not selected:
        return []
    out: List[NormEvent] = []
    cur: Optional[NormEvent] = None
    burst_n = 1
    last_ts: Optional[datetime] = None

    def flush() -> None:
        nonlocal cur, burst_n, last_ts
        if cur is None:
            return
        if burst_n > 1:
            msg = f"{cur.message or cur.event} (×{burst_n} in {collapse_ms // 1000}s)"
            out.append(replace(cur, message=msg))
        else:
            out.append(cur)
        cur = None
        burst_n = 1
        last_ts = None

    for e in sorted(selected, key=lambda x: (x.ts_utc, x.event, x.instrument)):
        key = (e.event, e.instrument)
        if cur is None:
            cur = e
            last_ts = e.ts_utc
            burst_n = 1
            continue
        ck = (cur.event, cur.instrument)
        gap = (e.ts_utc - last_ts).total_seconds() * 1000 if last_ts else 0
        if key == ck and gap <= collapse_ms:
            burst_n += 1
            last_ts = e.ts_utc
        else:
            flush()
            cur = e
            last_ts = e.ts_utc
            burst_n = 1
    flush()
    return out


def build_compressed_timeline(
    selected: List[NormEvent], tz_name: str
) -> Tuple[List[Dict[str, Any]], List[Dict[str, Any]]]:
    if ZoneInfo is None:
        def fmt_chicago(dt: datetime) -> str:
            return dt.strftime("%H:%M:%S")
    else:
        z = ZoneInfo(CHICAGO)

        def fmt_chicago(dt: datetime) -> str:
            return dt.astimezone(z).strftime("%H:%M:%S")

    entries: List[Dict[str, Any]] = []
    critical_periods: List[Dict[str, Any]] = []
    for e in selected:
        entries.append(
            {
                "start_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
                "end_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
                "display_start_chicago": fmt_chicago(e.ts_utc),
                "display_end_chicago": fmt_chicago(e.ts_utc),
                "event": e.event,
                "summary": (e.message or e.event)[:200],
                "instrument": e.instrument,
                "critical": "FAIL_CLOSED" in (e.event or "") or "STALL" in (e.event or ""),
            }
        )
    return entries, critical_periods


def _is_trigger_start_of_other_chain(
    e: NormEvent, chains: List[Dict[str, Any]], exclude_chain_id: Any
) -> bool:
    """True if this event is the opening trigger of a different chain (new episode)."""
    ts = e.ts_utc.isoformat().replace("+00:00", "Z")
    evn = e.event or ""
    for ch in chains:
        if ch.get("id") == exclude_chain_id:
            continue
        if ch.get("trigger_ts_utc") == ts and ch.get("trigger_event") == evn:
            return True
    return False


def postprocess_chains_stabilizer_mismatch(
    chains: List[Dict[str, Any]], selected: List[NormEvent], q_chain_ms: int
) -> None:
    mismatch_like = (
        "RECONCILIATION_MISMATCH_DETECTED",
        "RECONCILIATION_STUCK",
        "RECONCILIATION_OSCILLATION",
        "RECONCILIATION_MISMATCH_PERSISTENT",
    )
    for c in chains:
        if c.get("classification") != "NORMAL":
            continue
        end_s = c.get("end_utc")
        if not end_s:
            continue
        try:
            t_end = datetime.fromisoformat(end_s.replace("Z", "+00:00"))
        except Exception:
            continue
        cid = c.get("id")
        for e in sorted(selected, key=lambda x: x.ts_utc):
            if e.ts_utc <= t_end:
                continue
            gap_ms = (e.ts_utc - t_end).total_seconds() * 1000.0
            if gap_ms > q_chain_ms:
                break
            if _is_trigger_start_of_other_chain(e, chains, cid):
                continue
            ev = e.event or ""
            if ev in mismatch_like or ("MISMATCH" in ev and "METRICS" not in ev):
                c["classification"] = "DEGRADED"
                c["classification_detail"] = "STABILIZER_THEN_MISMATCH_WITHIN_Q_CHAIN"
                break


def incident_chains_from_timeline(
    selected: List[NormEvent],
    chain_window_ms: int,
    max_chain_span_ms: int,
    max_chain_events: int,
    q_chain_ms: int,
) -> List[Dict[str, Any]]:
    triggers = (
        "CONNECTION_LOST",
        "RECONCILIATION_MISMATCH_DETECTED",
        "STATE_CONSISTENCY_GATE_ENGAGED",
        "REGISTRY_BROKER_DIVERGENCE",
        "STALE_QTSW2_ORDER_DETECTED",
        "ADOPTION_NON_CONVERGENCE_ESCALATED",
        "IEA_ADOPTION_SCAN_GATE_ANOMALY",
        "ENGINE_TICK_STALL_DETECTED",
    )
    stabilizers = (
        "RECONCILIATION_CONVERGED",
        "DISCONNECT_RECOVERY_COMPLETE",
        "CONNECTION_RECOVERED",
        "STATE_CONSISTENCY_GATE_RELEASED",
        "RECONCILIATION_MISMATCH_CLEARED",
    )

    chains: List[Dict[str, Any]] = []
    cur: Optional[Dict[str, Any]] = None
    last_ts: Optional[datetime] = None
    chain_start_ts: Optional[datetime] = None

    def ms(dt: datetime) -> float:
        return dt.timestamp() * 1000.0

    def close_chain_failed(c: Dict[str, Any], end_t: datetime) -> Dict[str, Any]:
        c["end_utc"] = end_t.isoformat().replace("+00:00", "Z")
        c["resolution_time_ms"] = None
        c["classification"] = "FAILED"
        c["classification_detail"] = "UNSTABLE SYSTEM"
        _truncate_chain_events(c, max_chain_events)
        return c

    def _truncate_chain_events(c: Dict[str, Any], cap: int) -> None:
        evs = c.get("events") or []
        if len(evs) > cap:
            c["events"] = evs[:cap]
            c["events_truncated"] = True
        else:
            c["events_truncated"] = False

    for e in sorted(selected, key=lambda x: x.ts_utc):
        ev = e.event or ""
        is_trig = (
            ev in triggers
            or "FAIL_CLOSED" in ev
            or ev.startswith("ENGINE_TICK_STALL")
            or ev.startswith("STATE_CONSISTENCY_GATE")
        )
        is_stable = ev in stabilizers or ev.startswith("CONNECTION_RECOVERED")

        if cur is None:
            if is_trig:
                cur = {
                    "id": len(chains) + 1,
                    "start_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
                    "trigger_event": ev,
                    "trigger_instrument": e.instrument or "",
                    "trigger_ts_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
                    "events": [],
                    "end_utc": None,
                    "resolution_time_ms": None,
                    "classification": "NORMAL",
                    "classification_detail": "NORMAL RECOVERY",
                }
                cur["events"].append(
                    {"ts_utc": e.ts_utc.isoformat().replace("+00:00", "Z"), "event": ev, "instrument": e.instrument}
                )
                last_ts = e.ts_utc
                chain_start_ts = e.ts_utc
            continue

        assert cur is not None and last_ts is not None and chain_start_ts is not None

        if is_trig:
            gap_trig = ms(e.ts_utc) - ms(last_ts)
            if gap_trig <= chain_window_ms:
                cur["events"].append(
                    {"ts_utc": e.ts_utc.isoformat().replace("+00:00", "Z"), "event": ev, "instrument": e.instrument}
                )
                last_ts = e.ts_utc
                continue
            chains.append(close_chain_failed(cur, last_ts))
            cur = {
                "id": len(chains) + 1,
                "start_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
                "trigger_event": ev,
                "trigger_instrument": e.instrument or "",
                "trigger_ts_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
                "events": [
                    {
                        "ts_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
                        "event": ev,
                        "instrument": e.instrument,
                    }
                ],
                "end_utc": None,
                "resolution_time_ms": None,
                "classification": "NORMAL",
                "classification_detail": "NORMAL RECOVERY",
            }
            last_ts = e.ts_utc
            chain_start_ts = e.ts_utc
            continue

        gap = ms(e.ts_utc) - ms(last_ts)
        span = ms(e.ts_utc) - ms(chain_start_ts)
        if span > max_chain_span_ms:
            chains.append(close_chain_failed(cur, last_ts))
            cur = None
            last_ts = None
            chain_start_ts = None
            if is_trig:
                cur = {
                    "id": len(chains) + 1,
                    "start_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
                    "trigger_event": ev,
                    "trigger_instrument": e.instrument or "",
                    "trigger_ts_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
                    "events": [
                        {
                            "ts_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
                            "event": ev,
                            "instrument": e.instrument,
                        }
                    ],
                    "end_utc": None,
                    "resolution_time_ms": None,
                    "classification": "NORMAL",
                    "classification_detail": "NORMAL RECOVERY",
                }
                last_ts = e.ts_utc
                chain_start_ts = e.ts_utc
            continue

        if is_stable:
            cur["end_utc"] = e.ts_utc.isoformat().replace("+00:00", "Z")
            t0 = datetime.fromisoformat(cur["start_utc"].replace("Z", "+00:00"))
            cur["resolution_time_ms"] = int(ms(e.ts_utc) - ms(t0))
            cur["classification"] = "NORMAL"
            cur["classification_detail"] = "NORMAL RECOVERY"
            _truncate_chain_events(cur, max_chain_events)
            cur["events_truncated"] = cur.get("events_truncated", False)
            chains.append(cur)
            cur = None
            last_ts = None
            chain_start_ts = None
            continue

        if gap <= chain_window_ms:
            cur["events"].append(
                {"ts_utc": e.ts_utc.isoformat().replace("+00:00", "Z"), "event": ev, "instrument": e.instrument}
            )
            last_ts = e.ts_utc
        else:
            chains.append(close_chain_failed(cur, last_ts))
            cur = None
            last_ts = None
            chain_start_ts = None
            if is_trig:
                cur = {
                    "id": len(chains) + 1,
                    "start_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
                    "trigger_event": ev,
                    "trigger_instrument": e.instrument or "",
                    "trigger_ts_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
                    "events": [
                        {
                            "ts_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
                            "event": ev,
                            "instrument": e.instrument,
                        }
                    ],
                    "end_utc": None,
                    "resolution_time_ms": None,
                    "classification": "NORMAL",
                    "classification_detail": "NORMAL RECOVERY",
                }
                last_ts = e.ts_utc
                chain_start_ts = e.ts_utc

    if cur is not None:
        cur["classification"] = "FAILED"
        cur["classification_detail"] = "UNSTABLE SYSTEM"
        cur["end_utc"] = None
        cur["resolution_time_ms"] = None
        _truncate_chain_events(cur, max_chain_events)
        chains.append(cur)

    postprocess_chains_stabilizer_mismatch(chains, selected, q_chain_ms)
    return chains


def first_failure_select(
    overall: str,
    chains: List[Dict[str, Any]],
    selected: List[NormEvent],
) -> Optional[Dict[str, Any]]:
    adverse = overall != "OK" or any(c.get("classification") in ("FAILED", "DEGRADED") for c in chains)
    if not adverse:
        return None
    rank_map = {
        "ENGINE_TICK_STALL_DETECTED": 1,
        "STATE_CONSISTENCY_GATE_ENGAGED": 2,
        "REGISTRY_BROKER_DIVERGENCE": 3,
        "ADOPTION_NON_CONVERGENCE_ESCALATED": 4,
        "RECONCILIATION_MISMATCH_DETECTED": 5,
    }

    def rank(ev: str) -> int:
        for k, v in rank_map.items():
            if ev == k or ev.startswith(k):
                return v
        return 99

    cands: List[Tuple[datetime, int, NormEvent]] = []
    for e in selected:
        cands.append((e.ts_utc, rank(e.event or ""), e))
    cands.sort(key=lambda x: (x[0], x[1], x[2].event, x[2].instrument))
    if not cands:
        return None
    tiered = [c for c in cands if c[1] < 99]
    if tiered:
        _, _, e = tiered[0]
    else:
        warn_events = [
            e
            for e in selected
            if (e.level or "").upper() in ("WARN", "WARNING")
            or "WARN" in (e.level or "").upper()
        ]
        warn_events.sort(key=lambda x: (x.ts_utc, x.event, x.instrument))
        if not warn_events:
            _, _, e = cands[0]
        else:
            e = warn_events[0]
    chain_id = None
    for c in chains:
        for ev in c.get("events") or []:
            if ev.get("event") == e.event and ev.get("ts_utc") == e.ts_utc.isoformat().replace("+00:00", "Z"):
                chain_id = c.get("id")
                break
    if ZoneInfo is None:
        ch = e.ts_utc.strftime("%H:%M:%S")
    else:
        ch = e.ts_utc.astimezone(ZoneInfo(CHICAGO)).strftime("%H:%M:%S")
    return {
        "ts_chicago": ch,
        "ts_utc": e.ts_utc.isoformat().replace("+00:00", "Z"),
        "event": e.event,
        "instrument": e.instrument,
        "chain_id": chain_id,
    }


def recovery_quality_from_chains(chains: List[Dict[str, Any]]) -> Dict[str, Any]:
    res_times = [c["resolution_time_ms"] for c in chains if c.get("resolution_time_ms")]
    norm = sum(1 for c in chains if c.get("classification") == "NORMAL")
    deg = sum(1 for c in chains if c.get("classification") == "DEGRADED")
    fail = sum(1 for c in chains if c.get("classification") == "FAILED")
    return {
        "avg_recovery_time_ms": int(sum(res_times) / len(res_times)) if res_times else None,
        "max_recovery_time_ms": max(res_times) if res_times else None,
        "mismatch_to_recovery_median_ms": None,
        "mismatch_to_recovery_max_ms": None,
        "successful_recoveries": norm,
        "failed_recoveries": fail,
        "chains_classified_normal": norm,
        "chains_classified_degraded": deg,
        "chains_classified_failed": fail,
    }


def count_prior_engine_full_days(reports_dir: Path, audit_date: date) -> int:
    """Count prior days eligible for drift: engine_full and confidence not LOW."""
    n = 0
    if not reports_dir.is_dir():
        return 0
    for p in reports_dir.glob("*.json"):
        try:
            with p.open("r", encoding="utf-8") as f:
                o = json.load(f)
            ad = o.get("audit_date")
            if not ad:
                continue
            pd = date.fromisoformat(str(ad)[:10])
            if pd >= audit_date:
                continue
            if str(o.get("confidence") or "").upper() == "LOW":
                continue
            meta = o.get("meta") or {}
            if meta.get("audit_mode") == "PARTIAL":
                continue
            if meta.get("engine_full") is True:
                n += 1
        except Exception:
            continue
    return n


def watchdog_crosscheck(
    robot_events: List[NormEvent], feed_path: Optional[Path], day_start: datetime, day_end: datetime, max_delta: int
) -> Dict[str, Any]:
    if not feed_path or not feed_path.is_file():
        return {"available": False, "pairs_compared": [], "max_count_delta": None, "divergence": False}
    feed_events: List[NormEvent] = []
    st: Dict[str, Any] = {}
    feed_events.extend(ingest_jsonl_file(feed_path, day_start, day_end, st))
    keys = ["CONNECTION_LOST", "ENGINE_STALLED", "RECONCILIATION_QTY_MISMATCH"]
    deltas = []
    pairs = []
    for k in keys:
        rc = sum(1 for e in robot_events if (e.event or "") == k or ((e.event or "").find(k) >= 0 and k in (e.event or "")))
        fc = sum(1 for e in feed_events if (e.event or "") == k or k in (e.event or ""))
        if k == "CONNECTION_LOST":
            rc = sum(1 for e in robot_events if e.event == "CONNECTION_LOST")
            fc = sum(1 for e in feed_events if e.event == "CONNECTION_LOST")
        deltas.append(abs(rc - fc))
        pairs.append(k)
    mx = max(deltas) if deltas else 0
    return {
        "available": True,
        "pairs_compared": pairs,
        "max_count_delta": mx,
        "divergence": mx > max_delta,
    }


def _max_gap_ms(sorted_ts: List[datetime]) -> float:
    if len(sorted_ts) < 2:
        return 0.0
    mx = 0.0
    for a, b in zip(sorted_ts, sorted_ts[1:]):
        g = (b - a).total_seconds() * 1000.0
        mx = max(mx, g)
    return mx


def _severity_rank(s: Optional[str]) -> int:
    u = (s or "").upper()
    if "CRITICAL" in u:
        return 3
    if "WARN" in u:
        return 2
    return 1


def _merge_silent_cluster(cluster: List[Dict[str, Any]]) -> Dict[str, Any]:
    best = max(cluster, key=lambda x: _severity_rank(x.get("severity")))
    earliest = min(
        cluster,
        key=lambda x: x.get("ts_utc") if isinstance(x.get("ts_utc"), datetime) else datetime.max.replace(tzinfo=timezone.utc),
    )
    out: Dict[str, Any] = {
        "code": earliest.get("code"),
        "reason": earliest.get("reason"),
        "severity": best.get("severity"),
        "instrument": earliest.get("instrument") or "",
    }
    tu = earliest.get("ts_utc")
    if isinstance(tu, datetime):
        out["ts_utc"] = tu.isoformat().replace("+00:00", "Z")
    return out


def dedupe_silent_failure_signals(raw: List[Dict[str, Any]], window_ms: float) -> List[Dict[str, Any]]:
    """Group by (instrument, reason); merge consecutive rows within window_ms (chain gaps)."""
    if not raw:
        return []
    from collections import defaultdict

    buckets: Dict[Tuple[str, str], List[Dict[str, Any]]] = defaultdict(list)
    for s in raw:
        key = (str(s.get("instrument") or ""), str(s.get("reason") or ""))
        buckets[key].append(s)
    out: List[Dict[str, Any]] = []
    for key in sorted(buckets.keys()):
        bucket = buckets[key]
        bucket.sort(
            key=lambda x: x.get("ts_utc")
            if isinstance(x.get("ts_utc"), datetime)
            else datetime.min.replace(tzinfo=timezone.utc)
        )
        i = 0
        while i < len(bucket):
            cluster = [bucket[i]]
            prev = bucket[i].get("ts_utc")
            if not isinstance(prev, datetime):
                prev = datetime.min.replace(tzinfo=timezone.utc)
            j = i + 1
            while j < len(bucket):
                tj = bucket[j].get("ts_utc")
                if not isinstance(tj, datetime):
                    break
                if not isinstance(prev, datetime):
                    break
                if (tj - prev).total_seconds() * 1000.0 <= window_ms:
                    cluster.append(bucket[j])
                    prev = tj
                    j += 1
                else:
                    break
            out.append(_merge_silent_cluster(cluster))
            i = j
    return out


def silent_failure_scan(
    events: List[NormEvent],
    scratch: Dict[str, Any],
    audit_mode: str,
    thresholds: Dict[str, Any],
    day_start: datetime,
    day_end: datetime,
    engine_activity_detected: bool,
) -> Dict[str, Any]:
    raw: List[Dict[str, Any]] = []
    t_prof = float(thresholds.get("silent_t_profile_gap_min", 18)) * 60.0 * 1000.0
    t_hb = float(thresholds.get("silent_t_heartbeat_min", 5)) * 60.0 * 1000.0
    t_silent = float(thresholds.get("silent_t_silent_min", 20)) * 60.0 * 1000.0
    group_ms = float(thresholds.get("silent_failure_group_window_ms", 180000))

    cpu_ts: List[datetime] = []
    hb_ts: List[datetime] = []
    activity_ts: List[datetime] = []

    for e in events:
        ev = e.event or ""
        if ev == "ENGINE_CPU_PROFILE":
            cpu_ts.append(e.ts_utc)
        if ev == "ENGINE_TIMER_HEARTBEAT":
            hb_ts.append(e.ts_utc)
        if ev == "RECONCILIATION_PASS_SUMMARY":
            activity_ts.append(e.ts_utc)
        if ev in ("ORDER_SUBMITTED", "EXECUTION_FILLED", "ENTRY_FILLED", "EXIT_FILLED"):
            activity_ts.append(e.ts_utc)
        if ev == "LIFECYCLE_TRANSITIONED" or "LIFECYCLE" in ev:
            activity_ts.append(e.ts_utc)

    cpu_ts.sort()
    hb_ts.sort()
    activity_ts.sort()

    engine_lines = int(scratch.get("engine_stream_lines") or 0)
    cpu_n = int(scratch.get("cpu_profiles") or 0)
    eng_act = bool(scratch.get("engine_activity_detected"))

    if audit_mode == "FULL" and engine_lines > 100 and cpu_n == 0:
        raw.append(
            {
                "code": "SILENT_FAILURE_DETECTED",
                "reason": "engine_active_but_no_cpu_profiles",
                "severity": "WARNING",
                "instrument": "",
                "ts_utc": day_start,
            }
        )

    if eng_act and cpu_ts:
        if _max_gap_ms(cpu_ts) > t_prof:
            raw.append(
                {
                    "code": "SILENT_FAILURE_DETECTED",
                    "reason": "engine_cpu_profile_gap_exceeds_T_profile_gap",
                    "severity": "WARNING",
                    "instrument": "",
                    "ts_utc": cpu_ts[len(cpu_ts) // 2],
                }
            )
    elif eng_act and not cpu_ts and (day_end - day_start).total_seconds() * 1000.0 > t_prof:
        raw.append(
            {
                "code": "SILENT_FAILURE_DETECTED",
                "reason": "no_engine_cpu_profile_for_T_profile_gap",
                "severity": "WARNING",
                "instrument": "",
                "ts_utc": day_start,
            }
        )

    if eng_act and hb_ts:
        if _max_gap_ms(hb_ts) > t_hb:
            raw.append(
                {
                    "code": "SILENT_FAILURE_DETECTED",
                    "reason": "engine_timer_heartbeat_gap_exceeds_T_heartbeat",
                    "severity": "WARNING",
                    "instrument": "",
                    "ts_utc": hb_ts[len(hb_ts) // 2],
                }
            )
    elif eng_act and not hb_ts and (day_end - day_start).total_seconds() * 1000.0 > t_hb:
        raw.append(
            {
                "code": "SILENT_FAILURE_DETECTED",
                "reason": "no_engine_timer_heartbeat_for_T_heartbeat",
                "severity": "WARNING",
                "instrument": "",
                "ts_utc": day_start,
            }
        )

    if eng_act and len(activity_ts) >= 2:
        if _max_gap_ms(activity_ts) > t_silent:
            raw.append(
                {
                    "code": "SILENT_FAILURE_DETECTED",
                    "reason": "engine_active_but_no_reco_exec_lifecycle_for_T_silent",
                    "severity": "WARNING",
                    "instrument": "",
                    "ts_utc": activity_ts[len(activity_ts) // 2],
                }
            )
    elif eng_act and not activity_ts and (day_end - day_start).total_seconds() * 1000.0 > t_silent:
        raw.append(
            {
                "code": "SILENT_FAILURE_DETECTED",
                "reason": "engine_active_but_no_reconciliation_execution_or_lifecycle",
                "severity": "WARNING",
                "instrument": "",
                "ts_utc": day_start,
            }
        )

    raw_count = len(raw)
    signals = dedupe_silent_failure_signals(raw, group_ms)
    return {
        "detected": len(signals) > 0,
        "signals": signals,
        "signals_before_dedupe": raw_count,
    }


def run_audit(args: argparse.Namespace) -> int:
    project_root = resolve_project_root()
    thresholds = load_thresholds(project_root)
    log_dir = resolve_robot_log_dir(args.log_dir)
    reports_dir = resolve_reports_dir(args.reports_dir)
    reports_dir.mkdir(parents=True, exist_ok=True)

    audit_date = parse_audit_date(args.date)
    day_start, day_end = day_window_utc(audit_date, args.tz)
    rolling_window_minutes: Optional[int] = args.last_minutes
    if rolling_window_minutes is not None and rolling_window_minutes > 0:
        day_end = datetime.now(timezone.utc)
        day_start = day_end - timedelta(minutes=int(rolling_window_minutes))

    stats: Dict[str, Any] = {
        "files_read": 0,
        "lines_read": 0,
        "parse_errors_count": 0,
        "journal_files_used": 0,
    }
    raw_events: List[NormEvent] = []

    for path in collect_robot_jsonl_paths(log_dir):
        raw_events.extend(ingest_jsonl_file(path, day_start, day_end, stats))

    feed_path: Optional[Path] = None
    wd = collect_watchdog_paths(log_dir)
    if wd:
        feed_path = wd[0]

    journal_events = ingest_journal_snapshots(log_dir, audit_date, day_start, day_end, stats)

    min_eng = int(thresholds.get("min_engine_lines_for_full_audit", 50))

    events_robot, deduped_robot = dedupe_events(raw_events)
    logical_cpu = int(os.cpu_count() or 1)
    domains, scratch_robot, overall = compute_domains(events_robot, thresholds, logical_cpu)
    engine_full = evaluate_engine_full(scratch_robot, min_eng)
    audit_mode = "FULL" if engine_full else "PARTIAL"

    if audit_mode == "PARTIAL" and journal_events:
        events, deduped_n = dedupe_events(list(events_robot) + journal_events)
        domains, scratch, overall = compute_domains(events, thresholds, logical_cpu)
    else:
        events = events_robot
        deduped_n = deduped_robot
        scratch = scratch_robot

    stats["events_deduped"] = deduped_n
    stats["events_used"] = len(events)

    engine_lines_robot = int(scratch_robot.get("engine_stream_lines") or 0)
    engine_activity_detected = bool(scratch_robot.get("engine_activity_detected"))
    engine_full = evaluate_engine_full(scratch_robot, min_eng)

    wc = watchdog_crosscheck(
        events, feed_path, day_start, day_end, int(thresholds.get("watchdog_max_count_delta", 2))
    )

    parse_total = max(1, stats.get("lines_read", 0))
    parse_rate = stats.get("parse_errors_count", 0) / parse_total
    if audit_mode == "PARTIAL" and engine_lines_robot < min_eng:
        confidence = "LOW"
    elif audit_mode == "PARTIAL":
        confidence = "MEDIUM"
    elif parse_rate > 0.02 or wc.get("divergence"):
        confidence = "MEDIUM"
    else:
        confidence = "HIGH"

    if stats.get("source_fallback"):
        confidence = "LOW"

    selected = select_timeline_events(events, thresholds)
    tw = int(thresholds.get("timeline_collapse_window_ms", 120000))
    collapsed = collapse_timeline_burst(selected, tw)
    tmax = int(thresholds.get("timeline_max_entries", 400))
    timeline_truncated = len(collapsed) > tmax
    if timeline_truncated:
        collapsed = collapsed[:tmax]
    timeline_entries, critical_periods = build_compressed_timeline(collapsed, args.tz)
    chain_ms = int(thresholds.get("chain_window_ms", 120000))
    max_span = int(thresholds.get("max_chain_span_ms", 1800000))
    max_cev = int(thresholds.get("max_chain_events", 80))
    q_chain_ms = int(thresholds.get("chain_quiet_gap_ms", 300000))
    chains = incident_chains_from_timeline(selected, chain_ms, max_span, max_cev, q_chain_ms)
    rq = recovery_quality_from_chains(chains)

    silent = silent_failure_scan(
        events,
        scratch,
        audit_mode,
        thresholds,
        day_start,
        day_end,
        engine_activity_detected,
    )
    if silent["detected"] and overall == "OK":
        overall = "WARNING"

    summary = {
        "total_trades": None,
        "total_mismatches": scratch.get("mismatch"),
        "total_disconnects": scratch.get("disconnect_lost"),
        "max_cpu_parallelism_raw": scratch.get("parallelism_raw_max"),
        "max_cpu_parallelism_normalized": scratch.get("parallelism_norm_max"),
        "dominant_subsystem": scratch.get("dominant_subsystem"),
        "first_failure": None,
        "recovery_quality": rq,
    }

    apply_partial_mode_gating(domains, summary, audit_mode)
    if audit_mode == "PARTIAL":
        overall = overall_from_domains(domains, scratch)

    if silent["detected"] and overall == "OK":
        overall = "WARNING"

    enforced = confidence_enforced(confidence, overall)
    ff = first_failure_select(overall, chains, selected)
    summary["first_failure"] = ff

    prior_full = count_prior_engine_full_days(reports_dir, audit_date)
    min_drift_days = int(thresholds.get("min_engine_full_history_days_for_drift", 5))
    enable_drift = prior_full >= min_drift_days

    drift_block: Optional[Dict[str, Any]] = None
    drift_risk = "NONE"
    drift_disabled_reason: Optional[str] = None
    if not enable_drift:
        drift_block = None
        drift_risk = "NONE"
        drift_disabled_reason = "prior_engine_full_days_below_min"
    else:
        drift_block = {
            "window_days": int(thresholds.get("drift_window_days", 14)),
            "mismatch_rate_trend": "STABLE",
            "cpu_trend": "STABLE",
            "recovery_time_trend": "STABLE",
            "queue_wait_trend": "STABLE",
            "notes": ["baseline_history_insufficient_or_not_implemented"],
        }

    safety_status = domains["safety"]["status"]
    exec_status = domains["execution"]["status"]
    tr_decision, tr_reason, tr_block = trade_readiness_decision(
        overall,
        confidence,
        enforced,
        safety_status,
        exec_status,
        silent["detected"],
        drift_risk,
        audit_mode,
    )
    tr_block = list(tr_block)
    if audit_mode == "PARTIAL" and tr_decision == "CAUTION" and "PARTIAL_AUDIT_MODE" not in tr_block:
        tr_block.append("PARTIAL_AUDIT_MODE")

    generated = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")

    meta: Dict[str, Any] = {
        "files_read": stats.get("files_read", 0),
        "lines_read": stats.get("lines_read", 0),
        "events_used": stats.get("events_used", 0),
        "events_deduped": stats.get("events_deduped", 0),
        "parse_errors_count": stats.get("parse_errors_count", 0),
        "parse_error_rate": round(parse_rate, 6),
        "timeline_raw_events": len(selected),
        "timeline_compressed_lines": len(timeline_entries),
        "timeline_truncated": timeline_truncated,
        "timeline_max_entries": tmax,
        "source_primary": "robot",
        "source_fallback": bool(stats.get("source_fallback", False)),
        "source_fallback_reason": stats.get("source_fallback_reason"),
        "logical_processor_count": logical_cpu,
        "parallelism_normalized_max": None
        if audit_mode == "PARTIAL"
        else scratch.get("parallelism_norm_max"),
        "engine_active": engine_lines_robot > 0 or bool(scratch_robot.get("engine_start_hint")),
        "engine_activity_detected": engine_activity_detected,
        "domains_complete": audit_mode == "FULL",
        "watchdog_crosscheck": wc,
        "audit_mode": audit_mode,
        "engine_full": engine_full,
        "engine_lines_in_window": engine_lines_robot,
        "journal_snapshots_used": stats.get("journal_files_used", 0),
        "partial_reason": None
        if audit_mode == "FULL"
        else (
            "insufficient_engine_lines_or_activity"
            if engine_lines_robot < min_eng or not engine_activity_detected
            else "instrument_and_journal_only"
        ),
        "drift_disabled_reason": drift_disabled_reason,
        "prior_engine_full_days": prior_full,
        "min_engine_full_days_for_drift": min_drift_days,
    }
    if rolling_window_minutes is not None and rolling_window_minutes > 0:
        meta["audit_window"] = {
            "mode": "rolling",
            "last_minutes": rolling_window_minutes,
            "window_start_utc": day_start.isoformat().replace("+00:00", "Z"),
            "window_end_utc": day_end.isoformat().replace("+00:00", "Z"),
        }

    out_obj: Dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "audit_date": audit_date.isoformat(),
        "timezone": args.tz,
        "generated_at_utc": generated,
        "confidence": confidence,
        "confidence_enforced_status": enforced,
        "trade_readiness": {
            "decision": tr_decision,
            "reason": tr_reason,
            "blocking_factors": tr_block,
        },
        "meta": meta,
        "overall_status": overall,
        "summary": summary,
        "silent_failure": silent,
        "drift": drift_block,
        "drift_risk": drift_risk,
        "domains": domains,
        "timeline": {"critical_periods": critical_periods, "compressed_entries": timeline_entries},
        "incident_chains": chains,
        "engine_load_analysis": (
            el_block := compute_engine_load_analysis(events, day_start, day_end, thresholds, args.tz)
        ),
        "iea_integrity": analyze_iea_integrity(
            events,
            thresholds,
            engine_load_analysis=el_block,
            audit_window_start=day_start,
            audit_window_end=day_end,
        ),
        "convergence_failure_explanation": analyze_convergence_failure_explanations(
            events,
            chains,
            thresholds,
        ),
    }

    json_path = Path(args.json_out) if args.json_out else reports_dir / f"{audit_date.isoformat()}.json"
    json_path.parent.mkdir(parents=True, exist_ok=True)
    with json_path.open("w", encoding="utf-8") as f:
        json.dump(out_obj, f, indent=2, sort_keys=True)

    md_path = Path(args.md_out) if args.md_out else reports_dir / f"{audit_date.isoformat()}.md"
    md_lines = render_markdown(out_obj)
    with md_path.open("w", encoding="utf-8") as f:
        f.write("\n".join(md_lines))

    print(f"Wrote {json_path}")
    print(f"Wrote {md_path}")
    print(f"audit_mode={audit_mode} engine_full={engine_full} prior_engine_full_days={prior_full} drift_enabled={enable_drift}")
    if rolling_window_minutes is not None and rolling_window_minutes > 0:
        print(
            f"audit_window=rolling last_{rolling_window_minutes}m "
            f"{day_start.isoformat()} .. {day_end.isoformat()}"
        )
    return 0


def render_markdown(o: Dict[str, Any]) -> List[str]:
    lines: List[str] = []
    lines.append("🔷 DAILY REPORT")
    lines.append(f"DATE: {o.get('audit_date')}")
    lines.append(f"TIMEZONE: {o.get('timezone')}")
    meta_top = o.get("meta") or {}
    aw = meta_top.get("audit_window")
    if isinstance(aw, dict) and aw.get("mode") == "rolling":
        lines.append(
            f"AUDIT WINDOW: rolling last {aw.get('last_minutes')} minutes (UTC window ending at run time)"
        )
        lines.append(f"  window_start_utc: {aw.get('window_start_utc')}")
        lines.append(f"  window_end_utc: {aw.get('window_end_utc')}")
    lines.append("")
    lines.append(f"OVERALL STATUS: {o.get('overall_status')}")
    lines.append(f"EFFECTIVE STATUS (confidence-enforced): {o.get('confidence_enforced_status')}")
    lines.append("")
    tr = o.get("trade_readiness") or {}
    lines.append(f"TRADE READINESS: {tr.get('decision')}")
    lines.append(f"Reason: {tr.get('reason')}")
    lines.append("")
    lines.append("[SUMMARY]")
    lines.append(f"Confidence: {o.get('confidence')}")
    meta = o.get("meta") or {}
    lines.append(f"Audit mode: {meta.get('audit_mode')} (engine_full={meta.get('engine_full')})")
    s = o.get("summary") or {}
    lines.append(f"- Total mismatches: {s.get('total_mismatches')}")
    lines.append(f"- Total disconnects: {s.get('total_disconnects')}")
    lines.append(f"- First failure: {s.get('first_failure')}")
    lines.append("")
    lines.append("[ENGINE LOAD ANALYSIS]")
    el = o.get("engine_load_analysis") or {}
    if not el:
        lines.append("(unavailable)")
    else:
        lines.append(f"load_classification: {el.get('load_classification')}")
        notes = el.get("classification_notes") or []
        if notes:
            lines.append("classification_notes: " + "; ".join(str(n) for n in notes))
        lines.append(
            f"peak_events_per_second (5s bucket): {el.get('peak_events_per_second')} | "
            f"peak_subwindow_eps ({el.get('subwindow_seconds', 1)}s worst sub-window): {el.get('peak_subwindow_eps')} | "
            f"average_events_per_second: {el.get('average_events_per_second')} | "
            f"total_events: {el.get('total_events_in_window')}"
        )
        lines.append(
            f"reconciliation_event_ratio: {el.get('reconciliation_event_ratio')} "
            f"(upgrade if > { (el.get('classification_thresholds') or {}).get('reconciliation_ratio_upgrade', 0.5) })"
        )
        lines.append(f"total_storm_time_seconds: {el.get('total_storm_time_seconds')}")
        st = el.get("storm_thresholds") or {}
        lines.append(
            f"storm rule: eps > {st.get('events_per_second_gt')} for ≥ {st.get('min_consecutive_windows')} consecutive {el.get('bucket_seconds')}s windows | "
            f"escalate if total storm time > {st.get('total_time_escalate_seconds')}s | "
            f"CRITICAL if storm_type=RECONCILIATION and duration > {st.get('reconciliation_storm_duration_critical_seconds')}s"
        )
        storms = el.get("storms") or []
        lines.append(f"storms_detected: {len(storms)}")
        for si, stp in enumerate(storms[:12], 1):
            lc_st = stp.get("loop_counts_inside_storm") or {}
            lines.append(
                f"  STORM #{si} {stp.get('storm_type')} {stp.get('start_time_audit_tz')} → {stp.get('end_time_audit_tz')} | "
                f"duration_s={stp.get('duration_seconds')} avg_eps={stp.get('avg_eps')} max_eps={stp.get('max_eps')} | "
                f"peak_reconciliation_ratio={stp.get('peak_reconciliation_ratio')} | "
                f"loop_counts: recon={lc_st.get('reconciliation_loop_count')} adopt={lc_st.get('adoption_loop_count')} | "
                f"stall_overlap={stp.get('overlap_engine_tick_stall')} disconnect_overlap={stp.get('overlap_connection_lost')}"
            )
        if len(storms) > 12:
            lines.append(f"  … ({len(storms)} total storms)")
        loops = el.get("loop_signatures") or []
        lines.append(f"loop_signatures: {len(loops)}")
        for lp in loops[:8]:
            lines.append(
                f"  {lp.get('loop_classification')} / {lp.get('subtype')}: "
                f"max_repeats={lp.get('max_repeats_in_window')} in {lp.get('window_sec')}s window "
                f"@ {lp.get('peak_window_start_audit_tz')}"
            )
        topd = el.get("top_dense_buckets") or []
        lines.append(
            f"top_dense_buckets (highest eps; nonzero 5s windows): {el.get('nonzero_bucket_count')} nonzero buckets, "
            f"{len(topd)} rows exported"
        )
        for tb in topd[:15]:
            lines.append(
                f"  coarse_eps={tb.get('events_per_second')} peak_subwindow_eps={tb.get('peak_subwindow_eps')} "
                f"total={tb.get('total_events')} recon={tb.get('reconciliation_events')} adopt={tb.get('adoption_events')} | "
                f"{tb.get('start_time_utc')}"
            )
        if len(topd) > 15:
            lines.append(f"  … ({len(topd)} rows exported)")
    lines.append("")
    lines.append("[IEA INTEGRITY]")
    iea = o.get("iea_integrity") or {}
    lines.append(f"Status: {iea.get('status', '—')}")
    m = (iea.get("metrics") or {}) if isinstance(iea.get("metrics"), dict) else {}
    lines.append(f"- Resolution suspect events: {m.get('iea_resolution_suspect_count', '—')}")
    rtg = m.get("registry_timing_gap") or {}
    if isinstance(rtg, dict):
        lines.append(
            f"- Registry timing gaps: count={rtg.get('count', '—')} "
            f"avg_delay_ms={rtg.get('avg_delay_ms', '—')} max_delay_ms={rtg.get('max_delay_ms', '—')}"
        )
    else:
        lines.append("- Registry timing gaps: —")
    lines.append(f"- Lifecycle misrouting (CREATED→ENTRY_ACCEPTED invalid): {m.get('lifecycle_wrong_adapter_count', '—')}")
    reff = m.get("reconciliation_efficiency") or {}
    if isinstance(reff, dict):
        avg_e = reff.get("avg_efficiency")
        lowi = reff.get("low_efficiency_instruments") or []
        low_note = ""
        if isinstance(lowi, list) and lowi:
            low_note = (
                f" | low_eff instruments (sample): "
                f"{', '.join(str(x.get('instrument', x)) if isinstance(x, dict) else str(x) for x in lowi[:8])}"
            )
        eff_tag = ""
        if isinstance(avg_e, (int, float)) and avg_e < 0.05:
            eff_tag = " (LOW)"
        lines.append(f"- Reconciliation efficiency (avg): {avg_e}{eff_tag}{low_note}")
    else:
        lines.append("- Reconciliation efficiency: —")
    lines.append(f"- Cross-IEA duplicate events: {m.get('cross_iea_duplicate_events', '—')}")
    pr = m.get("progress_reconciliation") if isinstance(m.get("progress_reconciliation"), dict) else {}
    dt = pr.get("day_totals") if isinstance(pr.get("day_totals"), dict) else {}
    ss = pr.get("stuck_state") if isinstance(pr.get("stuck_state"), dict) else {}
    if dt:
        lines.append(
            f"- Progress reconciliation (day): cycles={dt.get('total_reconciliation_cycles', '—')} "
            f"progress_events={dt.get('progress_events', '—')} no_progress_cycles={dt.get('no_progress_cycles', '—')} "
            f"throttled_cycles={dt.get('throttled_cycles', '—')} "
            f"progress_efficiency_ratio={dt.get('progress_efficiency_ratio', '—')} "
            f"wasted_work_ratio={dt.get('wasted_work_ratio', '—')}"
        )
        if dt.get("estimated_cpu_waste_ms") is not None:
            lines.append(
                f"  (avg_gate_cycle_ms={dt.get('avg_gate_reconciliation_duration_ms', '—')} "
                f"estimated_cpu_waste_ms={dt.get('estimated_cpu_waste_ms')})"
            )
    if ss:
        lines.append(
            f"- Stuck state: detected={ss.get('stuck_state_detected', '—')} "
            f"stuck_duration_ms={ss.get('stuck_duration_ms', '—')} "
            f"reasons={ss.get('stuck_reasons', '—')}"
        )
    tc = m.get("throttle_correctness") if isinstance(m.get("throttle_correctness"), dict) else {}
    if tc:
        lines.append(
            f"- Throttle correctness: expected={tc.get('expected_throttle_activation_count', '—')}, "
            f"actual={tc.get('actual_throttle_activation_count', '—')}, "
            f"missed={tc.get('missed_throttle_opportunities', '—')}, "
            f"effectiveness={tc.get('throttle_effectiveness_ratio', '—')}"
        )
    pq = m.get("progress_quality") if isinstance(m.get("progress_quality"), dict) else {}
    if pq:
        qd = pq.get("quality_breakdown") if isinstance(pq.get("quality_breakdown"), dict) else {}
        good = (
            int(qd.get("mismatch_cleared", 0) or 0)
            + int(qd.get("mismatch_reduced", 0) or 0)
            + int(qd.get("ownership_improved", 0) or 0)
        )
        weak = int(qd.get("state_only_progress", 0) or 0)
        bad = int(qd.get("reverted_progress", 0) or 0)
        lines.append(
            f"- Progress quality: score={pq.get('progress_quality_score', '—')}, "
            f"good={good}, weak={weak}, reverted={bad} "
            f"(explicit={pq.get('explicit_progress_events', '—')}, inferred={pq.get('inferred_progress_events', '—')})"
        )
    rc = m.get("iea_root_cause") if isinstance(m.get("iea_root_cause"), dict) else {}
    prim = rc.get("primary") if isinstance(rc.get("primary"), dict) else None
    if prim:
        lines.append(
            f"- Root cause (hypothesis): {prim.get('root_cause', '—')} [{prim.get('case_id', '—')}] "
            f"confidence={prim.get('confidence', '—')} — {prim.get('likely_reason', '—')}"
        )
    evo = m.get("iea_expectation_vs_observed") if isinstance(m.get("iea_expectation_vs_observed"), dict) else {}
    if evo:
        for row_key in ("throttle_activation", "progress_events", "adoption_success"):
            row = evo.get(row_key) if isinstance(evo.get(row_key), dict) else {}
            if row:
                lines.append(
                    f"- Expected vs observed: {row.get('mechanism', row_key)} — "
                    f"expected={row.get('expected', '—')} observed={row.get('observed', '—')}"
                )
    tss = m.get("time_spent_in_states") if isinstance(m.get("time_spent_in_states"), dict) else {}
    pct = tss.get("percentages") if isinstance(tss.get("percentages"), dict) else {}
    if pct:
        top = ", ".join(f"{k}={v}%" for k, v in list(pct.items())[:8])
        lines.append(f"- Time in states (approx): {top}")
    elif tss.get("note"):
        lines.append(f"- Time in states: ({tss.get('note')})")
    corr = m.get("correlations") if isinstance(m.get("correlations"), dict) else {}
    crules = corr.get("rules") if isinstance(corr.get("rules"), list) else []
    if crules:
        lines.append("- Cross-layer correlations:")
        for cr in crules[:6]:
            if isinstance(cr, dict) and cr.get("interpretation"):
                lines.append(f"  • [{cr.get('id')}]: {cr.get('interpretation')}")
    oi = m.get("observability_integrity") if isinstance(m.get("observability_integrity"), dict) else {}
    if oi:
        lines.append(
            f"- Observability integrity: signal_reliability_score={oi.get('signal_reliability_score', '—')} "
            f"max_gap_s={oi.get('max_inter_event_gap_seconds', '—')} "
            f"gate_result_without_prior_start={oi.get('gate_result_without_prior_start_count', '—')}"
        )
    cpu_l = m.get("cpu_root_cause_link") if isinstance(m.get("cpu_root_cause_link"), dict) else {}
    if cpu_l and cpu_l.get("cpu_cause_hypothesis"):
        lines.append(
            f"- CPU load hypothesis: {cpu_l.get('cpu_cause_hypothesis')} "
            f"(aligned_iea={cpu_l.get('aligned_iea_root_cause', '—')})"
        )
    findings = iea.get("findings") or []
    key_line = None
    if isinstance(findings, list) and findings and str(iea.get("status") or "").upper() != "OK":
        for f in findings:
            if not isinstance(f, str):
                continue
            fl = f.lower()
            if "insufficient signals" in fl or "no iea integrity alerts" in fl:
                continue
            key_line = f
            break
        if key_line is None and isinstance(findings[0], str):
            key_line = findings[0]
    if key_line:
        lines.append("")
        lines.append("Key finding:")
        lines.append(key_line)
    lines.append("")
    cfe = o.get("convergence_failure_explanation")
    if isinstance(cfe, dict) and cfe:
        lines.extend(render_convergence_markdown(cfe, str(o.get("timezone") or "chicago")))
    else:
        lines.append("[CONVERGENCE FAILURE EXPLANATION]")
        lines.append("(none)")
    lines.append("")
    lines.append("[TIMELINE]")
    if meta.get("timeline_truncated"):
        cap = meta.get("timeline_max_entries", 400)
        lines.append(f"⚠ Timeline truncated ({cap} entries cap)")
    tl = (o.get("timeline") or {}).get("compressed_entries") or []
    if not tl:
        lines.append("— no high-signal events in window —")
    else:
        for row in tl[:200]:
            lines.append(
                f"[{row.get('display_start_chicago')}] {row.get('event')} — {row.get('summary')} ({row.get('instrument')})"
            )
    lines.append("")
    lines.append("[INCIDENT CHAINS]")
    ich = o.get("incident_chains") or []
    if not ich:
        lines.append("(none)")
    else:
        for c in ich:
            lines.append(f"INCIDENT #{c.get('id')} — {c.get('classification')} — trigger {c.get('trigger_event')}")
    return lines


def build_arg_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="Daily Robot+Watchdog audit")
    p.add_argument("--date", required=True, help="Audit calendar date YYYY-MM-DD")
    p.add_argument("--tz", default="chicago", choices=["chicago", "utc"], help="Audit day boundary timezone")
    p.add_argument("--log-dir", default=None, help="Override robot log directory")
    p.add_argument("--reports-dir", default=None, help="Override reports/daily_audit directory")
    p.add_argument("--json-out", default=None, help="Explicit JSON output path")
    p.add_argument("--md-out", default=None, help="Explicit Markdown output path")
    p.add_argument(
        "--last-minutes",
        type=int,
        default=None,
        metavar="N",
        help="Restrict audit to log events in [now - N min, now) UTC (rolling window). --date labels the report file.",
    )
    return p


if __name__ == "__main__":
    raise SystemExit(run_audit(build_arg_parser().parse_args()))
