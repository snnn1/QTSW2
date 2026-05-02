#!/usr/bin/env python3
"""
Run folder integrity audit (file trace only, stdlib only).

Reads ONLY files under a run root: *.json, *.jsonl (recursive).

Usage:
  python tools/run_folder_integrity_audit.py
  python tools/run_folder_integrity_audit.py --run-root runs/ebcc66de9d714fa4a35190bd11761aa0
  python tools/run_folder_integrity_audit.py --phase2-only

Legacy-style sections (A-G) print first; Phase 2 (H-M) extends unless --legacy-only.

Writes audit_report.json under the run root by default (same data as stdout, structured).
Top-level rollup: verdict, broker_truth, system_truth, unexplained_positions (schema_version 2+).
"""
from __future__ import annotations

import argparse
import json
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterator, List, Optional, Set, Tuple

# --- Repo root (tools/..) ---
PROJECT_ROOT = Path(__file__).resolve().parent.parent
LATEST_RUN_REL = Path("runs") / "LATEST_RUN.txt"
AUDIT_REPORT_FILENAME = "audit_report.json"
# Bump when the JSON shape changes (new/moved/renamed top-level or section fields).
SCHEMA_VERSION = "3"

# Equivalent to ENTRY_SUBMITTED for classification (file evidence)
ENTRY_SUBMIT_EQUIV_EVENTS = frozenset(
    {
        "ENTRY_SUBMITTED",
        "ORDER_SUBMIT_SUCCESS",
    }
)
ENTRY_SUBMIT_LIFECYCLE = "ENTRY_SUBMITTED"

TIMELINE_PRIORITY = {
    "RANGE_LOCK_OUTCOME": 0,
    "ENTRY_SUBMITTED": 1,
    "ORDER_SUBMIT_SUCCESS": 1,
    "ENTRY_FILLED": 2,
    "MISMATCH_DETECTED": 3,
    "MISMATCH_BLOCK_ENTER": 4,
    "HARD_FLATTEN_TRIGGERED": 5,
    "FORCED_FLATTEN_TRIGGERED": 5,
    "FLATTEN_REQUESTED": 6,
}


def _parse_ts(val: Any) -> Optional[datetime]:
    if val is None:
        return None
    if isinstance(val, (int, float)):
        return datetime.fromtimestamp(float(val), tz=timezone.utc)
    s = str(val).strip()
    if not s:
        return None
    try:
        if s.endswith("Z"):
            return datetime.fromisoformat(s.replace("Z", "+00:00"))
        dt = datetime.fromisoformat(s)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc)
    except Exception:
        return None


def resolve_run_root(project_root: Path, cli: Optional[str]) -> Path:
    if cli:
        p = Path(cli)
        if not p.is_absolute():
            p = project_root / p
        return p.resolve()
    latest = project_root / LATEST_RUN_REL
    if not latest.is_file():
        raise SystemExit(f"MISSING: {latest} (pass --run-root)")
    rel = latest.read_text(encoding="utf-8").strip().replace("\r", "").replace("\\", "/")
    p = project_root / rel
    return p.resolve()


def discover_json_files(run_root: Path) -> List[Path]:
    out: List[Path] = []
    for pat in ("**/*.json", "**/*.jsonl"):
        for p in sorted(run_root.glob(pat)):
            if p.is_file():
                out.append(p)
    return out


def rel_path(run_root: Path, p: Path) -> str:
    try:
        return str(p.resolve().relative_to(run_root.resolve()))
    except ValueError:
        return str(p)


def iter_json_objects(path: Path) -> Iterator[Tuple[int, Dict[str, Any]]]:
    """Yield (line_no_1based, obj) for JSON lines; single JSON object files yield one row."""
    text = path.read_text(encoding="utf-8", errors="replace")
    stripped = text.strip()
    if not stripped:
        return
    if path.suffix.lower() == ".json" and "\n" not in stripped[:2000]:
        try:
            obj = json.loads(stripped)
        except json.JSONDecodeError:
            return
        if isinstance(obj, dict):
            yield 1, obj
        return
    for i, line in enumerate(text.splitlines(), 1):
        line = line.strip()
        if not line:
            continue
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(obj, dict):
            yield i, obj


