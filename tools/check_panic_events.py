#!/usr/bin/env python3
"""Check for panic events and correlate with restarts on Jan 22."""

import json
from pathlib import Path
from datetime import datetime, timezone
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
    print("PANIC EVENT ANALYSIS - JANUARY 22, 2026")
    print("=" * 80)
    print()
    
    # Collect all events from Jan 22
    jan22_events = []
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
                            jan22_events.append(event)
                    except:
                        continue
        except:
            continue
    
    print(f"Total events on 2026-01-22: {len(jan22_events)}")
    print()
    
    # Find panic-related events
    panic_keywords = ['PANIC', 'panic', 'Panic', 'FAIL_CLOSED', 'DISCONNECT_FAIL_CLOSED', 
                      'KILL_SWITCH', 'EXECUTION_GATE_INVARIANT_VIOLATION', 'CRITICAL']
    
    panic_events = []
    for event in jan22_events:
        event_type = event.get('event', '')
        message = str(event.get('message', '')).upper()
        payload = str(event.get('data', {}).get('payload', '')).upper()
        
        if any(kw in event_type.upper() or kw in message or kw in payload for kw in panic_keywords):
            panic_events.append(event)
    
    print(f"Panic-related events found: {len(panic_events)}")
    print()
    
    if panic_events:
        print("Panic events on Jan 22:")
        for e in panic_events[:20]:  # Show first 20
            ts = e.get('ts_utc', '')[:19]
            event_type = e.get('event', 'N/A')
            inst = e.get('instrument', '')
            level = e.get('level', 'N/A')
            print(f"  {ts} | {level:5} | {event_type:40} | {inst}")
        print()
    
    # Find MID_SESSION_RESTART_DETECTED events on Jan 22
    restart_events = [e for e in jan22_events if e.get('event') == 'MID_SESSION_RESTART_DETECTED']
    
    print(f"MID_SESSION_RESTART_DETECTED events on Jan 22: {len(restart_events)}")
    print()
    
    if restart_events:
        print("Restart events timeline:")
        for e in restart_events[:10]:
            ts = e.get('ts_utc', '')[:19]
            inst = e.get('instrument', '')
            data = e.get('data', {})
            payload_str = str(data.get('payload', ''))
            
            # Extract previous_state from payload
            prev_state = 'N/A'
            if 'previous_state' in payload_str:
                try:
                    parts = payload_str.split('previous_state =')
                    if len(parts) > 1:
                        prev_state = parts[1].split(',')[0].strip()
                except:
                    pass
            
            print(f"  {ts} | {inst:4} | previous_state={prev_state}")
        print()
    
    # Check for ENGINE_START events (indicates restarts)
    engine_starts = [e for e in jan22_events if e.get('event') == 'ENGINE_START']
    
    print(f"ENGINE_START events on Jan 22: {len(engine_starts)}")
    if engine_starts:
        print("\nEngine start timeline:")
        for e in engine_starts:
            ts = e.get('ts_utc', '')[:19]
            print(f"  {ts}")
        print()
    
    # Check for DISCONNECT_FAIL_CLOSED (panic mode)
    fail_closed = [e for e in jan22_events if 'FAIL_CLOSED' in e.get('event', '')]
    
    print(f"FAIL_CLOSED events on Jan 22: {len(fail_closed)}")
    if fail_closed:
        print("\nFail-closed events:")
        for e in fail_closed[:10]:
            ts = e.get('ts_utc', '')[:19]
            event_type = e.get('event', 'N/A')
            inst = e.get('instrument', '')
            print(f"  {ts} | {event_type:40} | {inst}")
        print()
    
    # Timeline correlation
    print("=" * 80)
    print("TIMELINE CORRELATION")
    print("=" * 80)
    
    if panic_events and restart_events:
        first_panic = min([parse_timestamp(e.get('ts_utc', '')) for e in panic_events if parse_timestamp(e.get('ts_utc', ''))], default=None)
        first_restart = min([parse_timestamp(e.get('ts_utc', '')) for e in restart_events if parse_timestamp(e.get('ts_utc', ''))], default=None)
        
        if first_panic and first_restart:
            print(f"\nFirst panic event: {first_panic}")
            print(f"First restart event: {first_restart}")
            
            if first_restart > first_panic:
                print("\n[ANALYSIS] Restarts occurred AFTER panic events")
                print("This suggests the panic may have triggered NinjaTrader restarts")
            elif first_restart < first_panic:
                print("\n[ANALYSIS] Restarts occurred BEFORE panic events")
                print("This suggests restarts may have caused the panic")
            else:
                print("\n[ANALYSIS] Panic and restart events occurred simultaneously")
    
    # Check for error events
    error_events = [e for e in jan22_events if e.get('level') == 'ERROR']
    print(f"\nTotal ERROR level events on Jan 22: {len(error_events)}")
    
    if error_events:
        error_types = defaultdict(int)
        for e in error_events:
            error_types[e.get('event', 'N/A')] += 1
        
        print("\nTop error types:")
        for event_type, count in sorted(error_types.items(), key=lambda x: x[1], reverse=True)[:10]:
            print(f"  {event_type}: {count}")

if __name__ == "__main__":
    main()
