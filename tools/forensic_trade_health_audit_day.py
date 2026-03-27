#!/usr/bin/env python3
"""One-off style forensic: trade outcome vs system health for a calendar day (UTC slice)."""
from __future__ import annotations

import json
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

DAY = "2026-03-26"
DAY_START = f"{DAY}T00:00:00"
DAY_END = "2026-03-27T00:00:00"

INTERFERENCE_EVENTS = frozenset(
    {
        "RECONCILIATION_MISMATCH_DETECTED",
        "STATE_CONSISTENCY_GATE_ENGAGED",
        "STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH",
        "RECONCILIATION_MISMATCH_FAIL_CLOSED",
    }
)

REGISTRY_ANOMALY = frozenset(
    {
        "ORDER_REGISTRY_MISSING_FAIL_CLOSED",
        "EXECUTION_UPDATE_UNKNOWN_ORDER",
        "ORDER_LIFECYCLE_TRANSITION_INVALID",
        "MANUAL_OR_EXTERNAL_ORDER_DETECTED",
    }
)

LIFECYCLE_ERROR_EVENTS = frozenset(
    {
        "ORDER_LIFECYCLE_TRANSITION_INVALID",
        "INTENT_LIFECYCLE_TRANSITION_INVALID",
    }
)

FILL_ERROR_HINTS = frozenset(
    {
        "INTENT_FILL_OVERFILL",
        "UNKNOWN_ORDER_FILL_FLATTEN_FAILED",
        "DUPLICATE_ORDER_SUBMISSION_DETECTED",
    }
)

PROTECTIVE_ERROR_EVENTS = frozenset(
    {
        "PROTECTIVE_MISSING_STOP",
        "PROTECTIVE_MISSING_TARGET",
        "PROTECTIVE_STOP_QTY_MISMATCH",
        "INTENT_PROTECTIVE_FAILURE",
        "INTENT_PROTECTIVE_FAILURE_FALLBACK",
        "PROTECTIVE_ORDERS_FAILED_FLATTENED",
        "PROTECTIVE_CONFLICTING_ORDERS",
        "PROTECTIVE_EMERGENCY_FLATTEN_TRIGGERED",
    }
)


def parse_ts(s: str) -> Optional[datetime]:
    if not s:
        return None
    try:
        if s.endswith("Z"):
            s = s[:-1] + "+00:00"
        return datetime.fromisoformat(s.replace("Z", "+00:00"))
    except Exception:
        return None


def in_audit_day(ts: datetime) -> bool:
    ds = ts.strftime("%Y-%m-%d")
    return ds == DAY


def intent_from_obj(d: Dict[str, Any]) -> Optional[str]:
    if not d:
        return None
    v = d.get("intent_id") or d.get("intentId")
    if v:
        return str(v)
    return None


def collect_jsonl_paths(log_robot: Path) -> List[Path]:
    paths: List[Path] = []
    for pat in ("robot_*.jsonl", "robot_ENGINE*.jsonl"):
        for p in sorted(log_robot.glob(pat)):
            if p.is_file() and p not in paths:
                paths.append(p)
    arch = log_robot / "archive"
    if arch.is_dir():
        for p in sorted(arch.glob("robot_*.jsonl")):
            if p not in paths:
                paths.append(p)
    return paths


@dataclass
class TradeRow:
    intent_id: str
    instrument: str
    stream: str
    journal_file: str
    entry_ts: Optional[datetime]
    entry_price: Optional[float]
    exit_ts: Optional[datetime]
    exit_price: Optional[float]
    exit_reason: str
    trade_completed: bool
    entry_qty: int
    exit_qty: int
    expected_qty: int
    # evidence counts
    lifecycle_invalid: int = 0
    fill_evidence_lines: List[str] = field(default_factory=list)
    protective_delay_ms: Optional[float] = None
    registry_missing_failclosed_during: int = 0
    interference_counts: Dict[str, int] = field(default_factory=lambda: defaultdict(int))
    execution_filled_entry_qty: int = 0
    execution_filled_exit_qty: int = 0
    orphan_fill_hints: int = 0
    broker_before_registry: int = 0