def extract_ts_from_obj(obj: Dict[str, Any], path: Path) -> Optional[datetime]:
    for key in (
        "ts_utc",
        "timestamp_utc",
        "Utc",
        "timestamp",
        "EntrySubmittedAtUtc",
        "EntrySubmittedAt",
        "EntrySubmittedObservedAtUtc",
    ):
        if key in obj and obj[key] is not None:
            t = _parse_ts(obj[key])
            if t:
                return t
    # nested payload
    pl = obj.get("payload")
    if isinstance(pl, dict):
        for key in ("timestamp_utc", "broker_position_qty"):
            pass
        t = _parse_ts(pl.get("timestamp_utc"))
        if t:
            return t
    data = obj.get("data")
    if isinstance(data, dict):
        t = _parse_ts(data.get("ts_utc") or data.get("timestamp_utc"))
        if t:
            return t
    return None


def flatten_payload_instrument(obj: Dict[str, Any]) -> Optional[str]:
    pl = obj.get("payload")
    if isinstance(pl, dict) and pl.get("instrument"):
        return str(pl["instrument"]).strip()
    return None


@dataclass
class BrokerSnap:
    instrument: str
    broker_position_qty: float
    broker_direction: Optional[str] = None
    source_file: str = ""
    line_hint: str = ""


@dataclass
class JournalRow:
    instrument: str
    stream: str
    intent_id: str
    broker_order_id: Optional[str]
    entry_submitted: bool
    entry_filled: bool
    source_file: str
    entry_filled_at: Optional[datetime] = None
    exit_filled_at: Optional[datetime] = None
    completion_reason: str = ""
    realized_pnl_gross: Optional[float] = None
    trade_completed: bool = False


@dataclass
class TimelineEvent:
    ts: datetime
    event_type: str
    instrument: str
    source_file: str
    sort_key: Tuple[int, float]


def collect_broker_positions(
    run_root: Path, files: List[Path]
) -> Tuple[Dict[str, BrokerSnap], List[str]]:
    """instrument -> latest snap from execution_events payloads (file trace)."""
    by_inst: Dict[str, BrokerSnap] = {}
    notes: List[str] = []
    for path in files:
        rp = rel_path(run_root, path)
        if "execution_events" not in rp.replace("\\", "/"):
            continue
        for line_no, obj in iter_json_objects(path):
            pl = obj.get("payload")
            if not isinstance(pl, dict):
                continue
            qty = pl.get("broker_position_qty")
            if qty is None:
                qty = pl.get("broker_qty")
            try:
                qf = float(qty) if qty is not None else 0.0
            except (TypeError, ValueError):
                continue
            if qf <= 0:
                continue
            inst = str(pl.get("instrument") or "").strip()
            if not inst:
                inst = str(obj.get("instrument") or "").strip()
            if not inst:
                continue
            mt = str(pl.get("mismatch_type") or "")
            snap = BrokerSnap(
                instrument=inst,
                broker_position_qty=qf,
                broker_direction=str(pl.get("broker_direction") or "") or None,
                source_file=f"{rp}:{line_no}",
                line_hint=mt,
            )
            by_inst[inst] = snap
    return by_inst, notes


def load_execution_journals(run_root: Path, files: List[Path]) -> List[JournalRow]:
    rows: List[JournalRow] = []
    for path in files:
        rp = rel_path(run_root, path)
        if "state/execution_journals" not in rp.replace("\\", "/") and "execution_journals" not in rp:
            continue
        if path.suffix.lower() != ".json":
            continue
        for _, obj in iter_json_objects(path):
            inst = str(obj.get("Instrument") or "").strip()
            stream = str(obj.get("Stream") or "").strip()
            iid = str(obj.get("IntentId") or "").strip()
            if not inst or not iid:
                continue
            rows.append(
                JournalRow(
                    instrument=inst,
                    stream=stream,
                    intent_id=iid,
                    broker_order_id=str(obj.get("BrokerOrderId") or "").strip() or None,
                    entry_submitted=bool(obj.get("EntrySubmitted")),
                    entry_filled=bool(obj.get("EntryFilled")),
                    source_file=rp,
                    entry_filled_at=_parse_ts(obj.get("EntryFilledAtUtc") or obj.get("EntryFilledAt")),
                    exit_filled_at=_parse_ts(obj.get("ExitFilledAtUtc")),
                    completion_reason=str(obj.get("CompletionReason") or "").strip(),
                    realized_pnl_gross=(
                        float(obj["RealizedPnLGross"])
                        if obj.get("RealizedPnLGross") is not None
                        else None
                    ),
                    trade_completed=bool(obj.get("TradeCompleted")),
                )
            )
    return rows


