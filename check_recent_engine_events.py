#!/usr/bin/env python3
"""
Check recent ENGINE log events
"""

import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")
ENGINE_LOG_FILE = Path("logs/robot/robot_ENGINE.jsonl")

def parse_timestamp(ts_str):
    """Parse ISO timestamp."""
    if not ts_str:
        return None
    try:
        ts_str = ts_str.replace('Z', '+00:00')
        dt = datetime.fromisoformat(ts_str)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except:
        return None

def main():
    print("=" * 80)
    print("RECENT ENGINE LOG EVENTS")
    print("=" * 80)
    
    if not ENGINE_LOG_FILE.exists():
        print(f"[X] File not found: {ENGINE_LOG_FILE}")
        return
    
    # Read all events
    events = []
    try:
        with open(ENGINE_LOG_FILE, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    events.append(json.loads(line))
                except:
                    continue
    except Exception as e:
        print(f"[X] Error reading file: {e}")
        return
    
    print(f"Total events: {len(events)}")
    
    # Show most recent events
    recent_events = sorted([e for e in events if e.get('ts_utc')], 
                          key=lambda x: x.get('ts_utc', ''))[-20:]
    
    print(f"\n[RECENT EVENTS] (last 20)")
    for evt in reversed(recent_events):
        ts_str = evt.get('ts_utc', '')
        ts = parse_timestamp(ts_str)
        if ts:
            chicago_time = ts.astimezone(CHICAGO_TZ)
            elapsed = (datetime.now(timezone.utc) - ts).total_seconds()
            
            event_type = evt.get('event_type', '')
            event = evt.get('event', '')
            event_name = event or event_type or 'NO_TYPE'
            
            print(f"\n  {chicago_time.strftime('%H:%M:%S')} CT ({elapsed:.0f}s ago)")
            print(f"    Event: {event_name}")
            print(f"    Source: {evt.get('source', 'N/A')}")
            print(f"    Stream: {evt.get('stream', 'N/A')}")
            
            # Check for errors
            if 'ERROR' in event_name or 'ERROR' in str(evt.get('data', {})):
                data = evt.get('data', {})
                if isinstance(data, dict):
                    error = data.get('error', data.get('message', ''))
                    if error:
                        print(f"    ERROR: {error}")
    
    # Check for conversion errors
    conversion_errors = [e for e in events if 'CONVERSION_ERROR' in str(e.get('event', '')) or 'CONVERSION_ERROR' in str(e.get('event_type', ''))]
    print(f"\n[LOGGER CONVERSION ERRORS] Found {len(conversion_errors)}")
    if conversion_errors:
        for evt in conversion_errors[-5:]:
            ts_str = evt.get('ts_utc', '')
            ts = parse_timestamp(ts_str)
            if ts:
                chicago_time = ts.astimezone(CHICAGO_TZ)
                print(f"  {chicago_time.strftime('%H:%M:%S')} CT - {evt.get('event', evt.get('event_type', ''))}")
                data = evt.get('data', {})
                if isinstance(data, dict):
                    error = data.get('error', data.get('message', ''))
                    if error:
                        print(f"    Error: {error}")

if __name__ == "__main__":
    main()