def load_execution_journals(ej_dir: Path) -> List[TradeRow]:
    rows: List[TradeRow] = []
    for p in sorted(ej_dir.glob(f"{DAY}_*.json")):
        if p.name.startswith("__"):
            continue
        try:
            j = json.loads(p.read_text(encoding="utf-8"))
        except Exception:
            continue
        iid = j.get("IntentId")
        if not iid:
            continue
        part = p.stem.split("_")
        stream = part[2] if len(part) >= 3 else ""
        inst = j.get("Instrument") or ""
        ef = parse_ts(j.get("EntryFilledAtUtc") or j.get("EntryFilledAt") or "")
        xf = parse_ts(j.get("ExitFilledAtUtc") or j.get("CompletedAtUtc") or "")
        entry_px = j.get("EntryAvgFillPrice") or j.get("FillPrice") or j.get("EntryPrice")
        exit_px = j.get("ExitAvgFillPrice")
        tc = bool(j.get("TradeCompleted"))
        exit_reason = str(j.get("CompletionReason") or j.get("ExitOrderType") or "unknown")
        eq = int(j.get("FillQuantity") or j.get("ExpectedQuantity") or 0)
        exq = int(j.get("ExitFilledQuantityTotal") or 0)
        rows.append(
            TradeRow(
                intent_id=str(iid),
                instrument=inst,
                stream=stream,
                journal_file=p.name,
                entry_ts=ef,
                entry_price=float(entry_px) if entry_px is not None else None,
                exit_ts=xf,
                exit_price=float(exit_px) if exit_px is not None else None,
                exit_reason=exit_reason,
                trade_completed=tc,
                entry_qty=int(j.get("EntryFilledQuantityTotal") or j.get("FillQuantity") or 0),
                exit_qty=exq,
                expected_qty=eq,
            )
        )
    return rows