def journal_timing_mismatch_flags(run_root: Path, files: List[Path]) -> List[str]:
    """File-only: EntrySubmittedAtUtc vs EntrySubmittedObservedAtUtc on different calendar days -> TIMING_MISMATCH."""
    flags: List[str] = []
    for path in files:
        rp = rel_path(run_root, path)
        if "state/execution_journals" not in rp.replace("\\", "/"):
            continue
        if path.suffix.lower() != ".json":
            continue
        for line_no, obj in iter_json_objects(path):
            if not bool(obj.get("EntryFilled")):
                continue
            a = _parse_ts(obj.get("EntrySubmittedAtUtc") or obj.get("EntrySubmittedAt"))
            b = _parse_ts(obj.get("EntrySubmittedObservedAtUtc"))
            if a and b and a.date() != b.date():
                flags.append(
                    f"TIMING_MISMATCH intent={str(obj.get('IntentId'))[:8]}... "
                    f"submitted={a.date()} observed={b.date()} {rp}:{line_no}"
                )
    return flags


def has_entry_submitted_evidence(run_root: Path, files: List[Path], instrument: str) -> List[str]:
    """Return source file refs where ENTRY_SUBMITTED or equivalent appears for instrument."""
    hits: List[str] = []
    ins_l = instrument.lower()
    for path in files:
        rp = rel_path(run_root, path)
        for line_no, obj in iter_json_objects(path):
            ev = str(obj.get("event") or obj.get("event_type") or "").strip()
            # KEY_EVENTS
            if ev == "ENTRY_SUBMITTED":
                d = obj.get("data")
                di = obj.get("instrument")
                if isinstance(d, dict):
                    di = d.get("instrument") or di
                if str(di or "").strip().lower() == ins_l:
                    hits.append(f"{rp}:{line_no} event={ev}")
                    continue
            if ev in ENTRY_SUBMIT_EQUIV_EVENTS:
                data = obj.get("data") if isinstance(obj.get("data"), dict) else {}
                ins = str(obj.get("instrument") or data.get("instrument") or "").strip()
                if ins.lower() == ins_l:
                    hits.append(f"{rp}:{line_no} event={ev}")
                    continue
            if ev == "INTENT_LIFECYCLE_TRANSITION":
                data = obj.get("data") if isinstance(obj.get("data"), dict) else {}
                if str(data.get("newState") or "").upper() == ENTRY_SUBMIT_LIFECYCLE:
                    ins = str(obj.get("instrument") or data.get("instrument") or "").strip()
                    if ins.lower() == ins_l:
                        hits.append(f"{rp}:{line_no} event={ev} newState=ENTRY_SUBMITTED")
    return hits


def classify_execution_source(
    inst: str,
    broker_qty: float,
    journal_rows: List[JournalRow],
    entry_evidence: List[str],
) -> str:
    """SYSTEM = journal row + entry submit evidence; EXTERNAL = broker qty>0, no journal; UNKNOWN = conflicting."""
    has_ej = any(r.instrument.lower() == inst.lower() for r in journal_rows)
    has_entry = len(entry_evidence) > 0
    if broker_qty > 0:
        if has_ej and has_entry:
            return "SYSTEM"
        if not has_ej:
            return "EXTERNAL"
        if has_ej and not has_entry:
            return "UNKNOWN"
        return "UNKNOWN"
    # No broker snapshot for this instrument: still classify system-originated track
    if has_ej and has_entry:
        return "SYSTEM"
    return "UNKNOWN"


def build_broker_order_map(
    run_root: Path, files: List[Path], journal_rows: List[JournalRow]
) -> Dict[str, Tuple[str, str, str]]:
    """broker_order_id -> (intent_id, instrument, stream) from journals + logs."""
    m: Dict[str, Tuple[str, str, str]] = {}
    for r in journal_rows:
        if r.broker_order_id:
            m[r.broker_order_id] = (r.intent_id, r.instrument, r.stream)
    for path in files:
        rp = rel_path(run_root, path)
        for line_no, obj in iter_json_objects(path):
            data = obj.get("data") if isinstance(obj.get("data"), dict) else {}
            boid = data.get("broker_order_id") or obj.get("broker_order_id")
            if not boid:
                pl = obj.get("payload")
                if isinstance(pl, dict):
                    boid = pl.get("broker_order_id")
            if not boid:
                continue
            boid = str(boid).strip()
            iid = (
                data.get("intent_id")
                or data.get("intentId")
                or obj.get("intent_id")
            )
            pl = obj.get("payload")
            if isinstance(pl, dict) and not iid:
                iid = pl.get("intent_id")
            ins = str(obj.get("instrument") or data.get("instrument") or "").strip()
            stream = str(data.get("stream") or data.get("stream_key") or "").strip()
            if isinstance(pl, dict):
                if not ins:
                    ins = str(pl.get("instrument") or "").strip()
            if boid and iid:
                prev = m.get(boid)
                merged_stream = stream or (prev[2] if prev else "") or "?"
                merged_ins = ins or (prev[1] if prev else "") or "?"
                m[boid] = (str(iid), merged_ins, merged_stream)
    return m


