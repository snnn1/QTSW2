#!/usr/bin/env python3
"""Check for crash at 15:00 UTC - look for logging gaps."""

import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

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
    
    print("=" * 80)
    print("CRASH ANALYSIS - 15:00 UTC (9:00 AM CT) on JANUARY 22, 2026")
    print("=" * 80)
    print()
    
    # Collect all events from Jan 22
    all_events = []
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        ts_str = event.get('ts_utc', '')
                        if ts_str.startswith('2026-01-22'):
                            ts = parse_timestamp(ts_str)
                            if ts:
                                all_events.append((ts, event))
                    except:
                        continue
        except:
            continue
    
    # Sort by timestamp
    all_events.sort(key=lambda x: x[0])
    
    print(f"Total events on Jan 22: {len(all_events)}")
    
    if not all_events:
        print("\n[ERROR] No events found for Jan 22")
        return
    
    # Find events around 15:00 UTC
    crash_time = datetime(2026, 1, 22, 15, 0, 0, tzinfo=timezone.utc)
    window_start = crash_time - timedelta(minutes=10)
    window_end = crash_time + timedelta(minutes=30)
    
    events_before = [e for ts, e in all_events if ts < crash_time]
    events_after = [e for ts, e in all_events if ts >= crash_time]
    events_in_window = [e for ts, e in all_events if window_start <= ts <= window_end]
    
    print(f"\nEvents before 15:00 UTC: {len(events_before)}")
    print(f"Events after 15:00 UTC: {len(events_after)}")
    print(f"Events in window (14:50-15:30 UTC): {len(events_in_window)}")
    print()
    
    # Find last event before crash
    if events_before:
        last_before = max([ts for ts, e in all_events if ts < crash_time])
        last_event = [e for ts, e in all_events if ts == last_before][0]
        print(f"Last event before crash:")
        print(f"  Time: {last_before}")
        print(f"  Event: {last_event.get('event', 'N/A')}")
        print(f"  Instrument: {last_event.get('instrument', 'N/A')}")
        print(f"  Level: {last_event.get('level', 'N/A')}")
        print()
        
        gap_minutes = (crash_time - last_before).total_seconds() / 60
        print(f"Gap before crash: {gap_minutes:.1f} minutes")
        print()
    
    # Find first event after crash
    if events_after:
        first_after = min([ts for ts, e in all_events if ts >= crash_time])
        first_event = [e for ts, e in all_events if ts == first_after][0]
        print(f"First event after crash:")
        print(f"  Time: {first_after}")
        print(f"  Event: {first_event.get('event', 'N/A')}")
        print(f"  Instrument: {first_event.get('instrument', 'N/A')}")
        print(f"  Level: {first_event.get('level', 'N/A')}")
        print()
        
        gap_minutes = (first_after - crash_time).total_seconds() / 60
        print(f"Gap after crash: {gap_minutes:.1f} minutes")
        print()
        
        # Check if first event is ENGINE_START (restart)
        if first_event.get('event') == 'ENGINE_START':
            print("[INFO] First event after crash is ENGINE_START - system was restarted")
            print()
    else:
        print("[WARN] No events found after 15:00 UTC - system may have crashed completely")
        print()
    
    # Check for ERROR/WARN events just before crash
    error_events_before = [(ts, e) for ts, e in all_events if e.get('level') in ['ERROR', 'WARN'] and ts < crash_time and ts >= crash_time - timedelta(minutes=5)]
    
    if error_events_before:
        print(f"ERROR/WARN events in 5 minutes before crash: {len(error_events_before)}")
        error_types = defaultdict(int)
        for ts, e in error_events_before:
            error_types[e.get('event', 'N/A')] += 1
        
        print("\nError types:")
        for event_type, count in sorted(error_types.items(), key=lambda x: x[1], reverse=True):
            print(f"  {event_type}: {count}")
        print()
    
    # Check for notification events
    notification_events = [e for ts, e in all_events if 'PUSHOVER' in e.get('event', '') or 'NOTIFY' in e.get('event', '')]
    notifications_around_crash = [e for ts, e in notification_events if window_start <= ts <= window_end]
    
    print(f"Notification events around crash: {len(notifications_around_crash)}")
    if len(notifications_around_crash) == 0:
        print("[WARN] No notifications sent around crash time")
        print("This confirms the notification system was not triggered")
    print()
    
    # Summary
    print("=" * 80)
    print("SUMMARY")
    print("=" * 80)
    print(f"\nCrash time: {crash_time} UTC (9:00 AM CT)")
    
    if events_before and events_after:
        last_before = max([ts for ts, e in all_events if ts < crash_time])
        first_after = min([ts for ts, e in all_events if ts >= crash_time])
        downtime = (first_after - last_before).total_seconds() / 60
        print(f"System downtime: {downtime:.1f} minutes")
        print(f"Last event before crash: {last_before}")
        print(f"First event after crash: {first_after}")
    elif events_before:
        print(f"Last event before crash: {max([ts for ts, e in all_events if ts < crash_time])}")
        print("[WARN] No events found after crash - system may have crashed completely")
    else:
        print("[ERROR] No events found before crash either")

if __name__ == "__main__":
    main()
