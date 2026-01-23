#!/usr/bin/env python3
"""Check what happened at 15:00 UTC on Jan 22."""

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
    
    print("=" * 80)
    print("CRASH ANALYSIS - 15:00 UTC (9:00 AM CT) on JANUARY 22, 2026")
    print("=" * 80)
    print()
    
    # User says crash at 15:00 UTC (9:00 AM CT)
    target_time_start_utc = datetime(2026, 1, 22, 14, 50, 0, tzinfo=timezone.utc)   # 14:50 UTC
    target_time_end_utc = datetime(2026, 1, 22, 15, 10, 0, tzinfo=timezone.utc)      # 15:10 UTC
    
    events_in_window = []
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        ts_str = event.get('ts_utc', '')
                        if ts_str.startswith('2026-01-22T14:') or ts_str.startswith('2026-01-22T15:'):
                            ts = parse_timestamp(ts_str)
                            if ts and target_time_start_utc <= ts <= target_time_end_utc:
                                events_in_window.append(event)
                    except:
                        continue
        except:
            continue
    
    print(f"Total events in window (14:50-15:10 UTC / 8:50-9:10 AM CT): {len(events_in_window)}")
    print()
    
    # Filter for ERROR and WARN level events
    error_events = [e for e in events_in_window if e.get('level') == 'ERROR']
    warn_events = [e for e in events_in_window if e.get('level') == 'WARN']
    
    print(f"ERROR level events: {len(error_events)}")
    print(f"WARN level events: {len(warn_events)}")
    print()
    
    if error_events:
        print("ERROR events in time window:")
        error_types = Counter([e.get('event', 'N/A') for e in error_events])
        for event_type, count in sorted(error_types.items(), key=lambda x: x[1], reverse=True):
            print(f"  {event_type}: {count}")
        print()
        
        print("First 20 ERROR events:")
        for e in sorted(error_events, key=lambda x: x.get('ts_utc', ''))[:20]:
            ts = e.get('ts_utc', '')[:19]
            event_type = e.get('event', 'N/A')
            inst = e.get('instrument', '')
            data = e.get('data', {})
            payload = data.get('payload', {})
            
            # Extract error message if available
            error_msg = ''
            if isinstance(payload, dict):
                error_msg = payload.get('error', payload.get('message', ''))
            elif isinstance(payload, str):
                error_msg = payload[:80]
            
            print(f"  {ts} | {event_type:45} | {inst:4} | {str(error_msg)[:50]}")
        print()
    
    # Check for EXECUTION_GATE_INVARIANT_VIOLATION specifically
    invariant_violations = [e for e in error_events if e.get('event') == 'EXECUTION_GATE_INVARIANT_VIOLATION']
    
    if invariant_violations:
        print(f"EXECUTION_GATE_INVARIANT_VIOLATION events: {len(invariant_violations)}")
        print("\nFirst violation details:")
        for e in invariant_violations[:5]:
            ts = e.get('ts_utc', '')[:19]
            inst = e.get('instrument', '')
            data = e.get('data', {})
            payload = data.get('payload', {})
            
            if isinstance(payload, dict):
                error = payload.get('error', 'N/A')
                bar_time = payload.get('bar_timestamp_chicago', 'N/A')
                slot_time = payload.get('slot_time_chicago', 'N/A')
                can_detect = payload.get('can_detect_entries', 'N/A')
                entry_detected = payload.get('entry_detected', 'N/A')
                message = payload.get('message', 'N/A')
                
                print(f"\n  {ts} | {inst}")
                print(f"    Error: {error}")
                print(f"    Bar Time: {bar_time}")
                print(f"    Slot Time: {slot_time}")
                print(f"    Can Detect Entries: {can_detect}")
                print(f"    Entry Detected: {entry_detected}")
                print(f"    Message: {message}")
        print()
    
    # Check for DISCONNECT_FAIL_CLOSED
    fail_closed = [e for e in events_in_window if 'FAIL_CLOSED' in e.get('event', '')]
    if fail_closed:
        print(f"FAIL_CLOSED events: {len(fail_closed)}")
        for e in fail_closed:
            ts = e.get('ts_utc', '')[:19]
            event_type = e.get('event', 'N/A')
            print(f"  {ts} | {event_type}")
        print()
    
    # Check for connection issues
    connection_events = [e for e in events_in_window if 'CONNECTION' in e.get('event', '') or 'DISCONNECT' in e.get('event', '')]
    if connection_events:
        print(f"Connection-related events: {len(connection_events)}")
        conn_types = Counter([e.get('event', 'N/A') for e in connection_events])
        for event_type, count in sorted(conn_types.items()):
            print(f"  {event_type}: {count}")
        print()
    
    # Check for ENGINE_START (restarts)
    engine_starts = [e for e in events_in_window if e.get('event') == 'ENGINE_START']
    if engine_starts:
        print(f"ENGINE_START events (restarts): {len(engine_starts)}")
        for e in engine_starts:
            ts = e.get('ts_utc', '')[:19]
            print(f"  {ts}")
        print()
    
    # Timeline summary
    print("=" * 80)
    print("TIMELINE SUMMARY")
    print("=" * 80)
    
    if error_events:
        first_error = min([parse_timestamp(e.get('ts_utc', '')) for e in error_events if parse_timestamp(e.get('ts_utc', ''))], default=None)
        last_error = max([parse_timestamp(e.get('ts_utc', '')) for e in error_events if parse_timestamp(e.get('ts_utc', ''))], default=None)
        
        if first_error and last_error:
            print(f"\nFirst error: {first_error}")
            print(f"Last error: {last_error}")
            duration = (last_error - first_error).total_seconds() / 60
            print(f"Error duration: {duration:.1f} minutes")
    
    if invariant_violations:
        first_violation = min([parse_timestamp(e.get('ts_utc', '')) for e in invariant_violations if parse_timestamp(e.get('ts_utc', ''))], default=None)
        print(f"\nFirst EXECUTION_GATE_INVARIANT_VIOLATION: {first_violation}")
        print(f"Total violations in window: {len(invariant_violations)}")

if __name__ == "__main__":
    main()