def collect_timeline(
    run_root: Path, files: List[Path], interest: Set[str]
) -> List[TimelineEvent]:
    out: List[TimelineEvent] = []
    for path in files:
        rp = rel_path(run_root, path)
        for line_no, obj in iter_json_objects(path):
            ev = str(
                obj.get("event")
                or obj.get("event_type")
                or obj.get("message")
                or ""
            ).strip()
            if not ev:
                continue
            ts = extract_ts_from_obj(obj, path)
            if ts is None:
                continue
            inst = str(obj.get("instrument") or "").strip()
            pl = obj.get("payload")
            if isinstance(pl, dict) and not inst:
                inst = str(pl.get("instrument") or "").strip()
            data = obj.get("data")
            if isinstance(data, dict) and not inst:
                inst = str(data.get("instrument") or "").strip()
            if interest and ev not in interest and not any(x in ev for x in ("FLATTEN", "RANGE_LOCK", "MISMATCH")):
                continue
            pri = TIMELINE_PRIORITY.get(ev, 50)
            out.append(
                TimelineEvent(
                    ts=ts,
                    event_type=ev,
                    instrument=inst or "?",
                    source_file=f"{rp}:{line_no}",
                    sort_key=(pri, ts.timestamp()),
                )
            )
    out.sort(key=lambda x: (x.ts, x.sort_key[0]))
    return out


def interest_set() -> Set[str]:
    return {
        "RANGE_LOCK_OUTCOME",
        "ENTRY_SUBMITTED",
        "ENTRY_FILLED",
        "ORDER_SUBMIT_SUCCESS",
        "MISMATCH_DETECTED",
        "MISMATCH_BLOCK_ENTER",
        "HARD_FLATTEN_TRIGGERED",
        "FORCED_FLATTEN_TRIGGERED",
        "FLATTEN_REQUESTED",
        "INTENT_LIFECYCLE_TRANSITION",
    }


def collect_instruments_by_layer(
    run_root: Path, files: List[Path], journal_rows: List[JournalRow]
) -> Tuple[Set[str], Set[str], Set[str]]:
    key_ev: Set[str] = set()
    ej = {r.instrument for r in journal_rows}
    ev_files: Set[str] = set()
    for path in files:
        rp = rel_path(run_root, path).replace("\\", "/")
        if not rp.endswith("KEY_EVENTS.jsonl"):
            continue
        for _, obj in iter_json_objects(path):
            ins = obj.get("instrument")
            if ins:
                key_ev.add(str(ins).strip())
    for path in files:
        rp = rel_path(run_root, path).replace("\\", "/")
        if "execution_events" not in rp:
            continue
        for _, obj in iter_json_objects(path):
            pl = obj.get("payload")
            if isinstance(pl, dict) and pl.get("instrument"):
                ev_files.add(str(pl["instrument"]).strip())
            elif obj.get("instrument"):
                ev_files.add(str(obj["instrument"]).strip())
    return key_ev, ej, ev_files


def _snap_to_dict(b: BrokerSnap) -> Dict[str, Any]:
    return {
        "instrument": b.instrument,
        "broker_position_qty": b.broker_position_qty,
        "broker_direction": b.broker_direction,
        "source_file": b.source_file,
        "line_hint": b.line_hint,
    }


def _journal_row_to_dict(r: JournalRow) -> Dict[str, Any]:
    return {
        "instrument": r.instrument,
        "stream": r.stream,
        "intent_id": r.intent_id,
        "broker_order_id": r.broker_order_id,
        "entry_submitted": r.entry_submitted,
        "entry_filled": r.entry_filled,
        "source_file": r.source_file,
    }


def build_round_trip_rows(journal_rows: List[JournalRow]) -> List[Dict[str, Any]]:
    rows: List[Dict[str, Any]] = []
    for r in journal_rows:
        if not (r.entry_filled_at and r.exit_filled_at):
            continue
        held_seconds = (r.exit_filled_at - r.entry_filled_at).total_seconds()
        rows.append(
            {
                "instrument": r.instrument,
                "stream": r.stream,
                "intent_id": r.intent_id,
                "entry_filled_at_utc": r.entry_filled_at.isoformat(),
                "exit_filled_at_utc": r.exit_filled_at.isoformat(),
                "held_seconds": round(held_seconds, 3),
                "completion_reason": r.completion_reason,
                "realized_pnl_gross": r.realized_pnl_gross,
                "source_file": r.source_file,
            }
        )
    return sorted(rows, key=lambda x: x["held_seconds"])


