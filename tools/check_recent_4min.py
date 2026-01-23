#!/usr/bin/env python3
"""Check logging activity in the last 4 minutes."""

import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import Counter

def parse_timestamp(ts_str):
    """Parse ISO8601 timestamp."""
    try:
        # Handle various formats
        ts_str = ts_str.replace('Z', '+00:00')
        if '.' in ts_str:
            ts_str = ts_str.split('.')[0] + '+00:00'
        return datetime.fromisoformat(ts_str)
    except:
        return None

def main():
    cutoff = datetime.now(timezone.utc) - timedelta(minutes=4)
    print("=" * 80)
    print(f"CHECKING LAST 4 MINUTES (since {cutoff.isoformat()})")
    print("=" * 80)
    print()
    
    log_dir = Path("logs/robot")
    if not log_dir.exists():
        print("[ERROR] Log directory not found")
        return
    
    # Collect all events from last 4 minutes
    all_events = []
    errors = []
    warnings = []
    
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        ts = parse_timestamp(event.get('ts_utc', ''))
                        if ts and ts > cutoff:
                            all_events.append(event)
                            if event.get('level') == 'ERROR':
                                errors.append(event)
                            elif event.get('level') == 'WARN':
                                warnings.append(event)
                    except json.JSONDecodeError:
                        continue
        except Exception as e:
            print(f"[WARN] Error reading {log_file.name}: {e}")
            continue
    
    print(f"Total events in last 4 minutes: {len(all_events)}")
    print(f"  - ERROR: {len(errors)}")
    print(f"  - WARN: {len(warnings)}")
    print(f"  - INFO/DEBUG: {len(all_events) - len(errors) - len(warnings)}")
    print()
    
    # Level distribution
    levels = Counter([e.get('level', 'UNKNOWN') for e in all_events])
    print("Level distribution:")
    for level, count in sorted(levels.items()):
        print(f"  {level}: {count}")
    print()
    
    # Top event types
    event_types = Counter([e.get('event', 'UNKNOWN') for e in all_events])
    print("Top 10 event types:")
    for event_type, count in event_types.most_common(10):
        print(f"  {event_type}: {count}")
    print()
    
    # Recent errors
    if errors:
        print(f"Recent ERROR events (showing last 10):")
        for e in errors[-10:]:
            ts = e.get('ts_utc', '')[:19]
            inst = e.get('instrument', '')
            event_type = e.get('event', '')
            msg = e.get('message', '')[:60]
            print(f"  {ts} | {inst:4} | {event_type:30} | {msg}")
        print()
    
    # Check for logging system issues
    logging_issues = [
        e for e in all_events 
        if e.get('event') in ['LOG_BACKPRESSURE_DROP', 'LOG_WORKER_LOOP_ERROR', 'LOG_WRITE_FAILURE']
    ]
    
    if logging_issues:
        print(f"[WARN] Found {len(logging_issues)} logging system issues:")
        for e in logging_issues:
            print(f"  {e.get('ts_utc', '')[:19]} | {e.get('event')} | {e.get('message', '')}")
    else:
        print("[OK] No logging system issues detected")
    print()
    
    # Check for key events
    key_events = ['ENGINE_START', 'PROJECT_ROOT_RESOLVED', 'LOG_DIR_RESOLVED', 
                  'RANGE_LOCKED', 'ORDER_SUBMITTED', 'EXECUTION_FILLED']
    found_key = [e for e in all_events if e.get('event') in key_events]
    
    if found_key:
        print("Key events found:")
        for e in found_key[-5:]:
            print(f"  {e.get('ts_utc', '')[:19]} | {e.get('level')} | {e.get('event')}")
    print()
    
    # Latest events
    print("Latest 5 events:")
    for e in sorted(all_events, key=lambda x: x.get('ts_utc', ''))[-5:]:
        ts = e.get('ts_utc', '')[:19]
        level = e.get('level', '')
        event_type = e.get('event', '')
        inst = e.get('instrument', '')
        print(f"  {ts} | {level:5} | {inst:4} | {event_type}")

if __name__ == "__main__":
    main()
