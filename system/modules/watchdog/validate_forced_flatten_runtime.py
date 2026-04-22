#!/usr/bin/env python3
"""
Runtime validation: one stream's forced-flatten chain across ENGINE, per-instrument log, frontend_feed, tracker.

Usage (after a live day with session-close flatten):
  python -m modules.watchdog.validate_forced_flatten_runtime --date 2026-04-08 --stream MES1

Defaults: the active run context's robot_ENGINE.jsonl, frontend_feed.jsonl, and the first robot_<INST>.jsonl
matching --instrument if set, else heuristics from ENGINE lines.

Does not modify state; read-only.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional

_REPO = Path(__file__).resolve().parents[2]

ENGINE_EVENTS = (
    "FORCED_FLATTEN_TRIGGERED",
    "FORCED_FLATTEN_REQUEST_SUBMITTED",
    "FLATTEN_BROKER_FLAT_CONFIRMED",
    "SESSION_FORCED_FLATTENED",
)
STREAM_EVENTS = ("FORCED_FLATTEN_POSITION_CLOSED", "FORCED_FLATTEN_MARKET_CLOSE")
SLOT_END = "SLOT_END_SUMMARY"


def _iter_jsonl(path: Path) -> Iterable[Dict[str, Any]]:
    if not path.is_file():
        return
    with open(path, "r", encoding="utf-8-sig") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                o = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(o, dict):
                yield o


def _event_type(obj: Dict[str, Any]) -> str:
    return str(
        obj.get("event_type")
        or obj.get("event")
        or obj.get("@event")
        or ""
    ).strip()


def _trading_date(obj: Dict[str, Any]) -> str:
    d = obj.get("trading_date")
    if d:
        return str(d).strip()
    data = obj.get("data")
    if isinstance(data, dict) and data.get("trading_date"):
        return str(data["trading_date"]).strip()
    return ""


def _stream_hint(obj: Dict[str, Any]) -> str:
    s = obj.get("stream") or obj.get("stream_id")
    if s:
        return str(s).strip()
    data = obj.get("data")
    if isinstance(data, dict):
        s = data.get("stream")
        if s:
            return str(s).strip()
    return ""


def _matches_stream(obj: Dict[str, Any], stream: Optional[str]) -> bool:
    if not stream:
        return True
    st = stream.strip().upper()
    if _stream_hint(obj).upper() == st:
        return True
    blob = json.dumps(obj, default=str)
    if st in blob.upper():
        return True
    return False


def _matches_date(obj: Dict[str, Any], date: str) -> bool:
    if not date:
        return True
    td = _trading_date(obj)
    if td == date:
        return True
    blob = json.dumps(obj, default=str)
    return date in blob


def _collect(
    path: Path,
    want: tuple[str, ...],
    *,
    date: str,
    stream: Optional[str],
    apply_stream_filter: bool,
) -> Dict[str, List[str]]:
    out: Dict[str, List[str]] = {k: [] for k in want}
    for obj in _iter_jsonl(path):
        et = _event_type(obj)
        if et not in want:
            continue
        if not _matches_date(obj, date):
            continue
        if apply_stream_filter and stream and not _matches_stream(obj, stream):
            continue
        line = json.dumps(obj, default=str)
        out[et].append(line)
    return out


def _find_instrument_log(instrument: str) -> Optional[Path]:
    from modules.watchdog.run_context import resolve_active_run_context

    logs = resolve_active_run_context().robot_logs_dir
    if not logs.is_dir():
        return None
    # e.g. MES -> robot_MES.jsonl
    p = logs / f"robot_{instrument}.jsonl"
    if p.is_file():
        return p
    return None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--date", required=True, help="Trading date YYYY-MM-DD")
    ap.add_argument("--stream", default="", help="Timetable stream id e.g. MES1 (optional filter)")
    ap.add_argument("--instrument", default="", help="Per-instrument log stem e.g. MES (optional)")
    ap.add_argument("--engine", type=Path, default=None)
    ap.add_argument("--feed", type=Path, default=None)
    ap.add_argument("--instrument-log", type=Path, default=None)
    ap.add_argument("--skip-tracker", action="store_true")
    args = ap.parse_args()

    date = args.date.strip()
    stream = args.stream.strip() or None

    from modules.watchdog.run_context import resolve_active_run_context

    context = resolve_active_run_context()
    engine_path = args.engine or (context.robot_logs_dir / "robot_ENGINE.jsonl")
    feed_path = args.feed or context.frontend_feed_file

    inst = args.instrument.strip()
    inst_log = args.instrument_log
    if inst_log is None and inst:
        inst_log = _find_instrument_log(inst)
    elif inst_log is None and stream:
        m = re.match(r"^([A-Z]+)", stream.upper())
        if m:
            inst_log = _find_instrument_log(m.group(1))

    print("=== Paths ===")
    print(f"ENGINE:   {engine_path} ({'ok' if engine_path.is_file() else 'MISSING'})")
    print(f"FEED:     {feed_path} ({'ok' if feed_path.is_file() else 'MISSING'})")
    print(f"INST log: {inst_log} ({'ok' if inst_log and inst_log.is_file() else 'MISSING'})")
    print(f"Filter: trading_date={date!r} stream={stream!r}")
    print()

    def dump_section(
        title: str,
        path: Path,
        want: tuple[str, ...],
        *,
        apply_stream_filter: bool,
    ) -> None:
        print(f"=== {title} ===")
        if not path.is_file():
            print("(file missing)\n")
            return
        got = _collect(
            path, want, date=date, stream=stream, apply_stream_filter=apply_stream_filter
        )
        for et in want:
            lines = got.get(et) or []
            print(f"-- {et} ({len(lines)} line(s)) --")
            for ln in lines[:50]:
                print(ln)
            if len(lines) > 50:
                print(f"... ({len(lines) - 50} more)")
        print()

    # ENGINE: date-only filter (stream often null on FORCED_FLATTEN_TRIGGERED)
    dump_section(
        "robot_ENGINE.jsonl (engine events)",
        engine_path,
        ENGINE_EVENTS,
        apply_stream_filter=False,
    )
    if inst_log and inst_log.is_file():
        dump_section(
            "Per-instrument log (stream events)",
            inst_log,
            STREAM_EVENTS,
            apply_stream_filter=bool(stream),
        )
    else:
        print("=== Per-instrument log (stream events) ===")
        print("(skipped — pass --instrument MES or --instrument-log path)\n")

    # Feed: same engine types + stream types + SLOT_END for ordering question
    feed_want = tuple(dict.fromkeys(ENGINE_EVENTS + STREAM_EVENTS + (SLOT_END,)))
    dump_section(
        "frontend_feed.jsonl (engine + stream + SLOT_END_SUMMARY)",
        feed_path,
        feed_want,
        apply_stream_filter=bool(stream),
    )

    # SLOT_END before terminal: compare timestamps if both exist
    if feed_path.is_file():
        slot_ts: List[str] = []
        term_ts: List[str] = []
        for obj in _iter_jsonl(feed_path):
            if not _matches_date(obj, date):
                continue
            if stream and not _matches_stream(obj, stream):
                continue
            et = _event_type(obj)
            ts = str(obj.get("timestamp_utc") or obj.get("ts_utc") or "")
            if et == SLOT_END:
                slot_ts.append(ts)
            if et == "SESSION_FORCED_FLATTENED":
                term_ts.append(ts)
        print("=== Ordering check (frontend_feed, same date/stream filter) ===")
        print(f"SLOT_END_SUMMARY timestamps: {slot_ts[:20]}{'...' if len(slot_ts) > 20 else ''}")
        print(f"SESSION_FORCED_FLATTENED timestamps: {term_ts}")
        if slot_ts and term_ts:
            earliest_slot = min(slot_ts)
            earliest_term = min(term_ts)
            print(
                f"Earliest SLOT_END_SUMMARY < earliest SESSION_FORCED_FLATTENED: "
                f"{earliest_slot < earliest_term} (lex ISO compare ok for same offset fmt)"
            )
        print()

    if not args.skip_tracker:
        try:
            from modules.watchdog.aggregator.session_flatten_state import SessionFlattenStateTracker
            from modules.watchdog.replay_session_flatten import normalize_robot_engine_line
        except ImportError:
            sys.path.insert(0, str(_REPO))
            from modules.watchdog.aggregator.session_flatten_state import SessionFlattenStateTracker
            from modules.watchdog.replay_session_flatten import normalize_robot_engine_line

        print("=== Tracker replay (frontend_feed.jsonl, ingest only) ===")
        if not feed_path.is_file():
            print("(no feed file)\n")
            return 0
        tracker = SessionFlattenStateTracker()
        for obj in _iter_jsonl(feed_path):
            if not _matches_date(obj, date):
                continue
            ev = normalize_robot_engine_line(obj)
            tracker.ingest(ev)
        rows = tracker.list_rows_sorted()
        print(f"Rollup rows for date filter context: {len(rows)} total row(s) in tracker after full feed replay.")
        for r in rows:
            if r.get("trading_date") != date:
                continue
            if stream and stream.strip():
                rs = (r.get("stream") or "").strip()
                if rs and rs != stream.strip():
                    continue
            print(json.dumps(r, default=str))
        print()

    print("=== Checklist (fill by inspection) ===")
    print("[ ] SESSION_FORCED_FLATTENED present in robot_ENGINE.jsonl")
    print("[ ] SESSION_FORCED_FLATTENED present in frontend_feed.jsonl")
    print("[ ] stream field populated on SESSION_FORCED_FLATTENED (and/or key stream in blob)")
    print("[ ] Tracker flatten_status CONFIRMED for expected stream row")
    print("[ ] SLOT_END_SUMMARY ordering vs SESSION_FORCED_FLATTENED (see Ordering check above)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