def _qty_json(q: float) -> Any:
    """Integer when whole-number qty for cleaner diffs."""
    if abs(q - round(q)) < 1e-9:
        return int(round(q))
    return q


def broker_truth_summary(broker_snaps: Dict[str, BrokerSnap]) -> Dict[str, Any]:
    """Latest broker snapshot qty per instrument (qty > 0 only)."""
    positions: Dict[str, Any] = {}
    for inst, snap in sorted(broker_snaps.items()):
        if snap.broker_position_qty > 0:
            positions[inst] = _qty_json(snap.broker_position_qty)
    return {"positions": positions}


def system_truth_summary(journal_rows: List[JournalRow]) -> Dict[str, Any]:
    """Per-instrument aggregate from execution journals."""
    by_inst: Dict[str, Dict[str, bool]] = {}
    for r in journal_rows:
        cur = by_inst.setdefault(
            r.instrument,
            {"submitted": False, "filled": False},
        )
        cur["submitted"] = cur["submitted"] or r.entry_submitted
        cur["filled"] = cur["filled"] or r.entry_filled
    return {"positions": {k: dict(v) for k, v in sorted(by_inst.items())}}


def build_unexplained_positions(
    broker_snaps: Dict[str, BrokerSnap], journal_rows: List[JournalRow]
) -> List[Dict[str, Any]]:
    """Broker positions the file trace cannot tie to a linked system journal."""
    out: List[Dict[str, Any]] = []
    for inst, snap in sorted(broker_snaps.items()):
        if snap.broker_position_qty <= 0:
            continue
        has_j = any(r.instrument.lower() == inst.lower() for r in journal_rows)
        if not has_j:
            out.append(
                {
                    "instrument": inst,
                    "qty": _qty_json(snap.broker_position_qty),
                    "reason": "NO_EXECUTION_JOURNAL",
                }
            )
            continue
        linked = [
            r
            for r in journal_rows
            if r.instrument.lower() == inst.lower() and r.broker_order_id
        ]
        if not linked:
            out.append(
                {
                    "instrument": inst,
                    "qty": _qty_json(snap.broker_position_qty),
                    "reason": "ORDER_ID_NOT_LINKED",
                }
            )
    return out


def compute_verdict(
    unexplained_positions: List[Dict[str, Any]],
    tag_reasons: List[str],
    mismatch_flag: str,
    timing_mismatch_count: int,
    structural_clean_pass: bool,
) -> Dict[str, Any]:
    """
    High-level rollup: OK vs not, dominant issue, confidence in classification.
    primary_issue uses stable codes for automation.
    """
    explainable = True
    primary_issue = "NONE"
    confidence: str = "MEDIUM"

    only_unknown = tag_reasons == ["UNKNOWN (insufficient flags)"]

    def set_issue(code: str) -> None:
        nonlocal primary_issue, explainable
        explainable = False
        if primary_issue == "NONE":
            primary_issue = code

    # Priority: cross-layer mismatch first, then broker orphans, then clock skew.
    if "INSTRUMENT_MISMATCH" in tag_reasons:
        set_issue("INSTRUMENT_MISMATCH")

    for row in unexplained_positions:
        reason = str(row.get("reason") or "")
        if reason == "NO_EXECUTION_JOURNAL":
            set_issue("POSITION_NOT_ADOPTED")
        elif reason == "ORDER_ID_NOT_LINKED":
            set_issue("ORDER_NOT_LINKED")

    if timing_mismatch_count > 0:
        set_issue("TIMING_MISMATCH")

    if structural_clean_pass and primary_issue == "NONE":
        confidence = "HIGH"
    elif only_unknown and primary_issue == "NONE":
        confidence = "LOW"
    else:
        confidence = "MEDIUM"

    return {
        "explainable": explainable,
        "primary_issue": primary_issue,
        "confidence": confidence,
    }


