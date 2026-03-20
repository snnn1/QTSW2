#!/usr/bin/env python3
"""
Diagnose DATA_STALL patterns from frontend_feed.jsonl.

For each DATA_STALL_RECOVERED:
- Extract instrument, last_bar_utc, time since previous bar
- Find corresponding DATA_LOSS_DETECTED
- Classify: low liquidity, feed delay, disconnect/reconnect, bar aggregation delay
- Check CONNECTION_LOST nearby, multiple instruments stalled simultaneously

Output: per-instrument stall frequency, average stall duration, % active vs quiet session.
"""
from __future__ import annotations

import json
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

# Project root
PROJECT_ROOT = Path(__file__).resolve().parents[1]
FEED_PATH = PROJECT_ROOT / "logs" / "robot" / "frontend_feed.jsonl"

# CME RTH (rough): 8:30-15:15 CT = active trading
# Quiet: overnight, lunch (11:30-13:00 CT), pre-market
RTH_START_CT = (8, 30)   # 8:30
RTH_END_CT = (15, 15)    # 15:15
CT_OFFSET_HOURS = -6     # UTC-6 for CT (standard)


@dataclass
class StallEvent:
    ts_utc: datetime
    instrument: str
    last_bar_utc: datetime | None
    elapsed_seconds: float | None
    event_type: str


@dataclass
class StallPair:
    """DATA_LOSS_DETECTED + corresponding DATA_STALL_RECOVERED."""
    loss: StallEvent
    recovered: StallEvent
    stall_duration_sec: float
    time_since_prev_bar_sec: float | None = None
    classification: str = "unknown"
    connection_lost_nearby: bool = False
    multi_instrument_stall: bool = False


def parse_ts(s: str | None) -> datetime | None:
    if not s:
        return None
    try:
        return datetime.fromisoformat(s.replace("Z", "+00:00"))
    except Exception:
        return None


def is_active_session_ct(dt: datetime) -> bool:
    """True if within RTH (8:30-15:15 CT)."""
    utc = dt
    ct_hour = dt.hour + CT_OFFSET_HOURS
    if ct_hour < 0:
        ct_hour += 24
    ct_min = dt.minute
    start_min = RTH_START_CT[0] * 60 + RTH_START_CT[1]
    end_min = RTH_END_CT[0] * 60 + RTH_END_CT[1]
    now_min = ct_hour * 60 + ct_min
    return start_min <= now_min <= end_min


def load_events(path: Path) -> list[dict]:
    events = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return events


def extract_instrument(ev: dict) -> str | None:
    data = ev.get("data") or {}
    if isinstance(data, dict):
        return data.get("instrument") or data.get("execution_instrument_full_name")
    return ev.get("instrument") or ev.get("execution_instrument_full_name")


def parse_event(ev: dict) -> StallEvent | None:
    et = ev.get("event_type")
    if et not in ("DATA_STALL_RECOVERED", "DATA_LOSS_DETECTED"):
        return None
    ts = parse_ts(ev.get("timestamp_utc"))
    if not ts:
        return None
    inst = extract_instrument(ev)
    if not inst:
        return None
    data = ev.get("data") or {}
    if isinstance(data, str):
        data = {}
    last_bar = parse_ts(data.get("last_bar_utc"))
    elapsed = data.get("elapsed_seconds")
    if elapsed is not None:
        try:
            elapsed = float(elapsed)
        except (TypeError, ValueError):
            elapsed = None
    return StallEvent(ts_utc=ts, instrument=inst, last_bar_utc=last_bar, elapsed_seconds=elapsed, event_type=et)


