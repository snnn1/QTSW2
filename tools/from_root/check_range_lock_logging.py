#!/usr/bin/env python3
"""Check range lock logging to verify single authoritative lock implementation."""

import json
from pathlib import Path
from collections import defaultdict
from datetime import datetime

def analyze_range_locks():
    """Analyze RANGE_LOCKED events to check for duplicates and verify implementation."""
    
    log_dir = Path("logs/robot")
    if not log_dir.exists():
        log_dir = Path("automation/logs")
    
    today = datetime.now().strftime("%Y-%m-%d")
    
    # Check hydration log
    hydration_file = log_dir / f"hydration_{today}.jsonl"
    ranges_file = log_dir / f"ranges_{today}.jsonl"
    
    print("=" * 80)
    print("RANGE LOCK LOGGING ANALYSIS")
    print("=" * 80)
    print()
    
    # Analyze hydration log
    if hydration_file.exists():
        print(f"Analyzing hydration log: {hydration_file.name}")
        print("-" * 80)
        
        hydration_events = []
        with open(hydration_file, 'r', encoding='utf-8') as f:
            for line in f:
                if not line.strip():
                    continue
                try:
                    event = json.loads(line)
                    if event.get('event_type') == 'RANGE_LOCKED':
                        hydration_events.append(event)
                except:
                    continue
        
        # Group by stream
        by_stream = defaultdict(list)
        for event in hydration_events:
            stream_id = event.get('stream_id', 'UNKNOWN')
            by_stream[stream_id].append(event)
        
        print(f"Found {len(hydration_events)} RANGE_LOCKED events in hydration log")
        print()
        
        duplicates_found = False
        for stream_id, events in sorted(by_stream.items()):
            count = len(events)
            if count > 1:
                duplicates_found = True
                print(f"[WARNING] DUPLICATE DETECTED: {stream_id} has {count} RANGE_LOCKED events!")
                for i, event in enumerate(events, 1):
                    locked_at = event.get('timestamp_utc', 'UNKNOWN')
                    range_high = event.get('data', {}).get('range_high', 'N/A')
                    range_low = event.get('data', {}).get('range_low', 'N/A')
                    print(f"   Event {i}: locked_at={locked_at}, range=[{range_low}, {range_high}]")
            else:
                event = events[0]
                locked_at = event.get('timestamp_utc', 'UNKNOWN')
                range_high = event.get('data', {}).get('range_high', 'N/A')
                range_low = event.get('data', {}).get('range_low', 'N/A')
                breakout_missing = event.get('data', {}).get('breakout_levels_missing', False)
                status = "[OK]" if not breakout_missing else "[WARNING] (breakout levels missing)"
                print(f"  {status} {stream_id}: 1 event, locked_at={locked_at}, range=[{range_low}, {range_high}]")
        
        if not duplicates_found:
            print()
            print("[OK] No duplicate RANGE_LOCKED events found in hydration log")
        
        print()
    
    # Analyze ranges log
    if ranges_file.exists():
        print(f"Analyzing ranges log: {ranges_file.name}")
        print("-" * 80)
        
        range_events = []
        with open(ranges_file, 'r', encoding='utf-8') as f:
            for line in f:
                if not line.strip():
                    continue
                try:
                    event = json.loads(line)
                    if event.get('event_type') == 'RANGE_LOCKED':
                        range_events.append(event)
                except:
                    continue
        
        # Group by stream
        by_stream = defaultdict(list)
        for event in range_events:
            stream_id = event.get('stream_id', 'UNKNOWN')
            by_stream[stream_id].append(event)
        
        print(f"Found {len(range_events)} RANGE_LOCKED events in ranges log")
        print()
        
        duplicates_found = False
        for stream_id, events in sorted(by_stream.items()):
            count = len(events)
            if count > 1:
                duplicates_found = True
                print(f"[WARNING] DUPLICATE DETECTED: {stream_id} has {count} RANGE_LOCKED events!")
                for i, event in enumerate(events, 1):
                    locked_at = event.get('locked_at_utc', 'UNKNOWN')
                    range_high = event.get('range_high', 'N/A')
                    range_low = event.get('range_low', 'N/A')
                    print(f"   Event {i}: locked_at={locked_at}, range=[{range_low}, {range_high}]")
            else:
                event = events[0]
                locked_at = event.get('locked_at_utc', 'UNKNOWN')
                range_high = event.get('range_high', 'N/A')
                range_low = event.get('range_low', 'N/A')
                print(f"  [OK] {stream_id}: 1 event, locked_at={locked_at}, range=[{range_low}, {range_high}]")
        
        if not duplicates_found:
            print()
            print("[OK] No duplicate RANGE_LOCKED events found in ranges log")
        
        print()
    
    # Check robot logs for errors
    print("Checking robot logs for errors/warnings")
    print("-" * 80)
    
    robot_logs = list(log_dir.glob("robot_*.jsonl"))
    if robot_logs:
        # Get most recent
        robot_logs.sort(key=lambda p: p.stat().st_mtime, reverse=True)
        recent_log = robot_logs[0]
        
        print(f"Analyzing: {recent_log.name}")
        
        errors = {
            'DUPLICATE_RANGE_LOCKED': [],
            'RANGE_LOCKED_DERIVATION_FAILED': [],
            'RANGE_LOCK_TRANSITION_FAILED': [],
            'RANGE_LOCK_TRANSITION_INVALID': [],
            'RANGE_LOCK_FAILED': [],
            'RANGE_LOCKED_POST_ACTIONS_FAILED': []
        }
        
        with open(recent_log, 'r', encoding='utf-8') as f:
            for line_num, line in enumerate(f, 1):
                if not line.strip():
                    continue
                try:
                    event = json.loads(line)
                    event_type = event.get('event', '')
                    
                    for error_type in errors.keys():
                        if error_type in event_type:
                            errors[error_type].append({
                                'line': line_num,
                                'stream': event.get('data', {}).get('stream_id', 'UNKNOWN'),
                                'timestamp': event.get('ts_utc', 'UNKNOWN'),
                                'level': event.get('level', 'UNKNOWN')
                            })
                except:
                    continue
        
        found_errors = False
        for error_type, occurrences in errors.items():
            if occurrences:
                found_errors = True
                print(f"\n[WARNING] Found {len(occurrences)} {error_type} events:")
                for occ in occurrences[:5]:  # Show first 5
                    print(f"   Line {occ['line']}: {occ['stream']} at {occ['timestamp']} (level: {occ['level']})")
                if len(occurrences) > 5:
                    print(f"   ... and {len(occurrences) - 5} more")
        
        if not found_errors:
            print("[OK] No range lock errors found in robot logs")
        
        print()
    
    print("=" * 80)
    print("ANALYSIS COMPLETE")
    print("=" * 80)

if __name__ == "__main__":
    analyze_range_locks()