def build_audit_report(
    run_root: Path,
    files: List[Path],
    broker_snaps: Dict[str, BrokerSnap],
    journal_rows: List[JournalRow],
) -> Dict[str, Any]:
    """Structured report for stdout and audit_report.json (same facts)."""
    files_rel = sorted(rel_path(run_root, f) for f in files)
    order_map = build_broker_order_map(run_root, files, journal_rows)
    key_ins, ej_ins, ev_ins = collect_instruments_by_layer(run_root, files, journal_rows)

    all_inst = set(broker_snaps.keys()) | {r.instrument for r in journal_rows}
    section_h: List[Dict[str, Any]] = []
    for inst in sorted(all_inst):
        snap = broker_snaps.get(inst)
        bqty = snap.broker_position_qty if snap else 0.0
        ev = has_entry_submitted_evidence(run_root, files, inst)
        has_j = any(r.instrument.lower() == inst.lower() for r in journal_rows)
        src = classify_execution_source(inst, bqty, journal_rows, ev)
        section_h.append(
            {
                "instrument": inst,
                "execution_source": src,
                "has_execution_journal": has_j,
                "entry_evidence_count": len(ev),
                "entry_evidence_sample": ev[:10],
            }
        )

    order_map_rows = [
        {"broker_order_id": b, "intent_id": t[0], "instrument": t[1], "stream": t[2]}
        for b, t in sorted(order_map.items())
    ]
    journal_link_rows = []
    for r in journal_rows:
        journal_link_rows.append(
            {
                **_journal_row_to_dict(r),
                "linkage_status": "LINKED" if r.broker_order_id else "UNLINKED",
            }
        )
    position_link_rows = []
    for inst, snap in sorted(broker_snaps.items()):
        linked = [r for r in journal_rows if r.instrument.lower() == inst.lower() and r.broker_order_id]
        if snap.broker_position_qty > 0:
            position_link_rows.append(
                {
                    "instrument": inst,
                    "linkage_status": "LINKED" if linked else "UNLINKED",
                    "execution_journal_rows_with_broker_id": len(linked),
                }
            )

    all_events = collect_timeline(run_root, files, interest_set())
    mandatory = [
        e
        for e in all_events
        if e.event_type in interest_set() or e.event_type in ("MISMATCH_FAIL_CLOSED", "REGISTRY_BROKER_DIVERGENCE")
    ]
    seen = set()
    picked: List[TimelineEvent] = []
    for e in sorted(mandatory, key=lambda x: (x.ts, x.sort_key[0])):
        key = (e.event_type, e.source_file)
        if key in seen:
            continue
        seen.add(key)
        picked.append(e)
        if len(picked) >= 20:
            break
    section_j = [
        {
            "rank": i,
            "ts_utc": e.ts.isoformat(),
            "event_type": e.event_type,
            "instrument": e.instrument,
            "source_file": e.source_file,
        }
        for i, e in enumerate(picked, 1)
    ]

    mismatch_flag = "INSTRUMENT_MISMATCH"
    only_key = key_ins - ej_ins
    only_ev = ev_ins - ej_ins - key_ins
    execution_event_coverage_gap = bool(ej_ins and ev_ins and ev_ins < ej_ins)
    key_events_empty = not any(x for x in key_ins if x)
    if not (only_key or only_ev):
        mismatch_flag = "none_detected"

    unexplained_positions = build_unexplained_positions(broker_snaps, journal_rows)
    reasons: List[str] = [u["reason"] for u in unexplained_positions]
    if only_key or only_ev:
        reasons.append("INSTRUMENT_MISMATCH")
    tm_flags = journal_timing_mismatch_flags(run_root, files)
    if tm_flags:
        reasons.append("TIMING_MISMATCH")
    tag_reasons = sorted({x for x in reasons if not x.startswith("TIMING_MISMATCH intent=")})
    preventable = "YES" if "INSTRUMENT_MISMATCH" in reasons or "NO_EXECUTION_JOURNAL" in reasons else "NO"

    structural_clean_pass = (
        not unexplained_positions
        and mismatch_flag == "none_detected"
        and not tm_flags
    )
    verdict = compute_verdict(
        unexplained_positions,
        tag_reasons,
        mismatch_flag,
        len(tm_flags),
        structural_clean_pass,
    )
    broker_truth = broker_truth_summary(broker_snaps)
    system_truth = system_truth_summary(journal_rows)
    round_trips = build_round_trip_rows(journal_rows)

    return {
        "schema_version": SCHEMA_VERSION,
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "run_root": str(run_root.resolve()),
        "verdict": verdict,
        "broker_truth": broker_truth,
        "system_truth": system_truth,
        "unexplained_positions": unexplained_positions,
        "section_a": {
            "files_matched_json_jsonl": len(files),
            "files_scanned_relative": files_rel,
        },
        "section_b": {
            "broker_positions_from_execution_events": [_snap_to_dict(v) for k, v in sorted(broker_snaps.items())],
        },
        "section_c": {"execution_journal_rows": [_journal_row_to_dict(r) for r in journal_rows]},
        "section_h": {"instruments": section_h},
        "section_i": {
            "broker_order_map": order_map_rows,
            "execution_journal_links": journal_link_rows,
            "broker_position_linkage": position_link_rows,
        },
        "section_j": {"timeline_top_events": section_j},
        "section_k": {
            "instruments_execution_journals": sorted(ej_ins),
            "instruments_execution_events": sorted(ev_ins),
            "instruments_key_events": sorted(x for x in key_ins if x),
            "flag": mismatch_flag,
            "execution_event_coverage_gap": execution_event_coverage_gap,
            "missing_execution_event_instruments": sorted(ej_ins - ev_ins),
            "key_events_empty": key_events_empty,
        },
        "section_l": {
            "position_not_adopted_tags": tag_reasons,
            "timing_mismatch_details": tm_flags[:20],
        },
        "section_m": {"preventable": preventable, "preventable_note": "heuristic: YES if INSTRUMENT_MISMATCH or NO_EXECUTION_JOURNAL"},
        "section_n": {"quick_round_trips": round_trips[:20]},
    }


