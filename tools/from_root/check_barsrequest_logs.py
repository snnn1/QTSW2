#!/usr/bin/env python3
"""
Check robot logs for BarsRequest issues - finding nothing when it should have data.
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

def parse_timestamp(ts_str):
    """Parse ISO timestamp string."""
    try:
        ts_str = ts_str.replace('Z', '+00:00')
        return datetime.fromisoformat(ts_str)
    except:
        return None

def check_log_file(log_path):
    """Check a log file for BarsRequest events."""
    if not log_path.exists():
        return None
    
    events = []
    
    try:
        with open(log_path, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                
                try:
                    event = json.loads(line)
                    event_type = event.get('event_type', '')
                    
                    if 'BARSREQUEST' in event_type:
                        events.append(event)
                except json.JSONDecodeError:
                    continue
    
    except Exception as e:
        print(f"Error reading {log_path}: {e}", file=sys.stderr)
        return None
    
    return events

def main():
    log_dir = Path("logs/robot")
    
    print("=" * 80)
    print("BARSREQUEST LOG ANALYSIS")
    print("=" * 80)
    print()
    
    # Check frontend feed
    frontend_feed = log_dir / "frontend_feed.jsonl"
    all_events = []
    
    if frontend_feed.exists():
        print(f"Checking frontend feed: {frontend_feed}")
        events = check_log_file(frontend_feed)
        if events:
            all_events.extend(events)
    
    # Check robot logs
    robot_logs = list(log_dir.glob("robot_*.jsonl"))
    for log_file in sorted(robot_logs, key=lambda p: p.stat().st_mtime, reverse=True)[:5]:
        print(f"Checking: {log_file.name}")
        events = check_log_file(log_file)
        if events:
            all_events.extend(events)
    
    if not all_events:
        print("\nNo BarsRequest events found in logs.")
        print("\nPossible reasons:")
        print("  1. Robot is not running")
        print("  2. BarsRequest is being skipped (check BARSREQUEST_SKIPPED events)")
        print("  3. All streams are committed (no active streams)")
        print("  4. Events are not being logged")
        return
    
    # Sort by timestamp
    all_events.sort(key=lambda e: parse_timestamp(e.get('timestamp_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    print(f"\nFound {len(all_events)} BarsRequest events")
    print()
    
    # Group by event type
    by_type = defaultdict(list)
    for event in all_events:
        event_type = event.get('event_type', 'UNKNOWN')
        by_type[event_type].append(event)
    
    print("=" * 80)
    print("EVENT BREAKDOWN")
    print("=" * 80)
    print()
    
    for event_type, events in sorted(by_type.items()):
        print(f"{event_type}: {len(events)} event(s)")
    
    print()
    print("=" * 80)
    print("RECENT BARSREQUEST EVENTS (Last 20)")
    print("=" * 80)
    print()
    
    for event in all_events[-20:]:
        ts = event.get('timestamp_utc', 'Unknown')
        event_type = event.get('event_type', 'Unknown')
        instrument = event.get('instrument', 'N/A')
        
        payload = event.get('payload', {})
        if isinstance(payload, str):
            try:
                payload = json.loads(payload)
            except:
                payload = {"raw": payload[:200]}
        
        print(f"{ts} | {event_type}")
        print(f"  Instrument: {instrument}")
        
        # Show key fields
        if 'bars_returned' in payload:
            bars = payload.get('bars_returned', 0)
            print(f"  Bars returned: {bars}")
            if bars == 0:
                print(f"  [WARN] Zero bars returned!")
        
        if 'error' in payload:
            error = payload.get('error', '')
            print(f"  Error: {error}")
        
        if 'reason' in payload:
            reason = payload.get('reason', '')
            print(f"  Reason: {reason}")
        
        if 'note' in payload:
            note = payload.get('note', '')
            print(f"  Note: {note}")
        
        if 'range_start_chicago' in payload:
            range_start = payload.get('range_start_chicago', '')
            end_time = payload.get('end_time', '') or payload.get('request_end_chicago', '')
            print(f"  Time range: {range_start} to {end_time}")
        
        print()
    
    # Check for zero bars
    zero_bar_events = []
    for event in all_events:
        payload = event.get('payload', {})
        if isinstance(payload, str):
            try:
                payload = json.loads(payload)
            except:
                continue
        
        bars_returned = payload.get('bars_returned', None)
        if bars_returned == 0:
            zero_bar_events.append(event)
    
    if zero_bar_events:
        print("=" * 80)
        print("ZERO BARS EVENTS")
        print("=" * 80)
        print()
        print(f"Found {len(zero_bar_events)} events where bars_returned = 0")
        print()
        
        for event in zero_bar_events[-10:]:
            ts = event.get('timestamp_utc', 'Unknown')
            event_type = event.get('event_type', 'Unknown')
            instrument = event.get('instrument', 'N/A')
            
            payload = event.get('payload', {})
            if isinstance(payload, str):
                try:
                    payload = json.loads(payload)
                except:
                    payload = {}
            
            print(f"{ts} | {event_type} | {instrument}")
            
            if 'range_start_chicago' in payload:
                print(f"  Range: {payload.get('range_start_chicago')} to {payload.get('end_time', payload.get('request_end_chicago', 'N/A'))}")
            
            if 'note' in payload:
                print(f"  Note: {payload.get('note')}")
            
            if 'possible_causes' in payload:
                causes = payload.get('possible_causes', [])
                print(f"  Possible causes: {', '.join(causes)}")
            
            print()
    
    # Check for skipped events
    skipped_events = [e for e in all_events if 'SKIPPED' in e.get('event_type', '')]
    if skipped_events:
        print("=" * 80)
        print("BARSREQUEST SKIPPED EVENTS")
        print("=" * 80)
        print()
        print(f"Found {len(skipped_events)} skipped events")
        print()
        
        for event in skipped_events[-10:]:
            ts = event.get('timestamp_utc', 'Unknown')
            instrument = event.get('instrument', 'N/A')
            
            payload = event.get('payload', {})
            if isinstance(payload, str):
                try:
                    payload = json.loads(payload)
                except:
                    payload = {}
            
            reason = payload.get('reason', 'Unknown')
            note = payload.get('note', '')
            
            print(f"{ts} | {instrument}")
            print(f"  Reason: {reason}")
            if note:
                print(f"  Note: {note}")
            print()

if __name__ == "__main__":
    main()
