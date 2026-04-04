#!/usr/bin/env python3
"""
TIMETABLE VS STREAM FEED CONSISTENCY AUDIT (data-backed, single-process).

Usage (from repo root):
  python tools/timetable_watchdog_stream_audit.py

Uses live files and the same code paths as production poller / state_manager / aggregator.
"""
from __future__ import annotations

import json
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

# Repo root = parent of tools/
QTSW2_ROOT = Path(__file__).resolve().parent.parent
if str(QTSW2_ROOT) not in sys.path:
    sys.path.insert(0, str(QTSW2_ROOT))

from modules.watchdog.aggregator import WatchdogAggregator  # noqa: E402
from modules.watchdog.config import QTSW2_ROOT as CFG_ROOT  # noqa: E402
from modules.watchdog.timetable_poller import TimetablePoller  # noqa: E402


TIMETABLE_PATH = CFG_ROOT / "data" / "timetable" / "timetable_current.json"


@dataclass
class RawRow:
    index: int
    raw_stream: Any
    enabled: Any
    normalized_id: str


def _extract_from_file(doc: dict) -> Tuple[List[str], List[RawRow], List[str]]:
    """PART 1: enabled=true streams in JSON array order; track normalization."""
    streams_raw: List[str] = []
    rows: List[RawRow] = []
    anomalies: List[str] = []
    for i, entry in enumerate(doc.get("streams") or []):
        if not isinstance(entry, dict):
            continue
        raw_s = entry.get("stream")
        enabled = entry.get("enabled")
        if not enabled:
            continue
        if raw_s is None or (isinstance(raw_s, str) and not raw_s.strip()):
            anomalies.append(f"row {i}: enabled=True but stream empty/missing raw={raw_s!r}")
            continue
        streams_raw.append(raw_s if isinstance(raw_s, str) else str(raw_s))
        norm = str(raw_s).strip()
        rows.append(RawRow(index=i, raw_stream=raw_s, enabled=enabled, normalized_id=norm))
        if isinstance(raw_s, str) and raw_s != norm:
            anomalies.append(
                f"row {i}: whitespace trim would change {raw_s!r} -> {norm!r}"
            )
        if isinstance(raw_s, str) and raw_s != norm.upper() and norm.upper() == norm and raw_s != norm:
            pass  # casing check below
    # Normalized order with first-seen dedupe (matches poller ordered extraction intent)
    ordered_norm: List[str] = []
    seen: Set[str] = set()
    for r in rows:
        if r.normalized_id not in seen:
            ordered_norm.append(r.normalized_id)
            seen.add(r.normalized_id)
    return ordered_norm, rows, anomalies


def _order_mismatch_indices(a: List[str], b: List[str]) -> List[Dict[str, Any]]:
    """Indices where same multiset but different order; if lengths differ, only compare common prefix pattern."""
    if sorted(a) != sorted(b):
        return []
    if a == b:
        return []
    mismatches = []
    for i, (x, y) in enumerate(zip(a, b)):
        if x != y:
            mismatches.append({"index": i, "timetable": x, "watchdog": y})
    if len(a) == len(b):
        return mismatches if mismatches else [{"note": "same_set_different_order", "timetable": a, "watchdog": b}]
    return mismatches


