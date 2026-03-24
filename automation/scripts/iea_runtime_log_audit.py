#!/usr/bin/env python3
"""Parse robot jsonl logs for IEA runtime audit (stdout JSON)."""
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
        # Handle \u002B00:00 style from json
        s2 = s.replace("+00:00", "+00:00")
        if s2.endswith("Z"):
            return datetime.fromisoformat(s2.replace("Z", "+00:00"))
        return datetime.fromisoformat(s2)
    except Exception:
        return None


def main(root: Path) -> None:
    files = sorted(root.glob("robot*.jsonl")) + sorted((root / "archive").glob("robot*.jsonl"))
    files = [f for f in files if f.is_file()]

    event_counts: Counter[str] = Counter()
    by_file_events: dict[str, Counter[str]] = {}
    ts_list: list[tuple[datetime, str, str, dict]] = []  # ts, event, path, data

    adoption_starts: list[tuple[datetime, dict]] = []
    adoption_summaries: list[tuple[datetime, dict]] = []
    queue_pressure: list[tuple[datetime, dict]] = []
    enqueue_wait: list[tuple[datetime, dict]] = []
    stalls: list[tuple[datetime, dict]] = []
    iea_heartbeat: list[tuple[datetime, dict]] = []
    exec_cmd_stalled: list[tuple[datetime, dict]] = []

    # Targeted buckets
    watch = {
        "IEA_ADOPTION_SCAN_REQUEST_ACCEPTED",
        "IEA_ADOPTION_SCAN_REQUEST_SKIPPED",
        "IEA_ADOPTION_SCAN_EXECUTION_STARTED",
        "IEA_ADOPTION_SCAN_EXECUTION_COMPLETED",
        "IEA_ADOPTION_SCAN_OFF_WORKER_VIOLATION",
        "ADOPTION_DEFERRAL_HEARTBEAT_RETRY",
        "ADOPTION_SCAN_START",
        "ADOPTION_SCAN_SUMMARY",
        "IEA_QUEUE_PRESSURE_DIAG",
        "IEA_HEARTBEAT",
        "IEA_EXEC_UPDATE_ROUTED",
        "IEA_ENQUEUE_AND_WAIT_TIMEOUT",
        "IEA_ENQUEUE_AND_WAIT_TIMING",
        "IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW",
        "EXECUTION_COMMAND_STALLED",
        "REGISTRY_BROKER_DIVERGENCE",
        "REGISTRY_BROKER_DIVERGENCE_ADOPTED",
        "RECONCILIATION_ORDER_SOURCE_BREAKDOWN",
        "RECONCILIATION_ASSEMBLE_MISMATCH_DIAG",
        "RECOVERY_ALREADY_ACTIVE",
        "ORDER_UPDATE",
    }

    for fp in files:
        fc: Counter[str] = Counter()
        try:
            text = fp.read_text(encoding="utf-8", errors="replace")
        except OSError as e:
            print(json.dumps({"error_read": str(fp), "msg": str(e)}))
            continue
        rel = str(fp.relative_to(root)) if fp.is_relative_to(root) else str(fp)
        for line in text.splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                o = json.loads(line)
            except json.JSONDecodeError:
                continue
            ev = o.get("event") or ""
            fc[ev] += 1
            event_counts[ev] += 1
            ts = parse_ts(o.get("ts_utc") or "")
            data = o.get("data") if isinstance(o.get("data"), dict) else {}
            if ts and ev in watch:
                ts_list.append((ts, ev, rel, data))
            if ev == "ADOPTION_SCAN_START" and ts:
                adoption_starts.append((ts, data))
            if ev == "ADOPTION_SCAN_SUMMARY" and ts:
                adoption_summaries.append((ts, data))
            if ev == "IEA_QUEUE_PRESSURE_DIAG" and ts:
                queue_pressure.append((ts, data))
            if ev == "IEA_ENQUEUE_AND_WAIT_TIMING" and ts:
                enqueue_wait.append((ts, data))
            if ev == "IEA_ENQUEUE_AND_WAIT_TIMEOUT" and ts:
                enqueue_wait.append((ts, data))
            if ev == "EXECUTION_COMMAND_STALLED" and ts:
                exec_cmd_stalled.append((ts, data))
            if ev == "IEA_HEARTBEAT" and ts:
                iea_heartbeat.append((ts, data))
        by_file_events[rel] = fc

    # Session window: use primary ENGINE file in root (not archive) if present
    engine = root / "robot_ENGINE.jsonl"
    session_ts: list[datetime] = []
    if engine.exists():
        for line in engine.read_text(encoding="utf-8", errors="replace").splitlines():
            try:
                o = json.loads(line.strip())
                t = parse_ts(o.get("ts_utc") or "")
                if t:
                    session_ts.append(t)
            except json.JSONDecodeError:
                continue
    session_ts.sort()
    t0 = session_ts[0] if session_ts else None
    t1 = session_ts[-1] if session_ts else None
    duration_sec = (t1 - t0).total_seconds() if t0 and t1 else 0

    # Per-minute rates for ENGINE file only (representative)
    per_min: Counter[str] = Counter()
    if engine.exists():
        for line in engine.read_text(encoding="utf-8", errors="replace").splitlines():
            try:
                o = json.loads(line.strip())
                t = parse_ts(o.get("ts_utc") or "")
                if not t:
                    continue
                key = (o.get("event") or ""), t.replace(second=0, microsecond=0)
                per_min[key] += 1
            except Exception:
                continue

    # Adoption burst: consecutive START within 2s same iea
    adoption_starts.sort(key=lambda x: x[0])
    bursts = []
    cur = []
    prev_ts = None
    prev_iea = None
    for ts, d in adoption_starts:
        iea = d.get("iea_instance_id")
        if prev_ts and prev_iea == iea and (ts - prev_ts).total_seconds() <= 2.0:
            cur.append((ts, d))
        else:
            if len(cur) >= 5:
                bursts.append((prev_iea, len(cur), cur[0][0], cur[-1][0]))
            cur = [(ts, d)]
        prev_ts, prev_iea = ts, iea
    if len(cur) >= 5:
        bursts.append((prev_iea, len(cur), cur[0][0], cur[-1][0]))

    # Fingerprint consecutive identical (from START data)
    fp_runs = []
    last_fp = None
    run_len = 0
    run_start = None
    for ts, d in adoption_starts:
        key = (
            str(d.get("execution_instrument_key")),
            str(d.get("adoption_candidate_count")),
            str(d.get("broker_working_count")),
            str(d.get("qtsw2_working_same_instrument_count")),
        )
        if key == last_fp:
            run_len += 1
        else:
            if run_len >= 3:
                fp_runs.append((last_fp, run_len, run_start, ts))
            last_fp = key
            run_len = 1
            run_start = ts
    if run_len >= 3:
        fp_runs.append((last_fp, run_len, run_start, adoption_starts[-1][0]))

    # wall_ms from SUMMARY if present
    wall_ms = []
    for _, d in adoption_summaries:
        v = d.get("wall_ms")
        if v is not None:
            try:
                wall_ms.append(int(v))
            except (TypeError, ValueError):
                pass
        v2 = d.get("scan_wall_ms")
        if v2 is not None:
            try:
                w = int(v2)
                if w >= 0:
                    wall_ms.append(w)
            except (TypeError, ValueError):
                pass

    gate_events = {k: event_counts[k] for k in (
        "IEA_ADOPTION_SCAN_REQUEST_ACCEPTED",
        "IEA_ADOPTION_SCAN_REQUEST_SKIPPED",
        "IEA_ADOPTION_SCAN_EXECUTION_STARTED",
        "IEA_ADOPTION_SCAN_EXECUTION_COMPLETED",
        "IEA_ADOPTION_SCAN_OFF_WORKER_VIOLATION",
    )}

    out = {
        "log_root": str(root),
        "files_scanned": len(files),
        "robot_ENGINE.jsonl": {
            "lines_with_valid_ts": len(session_ts),
            "first_ts_utc": t0.isoformat() if t0 else None,
            "last_ts_utc": t1.isoformat() if t1 else None,
            "duration_hours": round(duration_sec / 3600, 4) if duration_sec else None,
        },
        "totals_selected_events": {k: event_counts[k] for k in sorted(watch) if event_counts[k]},
        "adoption_gate_instrumentation_events": gate_events,
        "adoption_scan_start_count": len(adoption_starts),
        "adoption_scan_summary_count": len(adoption_summaries),
        "adoption_scan_wall_ms_from_summary": {
            "count": len(wall_ms),
            "avg": round(sum(wall_ms) / len(wall_ms), 2) if wall_ms else None,
            "max": max(wall_ms) if wall_ms else None,
        },
        "adoption_burst_windows_ge_5_starts_within_2s_same_iea": [
            {"iea": a, "count": b, "from": c.isoformat(), "to": d.isoformat()} for a, b, c, d in bursts[:20]
        ],
        "identical_fingerprint_runs_ge_3_consecutive_starts": [
            {
                "fingerprint": {"iek": x[0][0], "cand": x[0][1], "brok": x[0][2], "q2": x[0][3]},
                "consecutive_count": x[1],
                "from_utc": x[2].isoformat(),
                "to_utc": x[3].isoformat(),
            }
            for x in sorted(fp_runs, key=lambda z: -z[1])[:15]
        ],
        "iea_queue_pressure_diag_count": len(queue_pressure),
        "queue_depth_samples": [
            {
                "ts": t.isoformat(),
                "depth": d.get("queue_depth_current"),
                "hwm": d.get("queue_depth_high_water_mark"),
                "work_age_ms": d.get("current_work_item_age_ms"),
            }
            for t, d in queue_pressure[:30]
        ],
        "enqueue_and_wait_timeouts": event_counts["IEA_ENQUEUE_AND_WAIT_TIMEOUT"],
        "enqueue_and_wait_overflow": event_counts["IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW"],
        "execution_command_stalled_count": len(exec_cmd_stalled),
        "reconciliation_order_source_breakdown": event_counts["RECONCILIATION_ORDER_SOURCE_BREAKDOWN"],
        "registry_broker_divergence": event_counts["REGISTRY_BROKER_DIVERGENCE"],
        "registry_broker_divergence_adopted": event_counts["REGISTRY_BROKER_DIVERGENCE_ADOPTED"],
        "recovery_already_active": event_counts["RECOVERY_ALREADY_ACTIVE"],
        "top_40_events_overall": event_counts.most_common(40),
    }
    print(json.dumps(out, indent=2))


if __name__ == "__main__":
    r = Path(sys.argv[1] if len(sys.argv) > 1 else "logs/robot")
    main(r.resolve())