def write_audit_report_json(run_root: Path, report: Dict[str, Any]) -> Path:
    out = run_root / AUDIT_REPORT_FILENAME
    out.write_text(json.dumps(report, indent=2), encoding="utf-8")
    return out


def print_verdict_block(report: Dict[str, Any]) -> None:
    v = report["verdict"]
    bt = report["broker_truth"]["positions"]
    st = report["system_truth"]["positions"]
    up = report["unexplained_positions"]
    print("=" * 72)
    print("VERDICT & TRUTH SUMMARY (schema_version=%s)" % report.get("schema_version", "?"))
    print("=" * 72)
    print(
        f"  explainable={v['explainable']}  primary_issue={v['primary_issue']}  confidence={v['confidence']}"
    )
    print(f"  broker_truth.positions: {bt if bt else '(none)'}")
    print(f"  system_truth.positions: {st if st else '(none)'}")
    print(f"  unexplained_positions ({len(up)}): {up if up else '[]'}")
    print("")


def print_legacy_sections(report: Dict[str, Any]) -> None:
    """Minimal A-G compatible summary (does not remove prior manual audit semantics)."""
    a = report["section_a"]
    b = report["section_b"]["broker_positions_from_execution_events"]
    c = report["section_c"]["execution_journal_rows"]
    run_root = report["run_root"]
    print("=" * 72)
    print("SECTION A - Run scope (file trace)")
    print("=" * 72)
    print(f"run_root: {run_root}")
    print(f"files_matched_json_jsonl: {a['files_matched_json_jsonl']}")
    print("")
    print("SECTION B - Broker positions (from execution_events payloads only)")
    print("-" * 72)
    if not b:
        print("MISSING: no broker_position_qty > 0 in execution_events/*.jsonl")
    for row in b:
        print(
            f"  {row['instrument']}: qty={row['broker_position_qty']} dir={row['broker_direction']} @ {row['source_file']}"
        )
    print("")
    print("SECTION C - Execution journals (state/execution_journals)")
    print("-" * 72)
    if not c:
        print("MISSING: no execution journal rows parsed")
    for r in c:
        print(
            f"  {r['instrument']} stream={r['stream']} intent={r['intent_id'][:8]}... "
            f"submitted={r['entry_submitted']} filled={r['entry_filled']} file={r['source_file']}"
        )
    print("")
    print("(Sections D-G: see Phase 2 root cause / mismatch below or run with full manual template.)")


