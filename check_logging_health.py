#!/usr/bin/env python3
"""Comprehensive logging health check"""
import json
import glob
from datetime import datetime, timezone, timedelta
from collections import Counter

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("LOGGING HEALTH CHECK")
    print("=" * 80)
    
    now_utc = datetime.now(timezone.utc)
    ten_minutes_ago = now_utc - timedelta(minutes=10)
    
    # Get all events
    all_events = []
    for log_file in log_files:
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        entry = json.loads(line)
                        all_events.append(entry)
                    except:
                        continue
        except:
            continue
    
    # Filter recent events
    recent_events = []
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= ten_minutes_ago:
                    recent_events.append((ts, entry))
        except:
            continue
    
    recent_events.sort(key=lambda x: x[0])
    
    print(f"\nTotal events in last 10 minutes: {len(recent_events)}")
    
    # Count by event type
    event_counts = Counter()
    for ts, entry in recent_events:
        event_type = entry.get('event', 'UNKNOWN')
        event_counts[event_type] += 1
    
    print("\n" + "=" * 80)
    print("EVENT TYPE BREAKDOWN (Last 10 minutes)")
    print("=" * 80)
    for event_type, count in event_counts.most_common(20):
        print(f"  {event_type:40s} {count:6d}")
    
    # Check for fix-related events
    print("\n" + "=" * 80)
    print("FIX VERIFICATION")
    print("=" * 80)
    
    rollover_count = event_counts.get('TRADING_DAY_ROLLOVER', 0)
    init_count = event_counts.get('TRADING_DATE_INITIALIZED', 0)
    backward_count = event_counts.get('TRADING_DATE_BACKWARD', 0)
    
    print(f"TRADING_DAY_ROLLOVER:        {rollover_count:6d} {'[FIXED]' if rollover_count == 0 else '[STILL SPAMMING]'}")
    print(f"TRADING_DATE_INITIALIZED:     {init_count:6d} {'[Present]' if init_count > 0 else '[Not seen - may have already initialized]'}")
    print(f"TRADING_DATE_BACKWARD:       {backward_count:6d} {'[Present]' if backward_count > 0 else '[Not seen - no backward dates]'}")
    
    # Check stream state transitions
    print("\n" + "=" * 80)
    print("STREAM STATE TRANSITIONS")
    print("=" * 80)
    
    pre_hydration = event_counts.get('PRE_HYDRATION_COMPLETE', 0)
    armed = event_counts.get('ARMED', 0)
    range_started = event_counts.get('RANGE_WINDOW_STARTED', 0)
    range_locked = event_counts.get('RANGE_LOCKED', 0)
    
    print(f"PRE_HYDRATION_COMPLETE:      {pre_hydration:6d}")
    print(f"ARMED:                        {armed:6d}")
    print(f"RANGE_WINDOW_STARTED:        {range_started:6d} {'[OK]' if range_started > 0 else '[MISSING]'}")
    print(f"RANGE_LOCKED:                 {range_locked:6d}")
    
    # Check for errors
    print("\n" + "=" * 80)
    print("ERROR CHECK")
    print("=" * 80)
    
    error_events = []
    for ts, entry in recent_events:
        event_type = entry.get('event', '')
        level = entry.get('level', '')
        if 'ERROR' in event_type or 'FAILED' in event_type or level == 'ERROR':
            error_events.append((ts, entry))
    
    print(f"Error events: {len(error_events)}")
    if error_events:
        print("\nRecent errors:")
        for ts, entry in error_events[-10:]:
            event_type = entry.get('event', 'UNKNOWN')
            print(f"  [{ts.strftime('%H:%M:%S')}] {event_type}")
    
    # Show latest events
    print("\n" + "=" * 80)
    print("LATEST 20 EVENTS")
    print("=" * 80)
    for ts, entry in recent_events[-20:]:
        event_type = entry.get('event', 'UNKNOWN')
        data = entry.get('data', {})
        state = data.get('state', '')
        session = data.get('session', '')
        print(f"[{ts.strftime('%H:%M:%S')}] {event_type:30s} | State: {state:15s} | Session: {session}")

if __name__ == '__main__':
    main()
