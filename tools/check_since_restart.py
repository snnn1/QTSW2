#!/usr/bin/env python3
"""Check logs since last restart (ENGINE_START event)."""

import json
from pathlib import Path
from datetime import datetime, timezone
from collections import Counter

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
    if not log_dir.exists():
        print("[ERROR] Log directory not found")
        return
    
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
    
    print("=" * 80)
    print(f"LOGS SINCE LAST RESTART")
    print("=" * 80)
    print(f"Restart time: {start_time.isoformat()}")
    print()
    
    # Collect all events since restart
    all_events = []
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        ts = parse_timestamp(event.get('ts_utc', ''))
                        if ts and ts >= start_time:
                            all_events.append(event)
                    except:
                        continue
        except Exception as e:
            print(f"[WARN] Error reading {log_file.name}: {e}")
            continue
    
    print(f"Total events since restart: {len(all_events)}")
    print()
    
    # Level distribution
    levels = Counter([e.get('level', 'UNKNOWN') for e in all_events])
    print("Level distribution:")
    for level, count in sorted(levels.items()):
        print(f"  {level}: {count}")
    print()
    
    # Top event types
    event_types = Counter([e.get('event', 'UNKNOWN') for e in all_events])
    print("Top 15 event types:")
    for event_type, count in event_types.most_common(15):
        print(f"  {event_type}: {count}")
    print()
    
    # Errors
    errors = [e for e in all_events if e.get('level') == 'ERROR']
    print(f"ERROR events: {len(errors)}")
    if errors:
        error_types = Counter([e.get('event', 'UNKNOWN') for e in errors])
        print("  Error breakdown:")
        for err_type, count in error_types.most_common(10):
            print(f"    {err_type}: {count}")
        print()
        print("  Recent ERROR events:")
        for e in errors[-10:]:
            ts = e.get('ts_utc', '')[:19]
            inst = e.get('instrument', '')
            event_type = e.get('event', '')
            print(f"    {ts} | {inst:4} | {event_type}")
    print()
    
    # RANGE_COMPUTE_FAILED with reason codes
    failures = [e for e in all_events if e.get('event') == 'RANGE_COMPUTE_FAILED']
    print(f"RANGE_COMPUTE_FAILED events: {len(failures)}")
    if failures:
        reasons = Counter([e.get('data', {}).get('reason', 'N/A') for e in failures])
        categories = Counter([e.get('data', {}).get('reason_category', 'N/A') for e in failures])
        
        print("  Reason codes:")
        for reason, count in reasons.most_common():
            print(f"    {reason}: {count}")
        
        print("  Categories:")
        for cat, count in categories.most_common():
            print(f"    {cat}: {count}")
        
        print("\n  Sample failures:")
        for e in failures[:5]:
            ts = e.get('ts_utc', '')[:19]
            inst = e.get('instrument', '')
            reason = e.get('data', {}).get('reason', 'N/A')
            category = e.get('data', {}).get('reason_category', 'N/A')
            print(f"    {ts} | {inst:4} | reason={reason:25} | category={category}")
    print()
    
    # Key events
    key_events = ['RANGE_LOCKED', 'ORDER_SUBMITTED', 'EXECUTION_FILLED', 'STREAM_ARMED']
    found_key = [e for e in all_events if e.get('event') in key_events]
    if found_key:
        print("Key events found:")
        key_counter = Counter([e.get('event') for e in found_key])
        for event_type, count in sorted(key_counter.items()):
            print(f"  {event_type}: {count}")
    print()
    
    # Latest events
    print("Latest 10 events:")
    for e in sorted(all_events, key=lambda x: x.get('ts_utc', ''))[-10:]:
        ts = e.get('ts_utc', '')[:19]
        level = e.get('level', '')
        event_type = e.get('event', '')
        inst = e.get('instrument', '')
        print(f"  {ts} | {level:5} | {inst:4} | {event_type}")

if __name__ == "__main__":
    main()