def print_phase2(report: Dict[str, Any]) -> None:
    h = report["section_h"]["instruments"]
    i = report["section_i"]
    j = report["section_j"]["timeline_top_events"]
    k = report["section_k"]
    l_ = report["section_l"]
    m = report["section_m"]
    n = report.get("section_n", {})

    print("")
    print("=" * 72)
    print("SECTION H - Execution source")
    print("=" * 72)
    for row in h:
        print(
            f"  {row['instrument']}: execution_source={row['execution_source']}  "
            f"(journal={row['has_execution_journal']}, entry_evidence_lines={row['entry_evidence_count']})"
        )
        for e in row["entry_evidence_sample"][:3]:
            print(f"    evidence: {e}")

    print("")
    print("=" * 72)
    print("SECTION I - Linkage (broker_order_id -> intent -> instrument -> stream)")
    print("=" * 72)
    for row in i["broker_order_map"][:40]:
        b = row["broker_order_id"]
        print(
            f"  {b[:12]}... -> intent={row['intent_id'][:8]}... inst={row['instrument']} stream={row['stream']}"
        )
    for r in i["execution_journal_links"]:
        print(
            f"  execution_journal: {r['instrument']} intent_id={r['intent_id']} stream={r['stream']} "
            f"broker_order_id={r['broker_order_id'] or 'MISSING'} linkage_status={r['linkage_status']} file={r['source_file']}"
        )
    for r in i["broker_position_linkage"]:
        print(
            f"  position_instrument={r['instrument']} linkage_status={r['linkage_status']} "
            f"(execution_journal_rows_with_broker_id={r['execution_journal_rows_with_broker_id']})"
        )

    print("")
    print("=" * 72)
    print("SECTION J - Timeline (top 20, relevance-ranked subset)")
    print("=" * 72)
    for row in j:
        print(
            f"  {row['rank']}. {row['ts_utc']}  {row['event_type']}  inst={row['instrument']}  {row['source_file']}"
        )

    print("")
    print("=" * 72)
    print("SECTION K - Instrument mismatch (journals vs KEY_EVENTS vs execution_events)")
    print("=" * 72)
    if k["flag"] == "INSTRUMENT_MISMATCH":
        print("  flag: INSTRUMENT_MISMATCH")
        print(f"    execution_journals instruments: {k['instruments_execution_journals']}")
        print(f"    execution_events instruments: {k['instruments_execution_events']}")
        print(f"    KEY_EVENTS instruments: {k['instruments_key_events']}")
    else:
        print("  flag: none detected (coarse set comparison)")
    if k.get("execution_event_coverage_gap"):
        print(
            "  coverage_gap: execution_events are sparse narrative evidence; "
            f"missing instruments={k.get('missing_execution_event_instruments', [])}"
        )
    if k.get("key_events_empty"):
        print("  coverage_gap: KEY_EVENTS is empty; durable journals remain primary verdict evidence")

    print("")
    print("=" * 72)
    print("SECTION L - Root cause (expanded, Level 2)")
    print("=" * 72)
    print("  POSITION_NOT_ADOPTED:")
    if not l_["position_not_adopted_tags"] and not l_["timing_mismatch_details"]:
        print("    - none")
    for r in l_["position_not_adopted_tags"]:
        print(f"    - {r}")
    for line in l_["timing_mismatch_details"]:
        print(f"    - {line}")

    print("")
    print("=" * 72)
    print("SECTION M - Preventability")
    print("=" * 72)
    print(
        f"  preventable: {m['preventable']}  (YES if mapping/adoption/journal path; NO if purely external broker noise - heuristic)"
    )

    print("")
    print("=" * 72)
    print("SECTION N - Quick round trips")
    print("=" * 72)
    rows = n.get("quick_round_trips", [])
    if not rows:
        print("  none detected")
    for row in rows[:20]:
        print(
            f"  {row['instrument']} {row['stream']} intent={row['intent_id'][:8]}... "
            f"held={row['held_seconds']}s completion={row['completion_reason'] or '?'} "
            f"pnl={row['realized_pnl_gross']} file={row['source_file']}"
        )


def main() -> None:
    ap = argparse.ArgumentParser(description="Run folder integrity audit (file trace).")
    ap.add_argument(
        "--run-root",
        help="Path to runs/<run_id> (default: runs/LATEST_RUN.txt target)",
    )
    ap.add_argument(
        "--legacy-only",
        action="store_true",
        help="Print only legacy sections A-C (minimal).",
    )
    ap.add_argument(
        "--phase2-only",
        action="store_true",
        help="Print only sections H-M.",
    )
    ap.add_argument(
        "--no-write-report",
        action="store_true",
        help=f"Do not write {AUDIT_REPORT_FILENAME} under the run root.",
    )
    ap.add_argument(
        "--quiet",
        action="store_true",
        help="Suppress the line that confirms where audit_report.json was written.",
    )
    args = ap.parse_args()

    run_root = resolve_run_root(PROJECT_ROOT, args.run_root)
    if not run_root.is_dir():
        raise SystemExit(f"MISSING or not a directory: {run_root}")

    files = discover_json_files(run_root)
    broker_snaps, _ = collect_broker_positions(run_root, files)
    journal_rows = load_execution_journals(run_root, files)

    report = build_audit_report(run_root, files, broker_snaps, journal_rows)

    if args.phase2_only:
        print_verdict_block(report)
        print_phase2(report)
    elif args.legacy_only:
        print_verdict_block(report)
        print_legacy_sections(report)
    else:
        print_verdict_block(report)
        print_legacy_sections(report)
        print_phase2(report)

    if not args.no_write_report:
        out_path = write_audit_report_json(run_root, report)
        if not args.quiet:
            print("")
            print(f"Wrote audit report: {out_path}")


if __name__ == "__main__":
    main()