def main() -> int:
    root = Path(__file__).resolve().parent.parent
    log_robot = root / "logs" / "robot"
    ej_dir = root / "data" / "execution_journals"

    trades = load_execution_journals(ej_dir)
    intent_set = {t.intent_id for t in trades}

    # Ingest JSONL for the audit day
    events_by_intent: Dict[str, List[Tuple[datetime, str, str, Dict[str, Any]]]] = defaultdict(list)
    global_day_events: List[Tuple[datetime, str, str, Dict[str, Any]]] = []

    for path in collect_jsonl_paths(log_robot):
        try:
            with path.open("r", encoding="utf-8", errors="replace") as f:
                for line_num, line in enumerate(f, 1):
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        ev = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    ts_s = ev.get("ts_utc") or ev.get("ts") or ""
                    ts = parse_ts(str(ts_s))
                    if ts is None or not in_audit_day(ts):
                        continue
                    evt = ev.get("event") or ""
                    inst = ev.get("instrument") or ""
                    d = ev.get("data") if isinstance(ev.get("data"), dict) else {}
                    iid = intent_from_obj(d)
                    tup = (ts, evt, inst, d)
                    global_day_events.append(tup)
                    if iid and iid in intent_set:
                        events_by_intent[iid].append(tup)
        except OSError:
            continue

    # Reconciliation metrics (all day, ENGINE stream in instrument '' or from any file)
    recon_started = sum(1 for t, e, _, _ in global_day_events if e == "STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED")
    progress_markers = sum(
        1
        for _, e, _, d in global_day_events
        if e
        in (
            "STATE_CONSISTENCY_GATE_RELEASED",
            "RECONCILIATION_PASS_SUMMARY",
            "STATE_CONSISTENCY_GATE_CONVERGED",
        )
    )
    # IEA measurable progress (if present)
    measurable = sum(1 for _, e, _, _ in global_day_events if "MEASURABLE_PROGRESS" in e or e == "IEA_MEASURABLE_RECONCILIATION_PROGRESS")

    # Loop signature: consecutive identical RECONCILIATION_RESULT payloads (instrument + gate_state) — sample longest run
    longest_streak = 0
    cur_key = None
    cur_len = 0
    for _, e, inst, d in sorted(global_day_events, key=lambda x: x[0]):
        if e != "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT":
            cur_key = None
            cur_len = 0
            continue
        key = (inst, str(d.get("gate_state")), str(d.get("mismatch_type")))
        if key == cur_key:
            cur_len += 1
        else:
            cur_key = key
            cur_len = 1
        longest_streak = max(longest_streak, cur_len)

    # Per-trade analysis
    anomaly_lifecycle = 0
    anomaly_fill = 0
    anomaly_protective = 0
    anomaly_registry = 0
    clean = interfered = impacted = 0
    position_divergence_windows: List[str] = []

    for t in trades:
        evs = sorted(events_by_intent.get(t.intent_id, []), key=lambda x: x[0])
        t.lifecycle_invalid = sum(1 for _, e, _, _ in evs if e in LIFECYCLE_ERROR_EVENTS)

        # Fills from EXECUTION_FILLED
        for ts, e, _, d in evs:
            if e != "EXECUTION_FILLED":
                continue
            ot = str(d.get("order_type") or "").upper()
            fq = d.get("fill_quantity") or d.get("filled_total") or 0
            try:
                q = int(fq)
            except (TypeError, ValueError):
                q = 0
            if ot == "ENTRY":
                t.execution_filled_entry_qty += q
            else:
                t.execution_filled_exit_qty += q

        if t.trade_completed and t.entry_qty > 0:
            if t.execution_filled_entry_qty != t.entry_qty:
                t.fill_evidence_lines.append(
                    f"journal_entry_qty={t.entry_qty} log_EXECUTION_FILLED_entry_sum={t.execution_filled_entry_qty}"
                )
            if t.trade_completed and t.entry_qty > 0 and t.exit_qty != t.entry_qty:
                t.fill_evidence_lines.append(
                    f"journal_exit_qty={t.exit_qty} entry_qty={t.entry_qty} reason={t.exit_reason}"
                )

        overfill_flags = sum(1 for _, e, _, d in evs if e in FILL_ERROR_HINTS or str(d.get("overfill", "")).lower() == "true")
        if overfill_flags:
            t.fill_evidence_lines.append(f"overfill_or_fill_error_events={overfill_flags}")

        # Protective delay: first STOP Working after entry fill
        entry_ts = t.entry_ts
        first_stop_working: Optional[datetime] = None
        if entry_ts:
            for ts, e, _, d in evs:
                if e != "ORDER_UPDATED":
                    continue
                if str(d.get("order_type")) != "STOP":
                    continue
                if str(d.get("order_state")) != "Working":
                    continue
                first_stop_working = ts
                break
            if first_stop_working:
                t.protective_delay_ms = (first_stop_working - entry_ts).total_seconds() * 1000
                if t.protective_delay_ms > 500:
                    anomaly_protective += 1

        # Registry / broker ordering
        for ts, e, _, d in evs:
            if e == "ORDER_REGISTRY_MISSING_FAIL_CLOSED":
                t.registry_missing_failclosed_during += 1
                anomaly_registry += 1
            if e == "ORDER_REGISTRY_EXEC_RESOLVED":
                rp = str(d.get("resolution_path") or "")
                if rp and rp != "DirectId":
                    t.broker_before_registry += 1

        # Interference: same instrument, overlap with trade window
        if t.entry_ts and t.exit_ts:
            w0 = t.entry_ts
            w1 = t.exit_ts
        elif t.entry_ts:
            w0 = t.entry_ts
            w1 = t.entry_ts
        else:
            w0 = w1 = None

        inf_counts: Dict[str, int] = defaultdict(int)
        if w0 and w1:
            for ts, e, inst, d in global_day_events:
                if e not in INTERFERENCE_EVENTS:
                    continue
                if inst != t.instrument:
                    continue
                if not (w0 <= ts <= w1):
                    continue
                inf_counts[e] += 1
        t.interference_counts = dict(inf_counts)

    # Outcome + interference classification
    clean = interfered = impacted = 0
    table_rows: List[Tuple[str, str, str, str]] = []
    for t in trades:
        has_inf = sum(t.interference_counts.values()) > 0
        degraded = (
            t.lifecycle_invalid > 0
            or t.registry_missing_failclosed_during > 0
            or (t.protective_delay_ms is not None and t.protective_delay_ms > 500)
            or t.broker_before_registry > 0
            or bool(t.fill_evidence_lines)
        )
        broken = not t.trade_completed or bool(t.fill_evidence_lines)

        if broken:
            cls = "BROKEN"
        elif degraded:
            cls = "DEGRADED"
        else:
            cls = "CORRECT"

        if t.lifecycle_invalid:
            anomaly_lifecycle += t.lifecycle_invalid
        if t.fill_evidence_lines:
            anomaly_fill += len(t.fill_evidence_lines)
        if t.registry_missing_failclosed_during:
            anomaly_registry += t.registry_missing_failclosed_during

        if not has_inf:
            icls = "CLEAN"
            clean += 1
        elif cls == "BROKEN" or (cls == "DEGRADED" and t.registry_missing_failclosed_during > 0):
            icls = "IMPACTED"
            impacted += 1
        else:
            icls = "INTERFERED"
            interfered += 1

        table_rows.append((t.intent_id, t.instrument, cls, icls))

    # Position divergence: RECONCILIATION mismatch with broker_qty != local_qty for instrument on day
    div_samples = []
    for ts, e, inst, d in global_day_events:
        if e != "RECONCILIATION_MISMATCH_DETECTED":
            continue
        try:
            bq = d.get("broker_qty")
            lq = d.get("local_qty")
            if bq is None or lq is None:
                continue
            if int(bq) != int(lq):
                div_samples.append(f"{ts.isoformat()} {inst} broker_qty={bq} local_qty={lq} type={d.get('mismatch_type')}")
        except Exception:
            continue
    position_divergence_windows = div_samples[:15]

    # Print report
    print(f"FORENSIC TRADE OUTCOME VS SYSTEM HEALTH — {DAY} (UTC date from ts_utc)")
    print()
    print("SECTION 1 — TRADE OUTCOME SUMMARY")
    print("intent_id | instrument | outcome_class | interference_class")
    for iid, inst, ocls, icls in table_rows:
        print(f"{iid} | {inst} | {ocls} | {icls}")

    print()
    print("SECTION 2 — ANOMALY COUNTS (evidence from logs tagged to intents where applicable)")
    print(f"lifecycle_invalid_events (sum over intents): {anomaly_lifecycle}")
    print(f"fill_anomaly_flags (journal vs EXECUTION_FILLED or qty mismatch): {anomaly_fill}")
    print(f"protective_delay_gt_500ms (trades): {anomaly_protective}")
    print(f"ORDER_REGISTRY_MISSING_FAIL_CLOSED (log lines, may duplicate instruments): {anomaly_registry}")
    print(f"reconciliation_loop_proxy longest_identical_result_streak: {longest_streak}")
    print()

    print("SECTION 3 — INTERFERENCE ANALYSIS")
    print(f"total_trades: {len(trades)}")
    print(f"clean: {clean}")
    print(f"interfered: {interfered}")
    print(f"impacted: {impacted}")
    print()

    print("SECTION 4 — RECONCILIATION QUALITY (day-wide ENGINE+instrument logs)")
    print(f"STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED count: {recon_started}")
    print(f"progress_marker_events (release/pass/converged + measurable): {progress_markers + measurable}")
    eff = (progress_markers + measurable) / recon_started if recon_started else None
    print(f"progress_efficiency_proxy: {eff} (progress_events / recon_started) — UNKNOWN if semantics differ")
    print(f"longest_no_progress_streak proxy (identical RECONCILIATION_RESULT run): {longest_streak}")
    print()

    print("SECTION 5 — POSITION TRUTH (sample broker_qty vs local_qty != )")
    for s in position_divergence_windows[:10]:
        print(f"  {s}")
    if len(div_samples) > 10:
        print(f"  … total divergence observations: {len(div_samples)}")
    print()

    print("SECTION 6 — FINAL VERDICT")
    any_broken = any(r[2] == "BROKEN" for r in table_rows)
    any_degraded = any(r[2] == "DEGRADED" for r in table_rows)
    if any_broken:
        verdict = "SYSTEM UNSAFE — at least one trade BROKEN by journal/log criteria; correctness not fully trusted"
    elif any_degraded or recon_started > 100:
        verdict = "SYSTEM DEGRADED — trades mostly completed but lifecycle/adoption/interference evidence present"
    else:
        verdict = "SYSTEM HEALTHY — no BROKEN trades and low interference by this pass"
    print(verdict)
    print()
    print("LIMITATIONS: Journal timestamps may be inconsistent (e.g. EntrySubmitted after EntryFilled);")
    print("interference uses instrument+trade window only (not intent-level gate payloads).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