def main() -> int:
    print("=" * 72)
    print("TIMETABLE VS STREAM FEED CONSISTENCY AUDIT")
    print("=" * 72)

    if not TIMETABLE_PATH.exists():
        print(f"MISSING: {TIMETABLE_PATH}")
        return 2

    stat = TIMETABLE_PATH.stat()
    file_mtime_utc = datetime.fromtimestamp(stat.st_mtime, tz=timezone.utc)

    doc = json.loads(TIMETABLE_PATH.read_text(encoding="utf-8"))
    timetable_enabled, raw_rows, norm_anomalies = _extract_from_file(doc)

    print("\n--- PART 1: RAW TIMETABLE (file) ---")
    print(f"TIMETABLE_ENABLED_STREAMS (normalized order, file array order, deduped): {timetable_enabled}")
    if raw_rows:
        print("Per-row (index, raw stream repr, normalized):")
        for r in raw_rows:
            print(f"  [{r.index}] raw={r.raw_stream!r} norm={r.normalized_id!r}")

    poller = TimetablePoller()
    poll_result = poller.poll()
    (
        trading_date,
        enabled_set,
        poll_hash,
        metadata,
        file_source,
        poll_ordered,
        _poll_identity_hash,
    ) = poll_result

    print("\n--- Poller (same read as production) ---")
    print(f"  trading_date: {trading_date}")
    print(f"  enabled set: {sorted(enabled_set) if enabled_set else None}")
    print(f"  enabled_streams_ordered: {poll_ordered}")
    print(f"  timetable hash (poller): {poll_hash[:16] + '...' if poll_hash and len(poll_hash) > 16 else poll_hash}")

    # PART 2 — Watchdog view (same run)
    agg = WatchdogAggregator()
    utc_now = datetime.now(timezone.utc)
    if trading_date:
        agg._state_manager.update_timetable_streams(
            enabled_set,
            trading_date,
            poll_hash,
            utc_now,
            enabled_streams_metadata=metadata,
            timetable_file_source=file_source,
            enabled_streams_unknown=False,
            enabled_streams_ordered=poll_ordered,
            timetable_identity_hash=_poll_identity_hash,
        )
    else:
        agg._state_manager.update_timetable_streams(
            None,
            None,
            None,
            utc_now,
            None,
            timetable_file_source=None,
            enabled_streams_unknown=True,
            enabled_streams_ordered=None,
        )

    snap = agg.get_stream_states()
    watchdog_streams = [s["stream"] for s in snap["streams"]]

    sm_hash = agg._state_manager.get_timetable_hash()
    last_ok = getattr(agg._state_manager, "_timetable_last_ok_utc", None)

    print("\n--- PART 2: WATCHDOG (get_stream_states, same run) ---")
    print(f"WATCHDOG_STREAMS: {watchdog_streams}")

    only_tt = [x for x in timetable_enabled if x not in watchdog_streams]
    only_wd = [x for x in watchdog_streams if x not in timetable_enabled]

    print("\n--- PART 3: EXACT DIFF ---")
    print(f"len(timetable file enabled list): {len(timetable_enabled)}")
    print(f"len(watchdog streams):          {len(watchdog_streams)}")
    print(f"ONLY_IN_TIMETABLE: {only_tt}")
    print(f"ONLY_IN_WATCHDOG:  {only_wd}")
    multiset_match = sorted(timetable_enabled) == sorted(watchdog_streams)
    if multiset_match and timetable_enabled != watchdog_streams:
        print(f"ORDER_MISMATCH (strict index compare): {[i for i,(a,b) in enumerate(zip(timetable_enabled, watchdog_streams)) if a!=b]}")
        print(f"  timetable order: {timetable_enabled}")
        print(f"  watchdog order:  {watchdog_streams}")
    elif not multiset_match:
        print("ORDER_MISMATCH: n/a (sets differ)")
    else:
        print("ORDER_MISMATCH: none (identical lists)")

    print("\n--- PART 5: NORMALIZATION ---")
    if norm_anomalies:
        for line in norm_anomalies:
            print(f"  {line}")
    else:
        print("  No whitespace/normalization anomalies flagged on enabled rows.")
    print("  Poller applies: stream_id = str(raw_id).strip() if raw_id is not None else '' (see timetable_poller.py)")

    print("\n--- PART 6: TIMING / STALENESS ---")
    print(f"  timetable file mtime (UTC): {file_mtime_utc.isoformat()}")
    print(f"  state_manager._timetable_last_ok_utc: {last_ok}")
    print(f"  audit run time (UTC): {utc_now.isoformat()}")
    stale_note = (
        last_ok and file_mtime_utc > last_ok.replace(tzinfo=timezone.utc)
        if last_ok
        else False
    )
    if stale_note:
        print("  NOTE: file_mtime > last_ok — file changed after last successful poll in this session (live server may differ).")
    else:
        print("  NOTE: staleness vs live poller requires running watchdog service (this script only simulates one poll).")

    print("\n--- PART 7: HASH ---")
    if poll_hash and sm_hash and poll_hash == sm_hash:
        print(f"  poller hash == state_manager hash: OK ({poll_hash[:12]}...)")
    elif poll_hash and sm_hash:
        print(f"  WATCHDOG_TIMETABLE_HASH_MISMATCH poller={poll_hash[:24]}... sm={sm_hash[:24]}...")
    else:
        print(f"  hash compare skipped: poller={poll_hash} sm={sm_hash}")

    print("\n--- PART 4: TRACE ONE MISMATCH ---")
    pick: Optional[str] = None
    if only_tt:
        pick = only_tt[0]
    elif only_wd:
        pick = only_wd[0]

    if not pick:
        print("  No stream in ONLY_IN_TIMETABLE or ONLY_IN_WATCHDOG for this run.")
        print("  Root cause for *observed* parity: file enabled list matches poller and get_stream_states.")
    else:
        print(f"  Tracing stream: {pick!r}")
        in_file = pick in timetable_enabled
        in_poller_set = bool(enabled_set and pick in enabled_set)
        in_poller_order = bool(poll_ordered and pick in poll_ordered)
        after_sm_order = pick in agg._state_manager.get_enabled_streams_ordered()
        in_get_states = pick in watchdog_streams
        print(f"    in TIMETABLE_ENABLED_STREAMS (file-derived): {in_file}")
        print(f"    in poller enabled set: {in_poller_set}")
        print(f"    in poller ordered list: {in_poller_order}")
        print(f"    in state_manager.get_enabled_streams_ordered(): {after_sm_order}")
        print(f"    in get_stream_states() stream ids: {in_get_states}")
        if only_tt:
            print("    Likely drop: poller failed or session resolve failed → no enabled set in SM; or stream filtered in aggregator loop.")
        if only_wd:
            print("    Likely extra: hydrate/carry-over removed in current code — if seen, check for alternate code version or non-timetable rows.")

    print("\n--- UI NOTE (not API) ---")
    print("  StreamStatusTable.tsx re-sorts rows by trading_date then slot_time (desc) then stream id.")
    print('  API "WATCHDOG_STREAMS" order may differ from on-screen order.')

    print("\n" + "=" * 72)
    print("SUMMARY (OUTPUT FORMAT)")
    print("=" * 72)
    print(f"TIMETABLE_ENABLED_STREAMS: {timetable_enabled}")
    print(f"WATCHDOG_STREAMS:          {watchdog_streams}")
    print(f"DIFF ONLY_IN_TIMETABLE: {only_tt}")
    print(f"DIFF ONLY_IN_WATCHDOG:  {only_wd}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
