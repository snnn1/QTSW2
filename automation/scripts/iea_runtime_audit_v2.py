#!/usr/bin/env python3
"""Full IEA runtime audit stats -> stdout JSON."""
from __future__ import annotations

import json
import sys
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path


def parse_ts(s: str) -> datetime | None:
    if not s:
        return None
    try:
        return datetime.fromisoformat(s.replace("Z", "+00:00").replace("+00:00", "+00:00"))
    except Exception:
        return None


def main(root: Path) -> None:
    files = sorted(root.glob("robot*.jsonl")) + sorted((root / "archive").glob("*.jsonl"))
    files = [f for f in files if f.is_file()]

    ec_all: Counter[str] = Counter()
    # ENGINE-only for session slice
    engine_path = root / "robot_ENGINE.jsonl"
    engine_lines: list[dict] = []
    if engine_path.exists():
        for line in engine_path.read_text(encoding="utf-8", errors="replace").splitlines():
            try:
                engine_lines.append(json.loads(line.strip()))
            except json.JSONDecodeError:
                continue

    # Latest session: max ts in ENGINE
    ts_all = [parse_ts(o.get("ts_utc") or "") for o in engine_lines]
    ts_all = [t for t in ts_all if t]
    t_end = max(ts_all) if ts_all else None
    t_start = min(ts_all) if ts_all else None

    # Optional: last 4 hours window for "latest session"
    window_start = (t_end or datetime.now(timezone.utc)).replace(tzinfo=timezone.utc)
    if t_end:
        from datetime import timedelta
        window_start = t_end - timedelta(hours=4)

    def in_window(o: dict) -> bool:
        t = parse_ts(o.get("ts_utc") or "")
        if not t or not t_end:
            return True
        return t >= window_start

    # Aggregate all files
    adoption_exec_complete: list[dict] = []
    adoption_accepted: list[dict] = []
    adoption_skipped: list[dict] = []
    adoption_starts: list[tuple[datetime, dict]] = []
    adoption_summaries: list[tuple[datetime, dict]] = []
    queue_pressure: list[tuple[datetime, dict]] = []
    enqueue_timeout = 0
    enqueue_slow = 0
    enqueue_timing: list[int] = []
    stalls: list[tuple[datetime, dict]] = []
    off_worker = 0
    deferral_hb: list[tuple[datetime, dict]] = []

    for fp in files:
        try:
            text = fp.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for line in text.splitlines():
            try:
                o = json.loads(line.strip())
            except json.JSONDecodeError:
                continue
            ev = o.get("event") or ""
            ec_all[ev] += 1
            ts = parse_ts(o.get("ts_utc") or "")
            d = o.get("data") if isinstance(o.get("data"), dict) else {}
            if ev == "IEA_ADOPTION_SCAN_EXECUTION_COMPLETED":
                adoption_exec_complete.append(d)
            elif ev == "IEA_ADOPTION_SCAN_REQUEST_ACCEPTED":
                adoption_accepted.append(d)
            elif ev == "IEA_ADOPTION_SCAN_REQUEST_SKIPPED":
                adoption_skipped.append(d)
            elif ev == "IEA_ADOPTION_SCAN_OFF_WORKER_VIOLATION":
                off_worker += 1
            elif ev == "ADOPTION_DEFERRAL_HEARTBEAT_RETRY" and ts:
                deferral_hb.append((ts, d))
            elif ev == "ADOPTION_SCAN_START" and ts:
                adoption_starts.append((ts, d))
            elif ev == "ADOPTION_SCAN_SUMMARY" and ts:
                adoption_summaries.append((ts, d))
            elif ev == "IEA_QUEUE_PRESSURE_DIAG" and ts:
                queue_pressure.append((ts, d))
            elif ev == "IEA_ENQUEUE_AND_WAIT_TIMEOUT":
                enqueue_timeout += 1
            elif ev == "IEA_ENQUEUE_AND_WAIT_TIMING" and ts:
                w = d.get("wait_ms") or d.get("elapsed_ms")
                try:
                    if w is not None:
                        enqueue_timing.append(int(w))
                except (TypeError, ValueError):
                    pass
                if d.get("slow") is True or (w and int(w) > 2000):
                    enqueue_slow += 1
            elif ev == "EXECUTION_COMMAND_STALLED" and ts:
                stalls.append((ts, d))

    # ENGINE window slice
    eng_w = [o for o in engine_lines if in_window(o)]
    ec_eng = Counter()
    for o in eng_w:
        ec_eng[o.get("event") or ""] += 1

    total_eng_w = sum(ec_eng.values()) or 1
    b_rec = sum(c for e, c in ec_eng.items() if e.startswith("RECONCILIATION") or e.startswith("RECOVERY"))
    b_sup = sum(c for e, c in ec_eng.items() if "SUPERVISORY" in e.upper())
    b_adopt = sum(c for e, c in ec_eng.items() if "ADOPTION" in e.upper())
    b_heart = sum(c for e, c in ec_eng.items() if "HEARTBEAT" in e.upper())
    b_exec = sum(
        c
        for e, c in ec_eng.items()
        if ("EXEC" in e.upper() and "EXECUTION" in e.upper()) or "IEA_EXEC" in e.upper()
    )
    b_reg = sum(c for e, c in ec_eng.items() if "REGISTRY" in e.upper() or "VERIFY_REGISTRY" in e.upper())

    # Skip reasons
    skip_reasons = Counter()
    for d in adoption_skipped:
        skip_reasons[d.get("disposition") or d.get("reason") or "unknown"] += 1

    # scan_request_source
    src_accepted = Counter()
    for d in adoption_accepted:
        src_accepted[str(d.get("scan_request_source") or "unknown")] += 1
    src_completed = Counter()
    wall_ms_list = []
    episodes = set()
    for d in adoption_exec_complete:
        src_completed[str(d.get("scan_request_source") or "unknown")] += 1
        w = d.get("scan_wall_ms")
        try:
            if w is not None and int(w) >= 0:
                wall_ms_list.append(int(w))
        except (TypeError, ValueError):
            pass
        ep = d.get("adoption_scan_episode_id")
        if ep:
            episodes.add(str(ep))

    # Per-minute rates in window (ENGINE)
    per_min = Counter()
    for o in eng_w:
        t = parse_ts(o.get("ts_utc") or "")
        if not t:
            continue
        key = t.replace(second=0, microsecond=0)
        per_min[(o.get("event") or "", key)] += 1

    # Top bursts: adoption events per minute
    adop_min = defaultdict(int)
    for o in eng_w:
        ev = o.get("event") or ""
        if "ADOPTION" not in ev.upper() and ev not in (
            "IEA_ADOPTION_SCAN_REQUEST_ACCEPTED",
            "IEA_ADOPTION_SCAN_REQUEST_SKIPPED",
            "IEA_ADOPTION_SCAN_EXECUTION_STARTED",
            "IEA_ADOPTION_SCAN_EXECUTION_COMPLETED",
        ):
            continue
        t = parse_ts(o.get("ts_utc") or "")
        if not t:
            continue
        adop_min[t.replace(second=0, microsecond=0)] += 1
    top_adop_min = sorted(adop_min.items(), key=lambda x: -x[1])[:15]

    # Same-IEA overlap START without SUMMARY
    stack: dict[str, int] = defaultdict(int)
    violations = []
    eng_adopt_seq = sorted(
        [
            (parse_ts(o.get("ts_utc") or ""), o.get("event"), o.get("data") or {})
            for o in engine_lines
            if (o.get("event") or "") in ("ADOPTION_SCAN_START", "ADOPTION_SCAN_SUMMARY", "IEA_ADOPTION_SCAN_EXECUTION_STARTED", "IEA_ADOPTION_SCAN_EXECUTION_COMPLETED")
            and parse_ts(o.get("ts_utc") or "")
        ],
        key=lambda x: x[0] or datetime.min.replace(tzinfo=timezone.utc),
    )
    for ts, ev, d in eng_adopt_seq:
        if ts is None:
            continue
        iea = str(d.get("iea_instance_id") or "")
        if ev in ("ADOPTION_SCAN_START", "IEA_ADOPTION_SCAN_EXECUTION_STARTED"):
            stack[iea] += 1
            if stack[iea] > 1:
                violations.append((ts.isoformat(), iea, ev))
        elif ev in ("ADOPTION_SCAN_SUMMARY", "IEA_ADOPTION_SCAN_EXECUTION_COMPLETED"):
            stack[iea] -= 1
            if stack[iea] < 0:
                violations.append(("underflow", ts.isoformat(), iea))

    # Registry in all robot*.jsonl (not archive for primary)
    reg_div = ec_all["REGISTRY_BROKER_DIVERGENCE"]
    reg_adopted = ec_all["REGISTRY_BROKER_DIVERGENCE_ADOPTED"]
    rob_files = [f for f in root.glob("robot*.jsonl") if f.is_file()]
    reg_lines = 0
    for fp in rob_files:
        for line in fp.read_text(encoding="utf-8", errors="replace").splitlines():
            try:
                o = json.loads(line.strip())
            except json.JSONDecodeError:
                continue
            if o.get("event") == "REGISTRY_BROKER_DIVERGENCE":
                reg_lines += 1

    out = {
        "engine_first_ts": t_start.isoformat() if t_start else None,
        "engine_last_ts": t_end.isoformat() if t_end else None,
        "window_hours": 4,
        "window_start_ts": window_start.isoformat() if t_end else None,
        "engine_events_in_window": len(eng_w),
        "pct_window": {
            "reconciliation_recovery": round(100 * b_rec / total_eng_w, 3),
            "supervisory_in_event_name": round(100 * b_sup / total_eng_w, 3),
            "adoption": round(100 * b_adopt / total_eng_w, 3),
            "heartbeat": round(100 * b_heart / total_eng_w, 3),
            "execution_like": round(100 * b_exec / total_eng_w, 3),
            "registry_in_engine": round(100 * b_reg / total_eng_w, 3),
        },
        "iea_adoption_gate": {
            "IEA_ADOPTION_SCAN_REQUEST_ACCEPTED": ec_all["IEA_ADOPTION_SCAN_REQUEST_ACCEPTED"],
            "IEA_ADOPTION_SCAN_REQUEST_SKIPPED": ec_all["IEA_ADOPTION_SCAN_REQUEST_SKIPPED"],
            "IEA_ADOPTION_SCAN_EXECUTION_STARTED": ec_all["IEA_ADOPTION_SCAN_EXECUTION_STARTED"],
            "IEA_ADOPTION_SCAN_EXECUTION_COMPLETED": ec_all["IEA_ADOPTION_SCAN_EXECUTION_COMPLETED"],
            "IEA_ADOPTION_SCAN_OFF_WORKER_VIOLATION": off_worker,
            "skip_disposition_breakdown": dict(skip_reasons),
            "scan_request_source_accepted": dict(src_accepted),
            "scan_request_source_completed": dict(src_completed),
            "distinct_adoption_scan_episode_id": len(episodes),
            "scan_wall_ms_count": len(wall_ms_list),
            "scan_wall_ms_avg": round(sum(wall_ms_list) / len(wall_ms_list), 2) if wall_ms_list else None,
            "scan_wall_ms_max": max(wall_ms_list) if wall_ms_list else None,
        },
        "adoption_legacy": {
            "ADOPTION_SCAN_START_all_files": ec_all["ADOPTION_SCAN_START"],
            "ADOPTION_SCAN_SUMMARY_all_files": ec_all["ADOPTION_SCAN_SUMMARY"],
            "ADOPTION_DEFERRAL_HEARTBEAT_RETRY": ec_all["ADOPTION_DEFERRAL_HEARTBEAT_RETRY"],
        },
        "queue_latency": {
            "IEA_QUEUE_PRESSURE_DIAG": ec_all["IEA_QUEUE_PRESSURE_DIAG"],
            "queue_pressure_samples": len(queue_pressure),
            "IEA_ENQUEUE_AND_WAIT_TIMEOUT": enqueue_timeout,
            "IEA_ENQUEUE_AND_WAIT_TIMING_count": ec_all["IEA_ENQUEUE_AND_WAIT_TIMING"],
            "enqueue_timing_wait_ms_max": max(enqueue_timing) if enqueue_timing else None,
            "enqueue_timing_wait_ms_avg": round(sum(enqueue_timing) / len(enqueue_timing), 2) if enqueue_timing else None,
            "EXECUTION_COMMAND_STALLED": ec_all["EXECUTION_COMMAND_STALLED"],
        },
        "registry_reconciliation": {
            "REGISTRY_BROKER_DIVERGENCE": reg_div,
            "REGISTRY_BROKER_DIVERGENCE_robot_files_linecount": reg_lines,
            "REGISTRY_BROKER_DIVERGENCE_ADOPTED": reg_adopted,
            "ORDER_REGISTRY_METRICS": ec_all["ORDER_REGISTRY_METRICS"],
            "RECONCILIATION_ORDER_SOURCE_BREAKDOWN_engine_window": ec_eng["RECONCILIATION_ORDER_SOURCE_BREAKDOWN"],
        },
        "overlap_violations_same_iea": violations[:20],
        "overlap_violation_count": len(violations),
        "top_adoption_events_per_minute_engine_window": [
            {"minute_utc": k.isoformat(), "count": v} for k, v in top_adop_min
        ],
        "IEA_REPEATED_UNCHANGED_STATE": ec_all.get("IEA_REPEATED_UNCHANGED_STATE", 0),
        "ADOPTION_SAME_STATE_RETRY_WINDOW": ec_all["ADOPTION_SAME_STATE_RETRY_WINDOW"],
        "top_25_events_all_logs": ec_all.most_common(25),
    }
    print(json.dumps(out, indent=2))


if __name__ == "__main__":
    main(Path(sys.argv[1] if len(sys.argv) > 1 else "logs/robot").resolve())