def classify_stall(pair: StallPair, bar_interval_min: int = 1) -> str:
    """Classify stall: low_liquidity, feed_delay, disconnect_reconnect, bar_aggregation_delay."""
    dur = pair.stall_duration_sec
    if pair.connection_lost_nearby:
        return "disconnect_reconnect"
    # Bar gap: if last_bar was ~N min ago and recovery bar is ~N min later, typical 1-min bar delay
    if pair.time_since_prev_bar_sec is not None:
        gap = pair.time_since_prev_bar_sec
        if gap > 300:  # >5 min
            return "low_liquidity"  # No trades for long time
        if 170 <= gap <= 200:  # ~3 min = 2-3 bars
            return "bar_aggregation_delay"  # 1-min bars, slight delay
    if dur <= 250:  # ~4 min
        return "feed_delay"  # Brief delay
    if dur > 600:  # >10 min
        return "low_liquidity"  # Long gap
    return "feed_delay"


def main() -> None:
    path = FEED_PATH
    if not path.exists():
        print(f"Feed not found: {path}", file=sys.stderr)
        sys.exit(1)

    events = load_events(path)
    stall_events: list[StallEvent] = []
    connection_lost_ts: list[datetime] = []
    bar_events_by_inst: dict[str, list[datetime]] = defaultdict(list)

    for ev in events:
        et = ev.get("event_type")
        if et == "CONNECTION_LOST":
            ts = parse_ts(ev.get("timestamp_utc"))
            if ts:
                connection_lost_ts.append(ts)
        elif et in ("DATA_STALL_RECOVERED", "DATA_LOSS_DETECTED"):
            se = parse_event(ev)
            if se:
                stall_events.append(se)
        elif et in ("BAR_ACCEPTED", "BAR_RECEIVED_NO_STREAMS"):
            inst = extract_instrument(ev)
            if inst:
                ts = parse_ts(ev.get("timestamp_utc"))
                if ts:
                    bar_events_by_inst[inst].append(ts)

    # Sort by time
    stall_events.sort(key=lambda e: e.ts_utc)
    connection_lost_ts.sort()

    # Build pairs: for each RECOVERED, find preceding LOSS for same instrument
    pairs: list[StallPair] = []
    loss_by_inst: dict[str, StallEvent] = {}

    for se in stall_events:
        if se.event_type == "DATA_LOSS_DETECTED":
            loss_by_inst[se.instrument] = se
        elif se.event_type == "DATA_STALL_RECOVERED":
            loss = loss_by_inst.get(se.instrument)
            if loss and se.last_bar_utc:
                stall_dur = (se.ts_utc - loss.ts_utc).total_seconds()
                # Time since previous bar: recovery bar time minus last bar before stall
                time_since_prev = None
                if loss.last_bar_utc:
                    time_since_prev = (se.last_bar_utc - loss.last_bar_utc).total_seconds()
                conn_nearby = any(
                    abs((se.ts_utc - cl).total_seconds()) <= 300
                    for cl in connection_lost_ts
                )
                # Multi-instrument: count distinct instruments with LOSS in same 60s window
                window_start = loss.ts_utc
                inst_in_window = {
                    e.instrument for e in stall_events
                    if e.event_type == "DATA_LOSS_DETECTED"
                    and abs((e.ts_utc - window_start).total_seconds()) <= 60
                }
                multi = len(inst_in_window) > 1
                pair = StallPair(
                    loss=loss,
                    recovered=se,
                    stall_duration_sec=stall_dur,
                    time_since_prev_bar_sec=time_since_prev,
                    connection_lost_nearby=conn_nearby,
                    multi_instrument_stall=multi,
                )
                pair.classification = classify_stall(pair)
                pairs.append(pair)
            loss_by_inst.pop(se.instrument, None)  # Clear after recovery

    # --- Output ---
    print("=" * 80)
    print("DATA_STALL PATTERN DIAGNOSIS")
    print("=" * 80)
    print(f"Source: {path}")
    print(f"DATA_STALL_RECOVERED events: {len([e for e in stall_events if e.event_type == 'DATA_STALL_RECOVERED'])}")
    print(f"DATA_LOSS_DETECTED events: {len([e for e in stall_events if e.event_type == 'DATA_LOSS_DETECTED'])}")
    print(f"Matched pairs: {len(pairs)}")
    print()

    if not pairs:
        print("No matched stall/recovery pairs found.")
        return

    # Per-event summary
    print("-" * 80)
    print("PER-EVENT SUMMARY (first 50)")
    print("-" * 80)
    for i, p in enumerate(pairs[:50]):
        inst = p.recovered.instrument
        last_bar = p.recovered.last_bar_utc.strftime("%Y-%m-%dT%H:%M:%SZ") if p.recovered.last_bar_utc else "N/A"
        ts_since = f"{p.time_since_prev_bar_sec:.0f}s" if p.time_since_prev_bar_sec is not None else "N/A"
        loss_ts = p.loss.ts_utc.strftime("%H:%M:%S UTC")
        rec_ts = p.recovered.ts_utc.strftime("%H:%M:%S UTC")
        print(f"{i+1:3}. {inst:6} | last_bar={last_bar} | time_since_prev={ts_since} | "
              f"LOSS@{loss_ts} REC@{rec_ts} | dur={p.stall_duration_sec:.0f}s | "
              f"{p.classification} | CONN_LOST={p.connection_lost_nearby} | multi={p.multi_instrument_stall}")
    if len(pairs) > 50:
        print(f"... and {len(pairs) - 50} more")

    # Per-instrument
    print()
    print("-" * 80)
    print("PER-INSTRUMENT STALL FREQUENCY")
    print("-" * 80)
    by_inst: dict[str, list[StallPair]] = defaultdict(list)
    for p in pairs:
        by_inst[p.recovered.instrument].append(p)

    for inst in sorted(by_inst.keys()):
        pl = by_inst[inst]
        print(f"  {inst:8}: {len(pl):4} stalls")

    # Average stall duration
    print()
    print("-" * 80)
    print("AVERAGE STALL DURATION (seconds)")
    print("-" * 80)
    for inst in sorted(by_inst.keys()):
        pl = by_inst[inst]
        avg = sum(p.stall_duration_sec for p in pl) / len(pl)
        print(f"  {inst:8}: {avg:.1f}s avg")

    # Classification
    print()
    print("-" * 80)
    print("CLASSIFICATION BREAKDOWN")
    print("-" * 80)
    by_class: dict[str, int] = defaultdict(int)
    for p in pairs:
        by_class[p.classification] += 1
    for c in sorted(by_class.keys()):
        print(f"  {c:25}: {by_class[c]:4}")

    # Active vs quiet
    print()
    print("-" * 80)
    print("ACTIVE SESSION vs QUIET PERIOD")
    print("-" * 80)
    active = sum(1 for p in pairs if is_active_session_ct(p.recovered.ts_utc))
    quiet = len(pairs) - active
    pct_active = 100 * active / len(pairs) if pairs else 0
    pct_quiet = 100 * quiet / len(pairs) if pairs else 0
    print(f"  During RTH (8:30-15:15 CT): {active:4} ({pct_active:.1f}%)")
    print(f"  Quiet period:              {quiet:4} ({pct_quiet:.1f}%)")

    # CONNECTION_LOST nearby
    print()
    print("-" * 80)
    print("CONNECTION_LOST NEARBY (±5 min)")
    print("-" * 80)
    conn_nearby = sum(1 for p in pairs if p.connection_lost_nearby)
    print(f"  Stalls with CONNECTION_LOST nearby: {conn_nearby} / {len(pairs)}")

    # Multi-instrument
    print()
    print("-" * 80)
    print("MULTI-INSTRUMENT SIMULTANEOUS STALLS")
    print("-" * 80)
    multi = sum(1 for p in pairs if p.multi_instrument_stall)
    print(f"  Stalls with multiple instruments in same 60s window: {multi} / {len(pairs)}")


if __name__ == "__main__":
    main()
