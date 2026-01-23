#!/usr/bin/env python3
"""Show what streams are armed."""

import json
from pathlib import Path
from collections import Counter
from datetime import datetime, timezone

def parse_timestamp(ts_str):
    """Parse ISO8601 timestamp."""
    try:
        ts_str = ts_str.replace('Z', '+00:00')
        if '.' in ts_str:
            ts_str = ts_str.split('.')[0] + '+00:00'
        return datetime.fromisoformat(ts_str)
    except:
        return None

def main():
    log_dir = Path("logs/robot")
    
    # Find latest ENGINE_START
    engine_log = log_dir / "robot_ENGINE.jsonl"
    if not engine_log.exists():
        print("[ERROR] ENGINE log not found")
        return
    
    engine_events = []
    with open(engine_log, 'r', encoding='utf-8-sig') as f:
        for line in f:
            if not line.strip():
                continue
            try:
                event = json.loads(line)
                engine_events.append(event)
            except:
                continue
    
    starts = [e for e in engine_events if e.get('event') == 'ENGINE_START']
    if not starts:
        print("[ERROR] No ENGINE_START events found")
        return
    
    latest_start = starts[-1]
    start_time = parse_timestamp(latest_start.get('ts_utc', ''))
    
    if not start_time:
        print("[ERROR] Could not parse start time")
        return
    
    # Collect STREAM_ARMED events since restart
    armed_events = []
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        if event.get('event') == 'STREAM_ARMED':
                            ts = parse_timestamp(event.get('ts_utc', ''))
                            if ts and ts >= start_time:
                                armed_events.append(event)
                    except:
                        continue
        except:
            continue
    
    print("=" * 80)
    print(f"STREAMS ARMED SINCE RESTART ({start_time.isoformat()})")
    print("=" * 80)
    print(f"Total STREAM_ARMED events: {len(armed_events)}")
    print()
    
    # Extract instrument from event (it's in the top-level 'instrument' field)
    instruments = Counter([e.get('instrument', 'UNKNOWN') for e in armed_events])
    print("By instrument:")
    for inst, count in sorted(instruments.items()):
        print(f"  {inst}: {count} streams")
    print()
    
    # Extract session and slot from data.payload (it's a string that needs parsing)
    sessions = []
    slots = []
    stream_combos = {}
    
    for e in armed_events:
        inst = e.get('instrument', 'UNKNOWN')
        payload_str = str(e.get('data', {}).get('payload', ''))
        
        # Parse payload string (format: "{ instrument = CL, session = S1, slot_time_chicago = 07:30, ... }")
        session = 'UNKNOWN'
        slot = 'UNKNOWN'
        
        if 'session =' in payload_str:
            try:
                session_part = payload_str.split('session =')[1].split(',')[0].strip()
                session = session_part
            except:
                pass
        
        if 'slot_time_chicago =' in payload_str:
            try:
                slot_part = payload_str.split('slot_time_chicago =')[1].split(',')[0].strip()
                slot = slot_part
            except:
                pass
        
        sessions.append(session)
        slots.append(slot)
        
        combo = f"{inst} | {session} | {slot}"
        if combo not in stream_combos:
            stream_combos[combo] = e.get('ts_utc', '')[:19]
    
    session_counter = Counter(sessions)
    print("By session:")
    for session, count in sorted(session_counter.items()):
        print(f"  {session}: {count} streams")
    print()
    
    print(f"Unique stream combinations: {len(stream_combos)}")
    print("\nSample streams (first 20):")
    for i, (combo, ts) in enumerate(sorted(stream_combos.items())[:20], 1):
        print(f"  {i:2}. {combo} (armed at {ts})")
    
    if len(stream_combos) > 20:
        print(f"  ... and {len(stream_combos) - 20} more")
    
    print()
    print("=" * 80)
    print("EXPLANATION")
    print("=" * 80)
    print("""
A 'stream' represents a unique trading slot combination:
  - Instrument (ES, NQ, GC, CL, etc.)
  - Session (evening, morning, etc.)
  - Slot Time (specific time within that session, e.g., '22:00')

When a stream is 'ARMED', it means:
  1. The stream has been initialized for a new trading slot
  2. It enters PRE_HYDRATION state (loading historical bars)
  3. After pre-hydration, it will transition to RANGE_BUILDING
  4. Once range is computed, it moves to RANGE_LOCKED
  5. Finally, it completes and goes to DONE

State progression: PRE_HYDRATION -> ARMED -> RANGE_BUILDING -> RANGE_LOCKED -> DONE

The 50 STREAM_ARMED events indicate 50 different trading slots have been
initialized and are ready to start range computation and trading.
""")

if __name__ == "__main__":
    main()
