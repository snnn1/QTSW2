#!/usr/bin/env python3
"""Summary of range lock logging analysis."""

import json
from pathlib import Path
from collections import defaultdict
from datetime import datetime

def analyze():
    log_dir = Path("logs/robot")
    today = datetime.now().strftime("%Y-%m-%d")
    
    hydration_file = log_dir / f"hydration_{today}.jsonl"
    ranges_file = log_dir / f"ranges_{today}.jsonl"
    
    print("=" * 80)
    print("RANGE LOCK IMPLEMENTATION VERIFICATION")
    print("=" * 80)
    print()
    
    # Check hydration log (new code)
    if hydration_file.exists():
        print("HYDRATION LOG (New Code - After Restart):")
        print("-" * 80)
        
        events = []
        with open(hydration_file, 'r', encoding='utf-8') as f:
            for line in f:
                if not line.strip():
                    continue
                try:
                    event = json.loads(line)
                    if event.get('event_type') == 'RANGE_LOCKED':
                        events.append(event)
                except:
                    continue
        
        by_stream = defaultdict(list)
        for event in events:
            stream_id = event.get('stream_id', 'UNKNOWN')
            by_stream[stream_id].append(event)
        
        print(f"Total RANGE_LOCKED events: {len(events)}")
        print()
        
        all_good = True
        for stream_id, stream_events in sorted(by_stream.items()):
            count = len(stream_events)
            if count > 1:
                all_good = False
                print(f"[ERROR] {stream_id}: {count} events (DUPLICATE!)")
            else:
                event = stream_events[0]
                data = event.get('data', {})
                breakout_missing = data.get('breakout_levels_missing', False)
                status = "OK" if not breakout_missing else "WARNING (breakout missing)"
                print(f"[OK] {stream_id}: 1 event, {status}")
        
        if all_good:
            print()
            print("[SUCCESS] No duplicates in hydration log - new code working correctly!")
        print()
    
    # Check ranges log (all events including old code)
    if ranges_file.exists():
        print("RANGES LOG (All Events - Including Old Code):")
        print("-" * 80)
        
        events = []
        with open(ranges_file, 'r', encoding='utf-8') as f:
            for line in f:
                if not line.strip():
                    continue
                try:
                    event = json.loads(line)
                    if event.get('event_type') == 'RANGE_LOCKED':
                        events.append(event)
                except:
                    continue
        
        # Group by stream and check for duplicates
        by_stream = defaultdict(list)
        for event in events:
            stream_id = event.get('stream_id', 'UNKNOWN')
            by_stream[stream_id].append(event)
        
        print(f"Total RANGE_LOCKED events: {len(events)}")
        print()
        
        # Separate by time (before/after restart ~16:31)
        restart_time = datetime.fromisoformat("2026-01-29T16:31:00+00:00")
        
        for stream_id, stream_events in sorted(by_stream.items()):
            count = len(stream_events)
            before_restart = []
            after_restart = []
            
            for event in stream_events:
                locked_at_str = event.get('locked_at_utc', '')
                try:
                    locked_at = datetime.fromisoformat(locked_at_str.replace('Z', '+00:00'))
                    if locked_at < restart_time:
                        before_restart.append(event)
                    else:
                        after_restart.append(event)
                except:
                    pass
            
            if count > 1:
                print(f"[WARNING] {stream_id}: {count} total events")
                print(f"  Before restart (old code): {len(before_restart)} events")
                print(f"  After restart (new code): {len(after_restart)} events")
                
                if len(after_restart) > 1:
                    print(f"  [ERROR] New code wrote {len(after_restart)} events - should be 1!")
                elif len(after_restart) == 1:
                    print(f"  [OK] New code wrote exactly 1 event (correct)")
            else:
                print(f"[OK] {stream_id}: 1 event")
        
        print()
    
    # Check for errors in robot logs
    print("ERROR CHECK:")
    print("-" * 80)
    
    robot_logs = list(log_dir.glob("robot_*.jsonl"))
    if robot_logs:
        robot_logs.sort(key=lambda p: p.stat().st_mtime, reverse=True)
        recent_log = robot_logs[0]
        
        error_types = [
            'DUPLICATE_RANGE_LOCKED',
            'RANGE_LOCK_TRANSITION_FAILED',
            'RANGE_LOCK_TRANSITION_INVALID',
            'RANGE_LOCK_FAILED',
            'RANGE_LOCKED_POST_ACTIONS_FAILED'
        ]
        
        found_errors = []
        with open(recent_log, 'r', encoding='utf-8') as f:
            for line in f:
                if not line.strip():
                    continue
                try:
                    event = json.loads(line)
                    event_type = event.get('event', '')
                    for error_type in error_types:
                        if error_type in event_type:
                            found_errors.append({
                                'type': error_type,
                                'stream': event.get('data', {}).get('stream_id', 'UNKNOWN'),
                                'timestamp': event.get('ts_utc', 'UNKNOWN'),
                                'level': event.get('level', 'UNKNOWN')
                            })
                            break
                except:
                    continue
        
        if found_errors:
            print(f"[WARNING] Found {len(found_errors)} error events:")
            for err in found_errors[:10]:
                print(f"  {err['type']}: {err['stream']} at {err['timestamp']} (level: {err['level']})")
        else:
            print("[OK] No range lock errors found")
        
        print()
    
    print("=" * 80)
    print("VERIFICATION COMPLETE")
    print("=" * 80)

if __name__ == "__main__":
    analyze()
