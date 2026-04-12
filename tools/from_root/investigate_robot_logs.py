#!/usr/bin/env python3
"""
Full Robot Log Investigation
Analyzes stalls, market status, errors, and system health.
"""
import sys
import json
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict
import pytz

qtsw2_root = Path(__file__).parent
sys.path.insert(0, str(qtsw2_root))

CHICAGO = pytz.timezone("America/Chicago")


def load_market_session():
    from modules.watchdog.market_session import is_market_open
    return is_market_open


def main():
    feed_path = qtsw2_root / "logs" / "robot" / "frontend_feed.jsonl"
    engine_path = qtsw2_root / "logs" / "robot" / "robot_ENGINE.jsonl"

    print("=" * 80)
    print("FULL ROBOT LOG INVESTIGATION")
    print("=" * 80)
    print(f"Timestamp: {datetime.now().isoformat()}")
    print()

    # --- 1. ENGINE TICK STALLS (frontend_feed) ---
    print("=" * 80)
    print("1. ENGINE TICK STALL ANALYSIS (Watchdog-detected)")
    print("=" * 80)
    stalls = []
    if feed_path.exists():
        with open(feed_path, "r", encoding="utf-8-sig") as f:
            for line in f:
                try:
                    e = json.loads(line.strip())
                    if e.get("event_type") == "ENGINE_TICK_STALL_DETECTED":
                        stalls.append(e)
                except Exception:
                    pass

    print(f"Total ENGINE_TICK_STALL_DETECTED events: {len(stalls)}")
    if stalls:
        first_ts = stalls[0].get("timestamp_utc", "")
        last_ts = stalls[-1].get("timestamp_utc", "")
        print(f"First: {first_ts[:19]}")
        print(f"Last:  {last_ts[:19]}")
        try:
            is_market_open = load_market_session()
            open_count = closed_count = 0
            for e in stalls:
                ts = e.get("timestamp_utc", "")
                if ts:
                    dt = datetime.fromisoformat(ts.replace("Z", "+00:00"))
                    chicago = dt.astimezone(CHICAGO)
                    if is_market_open(chicago):
                        open_count += 1
                    else:
                        closed_count += 1
            print(f"Stalls when market OPEN:   {open_count}")
            print(f"Stalls when market CLOSED: {closed_count}")
            if len(stalls) >= 2:
                intervals = []
                for i in range(1, len(stalls)):
                    t1 = datetime.fromisoformat(
                        stalls[i - 1].get("timestamp_utc", "").replace("Z", "+00:00")
                    )
                    t2 = datetime.fromisoformat(
                        stalls[i].get("timestamp_utc", "").replace("Z", "+00:00")
                    )
                    intervals.append((t2 - t1).total_seconds())
                avg = sum(intervals) / len(intervals)
                print(f"Avg interval between stalls: {avg:.1f}s")
                print(f"Min/Max interval: {min(intervals):.0f}s / {max(intervals):.0f}s")
        except Exception as ex:
            print(f"[ERROR] Market check: {ex}")
    else:
        print("No stall events found.")
    print()

    # --- 2. ROBOT ENGINE ERRORS/WARNINGS ---
    print("=" * 80)
    print("2. ROBOT ENGINE ERRORS & WARNINGS")
    print("=" * 80)
    errors = defaultdict(list)
    warns = defaultdict(list)
    if engine_path.exists():
        with open(engine_path, "r", encoding="utf-8-sig") as f:
            lines = f.readlines()
            recent = lines[-5000:] if len(lines) > 5000 else lines
            for line in recent:
                try:
                    e = json.loads(line.strip())
                    evt = e.get("event", "")
                    ts = e.get("ts_utc", "")[:19]
                    if e.get("level") == "ERROR":
                        errors[evt].append(ts)
                    elif e.get("level") == "WARN":
                        warns[evt].append(ts)
                except Exception:
                    pass

    print("ERROR events (last 5000 lines):")
    for evt, timestamps in sorted(errors.items(), key=lambda x: -len(x[1])):
        print(f"  {evt}: {len(timestamps)} occurrences")
        if timestamps:
            print(f"    Latest: {timestamps[-1]}")
    print("WARN events:")
    for evt, timestamps in sorted(warns.items(), key=lambda x: -len(x[1])):
        print(f"  {evt}: {len(timestamps)} occurrences")
        if timestamps:
            print(f"    Latest: {timestamps[-1]}")
    print()

    # --- 3. EVENT TYPE COUNTS (frontend_feed) ---
    print("=" * 80)
    print("3. FRONTEND FEED EVENT SUMMARY (recent 5000 lines)")
    print("=" * 80)
    event_counts = defaultdict(int)
    if feed_path.exists():
        with open(feed_path, "r", encoding="utf-8-sig") as f:
            lines = f.readlines()
            recent = lines[-5000:] if len(lines) > 5000 else lines
            for line in recent:
                try:
                    e = json.loads(line.strip())
                    event_counts[e.get("event_type", "?")] += 1
                except Exception:
                    pass
    for evt, cnt in sorted(event_counts.items(), key=lambda x: -x[1]):
        print(f"  {evt}: {cnt}")
    print()

    # --- 4. MARKET STATUS ---
    print("=" * 80)
    print("4. CURRENT MARKET STATUS")
    print("=" * 80)
    try:
        is_market_open = load_market_session()
        chicago_now = datetime.now(CHICAGO)
        open_ = is_market_open(chicago_now)
        print(f"Chicago time: {chicago_now.strftime('%Y-%m-%d %H:%M:%S %Z')}")
        print(f"Market open: {open_}")
    except Exception as ex:
        print(f"[ERROR] {ex}")
    print()

    # --- 5. DATA FLOW (bar/tick age) ---
    print("=" * 80)
    print("5. DATA FLOW (bar & tick age)")
    print("=" * 80)
    bar_events = []
    tick_events = []
    if feed_path.exists():
        with open(feed_path, "r", encoding="utf-8-sig") as f:
            lines = f.readlines()
            recent = lines[-2000:] if len(lines) > 2000 else lines
            for line in recent:
                try:
                    e = json.loads(line.strip())
                    et = e.get("event_type", "")
                    ts = e.get("timestamp_utc", "")
                    if et in ("BAR_ACCEPTED", "ONBARUPDATE_CALLED", "BAR_RECEIVED_NO_STREAMS"):
                        bar_events.append((et, ts))
                    elif et == "ENGINE_TICK_CALLSITE":
                        tick_events.append(ts)
                except Exception:
                    pass
    now = datetime.now(timezone.utc)
    if bar_events:
        latest_bar_ts = bar_events[-1][1]
        try:
            bar_dt = datetime.fromisoformat(latest_bar_ts.replace("Z", "+00:00"))
            age = (now - bar_dt).total_seconds()
            print(f"Latest bar event: {bar_events[-1][0]} at {latest_bar_ts[:19]}")
            print(f"  Age: {age:.1f}s")
        except Exception:
            print("Could not parse bar timestamp")
    else:
        print("No bar events in recent feed")
    if tick_events:
        latest_tick = tick_events[-1]
        try:
            tick_dt = datetime.fromisoformat(latest_tick.replace("Z", "+00:00"))
            age = (now - tick_dt).total_seconds()
            print(f"Latest tick (ENGINE_TICK_CALLSITE): {latest_tick[:19]}")
            print(f"  Age: {age:.1f}s")
        except Exception:
            print("Could not parse tick timestamp")
    else:
        print("No tick events in recent feed")
    print()

    # --- 6. ORCHESTRATOR ---
    orch_path = qtsw2_root / "automation" / "logs" / "orchestrator_state.json"
    if orch_path.exists():
        print("=" * 80)
        print("6. ORCHESTRATOR STATE")
        print("=" * 80)
        with open(orch_path) as f:
            orch = json.load(f)
        print(f"  State: {orch.get('state')}")
        print(f"  Run health: {orch.get('metadata', {}).get('run_health')}")
        print(f"  Updated: {orch.get('updated_at')}")
    print()

    print("=" * 80)
    print("INVESTIGATION COMPLETE")
    print("=" * 80)


if __name__ == "__main__":
    main()
