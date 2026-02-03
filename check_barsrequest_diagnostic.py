#!/usr/bin/env python3
"""
Comprehensive diagnostic for BarsRequest "found nothing" issue.
Checks logs, stream status, and identifies root cause.
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

def parse_timestamp(ts_str):
    """Parse ISO timestamp string."""
    try:
        ts_str = ts_str.replace('Z', '+00:00')
        return datetime.fromisoformat(ts_str)
    except:
        return None

def check_log_file(log_path):
    """Check a log file for events."""
    if not log_path.exists():
        return []
    
    events = []
    
    try:
        with open(log_path, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                
                try:
                    event = json.loads(line)
                    events.append(event)
                except json.JSONDecodeError:
                    continue
    
    except Exception as e:
        print(f"Error reading {log_path}: {e}", file=sys.stderr)
        return []
    
    return events

def main():
    log_dir = Path("logs/robot")
    
    print("=" * 80)
    print("BARSREQUEST DIAGNOSTIC - 'Found Nothing' Issue")
    print("=" * 80)
    print()
    
    # Collect all events
    all_events = []
    
    # Check frontend feed
    frontend_feed = log_dir / "frontend_feed.jsonl"
    if frontend_feed.exists():
        print(f"Reading: {frontend_feed.name}")
        events = check_log_file(frontend_feed)
        all_events.extend(events)
    
    # Check robot logs
    robot_logs = list(log_dir.glob("robot_*.jsonl"))
    for log_file in sorted(robot_logs, key=lambda p: p.stat().st_mtime, reverse=True)[:5]:
        print(f"Reading: {log_file.name}")
        events = check_log_file(log_file)
        all_events.extend(events)
    
    if not all_events:
        print("\nNo events found in logs.")
        return
    
    # Sort by timestamp
    all_events.sort(key=lambda e: parse_timestamp(e.get('timestamp_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    print(f"\nTotal events: {len(all_events)}")
    print()
    
    # Filter relevant events
    barsrequest_events = [e for e in all_events if 'BARSREQUEST' in e.get('event_type', '')]
    stream_events = [e for e in all_events if e.get('event_type', '') in ['STREAM_CREATED', 'STREAM_ARMED', 'STREAM_SKIPPED', 'STREAMS_CREATED']]
    engine_events = [e for e in all_events if e.get('event_type', '') in ['ENGINE_START', 'ENGINE_STOP', 'TIMETABLE_LOADED']]
    
    print("=" * 80)
    print("ENGINE STATUS")
    print("=" * 80)
    print()
    
    recent_engine = [e for e in engine_events[-10:]]
    if recent_engine:
        for event in recent_engine:
            ts = event.get('timestamp_utc', 'Unknown')
            event_type = event.get('event_type', 'Unknown')
            print(f"{ts} | {event_type}")
    else:
        print("No recent ENGINE_START/STOP events found")
    
    print()
    print("=" * 80)
    print("STREAM STATUS")
    print("=" * 80)
    print()
    
    recent_streams = stream_events[-20:]
    if recent_streams:
        for event in recent_streams:
            ts = event.get('timestamp_utc', 'Unknown')
            event_type = event.get('event_type', 'Unknown')
            stream = event.get('stream', 'N/A')
            instrument = event.get('instrument', 'N/A')
            
            payload = event.get('payload', {})
            if isinstance(payload, str):
                try:
                    payload = json.loads(payload)
                except:
                    payload = {}
            
            reason = payload.get('reason', '')
            committed = payload.get('committed', 'N/A')
            
            print(f"{ts} | {event_type} | {stream} | {instrument}")
            if reason:
                print(f"  Reason: {reason}")
            if committed != 'N/A':
                print(f"  Committed: {committed}")
            print()
    else:
        print("No stream creation events found")
        print("\n[ISSUE] Streams are not being created!")
        print("  Possible causes:")
        print("    1. Timetable has no enabled streams")
        print("    2. All streams are committed (from previous day)")
        print("    3. Engine.Start() failed silently")
    
    print()
    print("=" * 80)
    print("BARSREQUEST EVENTS")
    print("=" * 80)
    print()
    
    if barsrequest_events:
        print(f"Found {len(barsrequest_events)} BarsRequest events")
        print()
        
        # Group by type
        by_type = defaultdict(list)
        for event in barsrequest_events:
            event_type = event.get('event_type', 'UNKNOWN')
            by_type[event_type].append(event)
        
        for event_type, events in sorted(by_type.items()):
            print(f"{event_type}: {len(events)} event(s)")
        
        print()
        print("Recent BarsRequest events:")
        print()
        
        for event in barsrequest_events[-10:]:
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
            
            # Show key diagnostic fields
            if 'bars_returned' in payload:
                bars = payload.get('bars_returned', 0)
                print(f"  Bars returned: {bars}")
                if bars == 0:
                    print(f"  [ISSUE] Zero bars returned!")
            
            if 'result' in payload:
                result = payload.get('result', '')
                print(f"  Result: {result}")
                if result == 'ALL_STREAMS_COMMITTED':
                    print(f"  [ISSUE] All streams are committed - BarsRequest skipped")
                elif result == 'NO_STREAMS_FOUND':
                    print(f"  [ISSUE] No streams found for instrument")
            
            if 'reason' in payload:
                reason = payload.get('reason', '')
                print(f"  Reason: {reason}")
            
            if 'error' in payload:
                error = payload.get('error', '')
                print(f"  Error: {error}")
            
            if 'note' in payload:
                note = payload.get('note', '')
                print(f"  Note: {note}")
            
            if 'range_start_chicago' in payload:
                range_start = payload.get('range_start_chicago', '')
                end_time = payload.get('end_time', '') or payload.get('request_end_chicago', '')
                print(f"  Time range: {range_start} to {end_time}")
            
            if 'possible_causes' in payload:
                causes = payload.get('possible_causes', [])
                print(f"  Possible causes: {', '.join(causes)}")
            
            print()
    else:
        print("No BarsRequest events found in logs!")
        print()
        print("[ISSUE] BarsRequest is not being called or not logging events")
        print()
        print("Possible reasons:")
        print("  1. BarsRequest is being skipped (check for BARSREQUEST_SKIPPED events)")
        print("  2. No enabled streams exist (check STREAM_SKIPPED events)")
        print("  3. All streams are committed (check STREAM_SKIPPED with reason=ALREADY_COMMITTED_JOURNAL)")
        print("  4. GetBarsRequestTimeRange() returns null")
        print("  5. Robot is not running")
    
    print()
    print("=" * 80)
    print("DIAGNOSIS SUMMARY")
    print("=" * 80)
    print()
    
    # Check for zero bars
    zero_bar_events = [e for e in barsrequest_events 
                      if e.get('payload', {}).get('bars_returned') == 0 
                      if isinstance(e.get('payload'), dict)]
    
    skipped_events = [e for e in barsrequest_events if 'SKIPPED' in e.get('event_type', '')]
    
    if zero_bar_events:
        print(f"[ISSUE] Found {len(zero_bar_events)} events where bars_returned = 0")
        print()
        print("This means BarsRequest executed but NinjaTrader returned no bars.")
        print("Possible causes:")
        print("  1. Strategy started after slot_time (bars already passed)")
        print("  2. NinjaTrader 'Days to load' setting too low")
        print("  3. No historical data available for this date")
        print("  4. Bars are being filtered out (outside requested range)")
    
    if skipped_events:
        print(f"\n[ISSUE] Found {len(skipped_events)} BarsRequest skipped events")
        print()
        print("BarsRequest is being skipped before execution.")
        print("Check the 'reason' field in skipped events above.")
    
    if not barsrequest_events and not skipped_events:
        print("[ISSUE] No BarsRequest events found at all")
        print()
        print("BarsRequest is not being called. Check:")
        print("  1. Are streams being created? (check STREAM_CREATED events)")
        print("  2. Are streams committed? (check STREAM_SKIPPED with reason=ALREADY_COMMITTED_JOURNAL)")
        print("  3. Is GetBarsRequestTimeRange() returning null?")
        print("  4. Is RequestHistoricalBarsForPreHydration() being called?")

if __name__ == "__main__":
    main()
