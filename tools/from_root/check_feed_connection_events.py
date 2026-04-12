#!/usr/bin/env python3
"""
Check if connection events are in the watchdog feed file.
"""
import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

def main():
    feed_file = Path("logs/robot/frontend_feed.jsonl")
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("WATCHDOG FEED FILE - CONNECTION EVENTS CHECK")
    print("="*80)
    
    if not feed_file.exists():
        print(f"\n[ERROR] Feed file not found: {feed_file}")
        print("The watchdog event feed generator may not have run yet.")
        return
    
    # Read last 5000 lines (to catch recent events)
    print(f"\nReading feed file: {feed_file}")
    try:
        with open(feed_file, 'r', encoding='utf-8') as f:
            lines = f.readlines()
        print(f"Total lines in feed: {len(lines):,}")
    except Exception as e:
        print(f"[ERROR] Failed to read feed file: {e}")
        return
    
    # Parse events
    events = []
    for line in lines[-5000:]:  # Last 5000 lines
        try:
            e = json.loads(line.strip())
            events.append(e)
        except:
            pass
    
    print(f"Parsed {len(events)} events from last 5000 lines\n")
    
    # Check for connection events
    connection_types = [
        'CONNECTION_LOST',
        'CONNECTION_LOST_SUSTAINED',
        'CONNECTION_RECOVERED',
        'CONNECTION_RECOVERED_NOTIFICATION',
    ]
    
    print("="*80)
    print("CONNECTION EVENTS IN FEED:")
    print("="*80)
    
    for event_type in connection_types:
        matching = [e for e in events if e.get('event_type') == event_type]
        print(f"  {event_type:35}: {len(matching):6} events")
        
        if matching:
            latest = matching[-1]
            ts_str = latest.get('timestamp_utc', '')
            if ts_str:
                try:
                    dt = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                    if dt.tzinfo is None:
                        dt = dt.replace(tzinfo=timezone.utc)
                    dt_chicago = dt.astimezone(chicago_tz)
                    age = (datetime.now(timezone.utc) - dt).total_seconds()
                    print(f"    Latest: {dt_chicago.strftime('%Y-%m-%d %H:%M:%S')} CT ({age:.1f}s ago)")
                except:
                    print(f"    Latest: {ts_str[:19]}")
    
    # Check if CONNECTION_RECOVERED_NOTIFICATION is in LIVE_CRITICAL_EVENT_TYPES
    print("\n" + "="*80)
    print("CONFIGURATION CHECK:")
    print("="*80)
    
    try:
        import sys
        sys.path.insert(0, str(Path.cwd()))
        from modules.watchdog.config import LIVE_CRITICAL_EVENT_TYPES
        
        has_recovered_notif = "CONNECTION_RECOVERED_NOTIFICATION" in LIVE_CRITICAL_EVENT_TYPES
        print(f"  CONNECTION_RECOVERED_NOTIFICATION in LIVE_CRITICAL_EVENT_TYPES: {has_recovered_notif}")
        
        if not has_recovered_notif:
            print("  [WARN] CONNECTION_RECOVERED_NOTIFICATION not in config!")
            print("         The event feed will filter it out.")
        else:
            print("  [OK] CONNECTION_RECOVERED_NOTIFICATION is configured")
    except Exception as e:
        print(f"  [ERROR] Could not check config: {e}")
    
    # Show recent connection events
    all_conn = [e for e in events if any(et in e.get('event_type', '') for et in ['CONNECTION'])]
    print(f"\n" + "="*80)
    print(f"RECENT CONNECTION EVENTS IN FEED (last 10):")
    print("="*80)
    
    if all_conn:
        for e in all_conn[-10:]:
            ts_str = e.get('timestamp_utc', '')
            event_type = e.get('event_type', 'UNKNOWN')
            try:
                dt = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                if dt.tzinfo is None:
                    dt = dt.replace(tzinfo=timezone.utc)
                dt_chicago = dt.astimezone(chicago_tz)
                print(f"  {dt_chicago.strftime('%H:%M:%S')} CT | {event_type}")
            except:
                print(f"  {ts_str[:19]} | {event_type}")
    else:
        print("  No connection events found in feed")
        print("\n  [INFO] This could mean:")
        print("    - Connection events haven't occurred recently")
        print("    - Events are filtered out (not in LIVE_CRITICAL_EVENT_TYPES)")
        print("    - Feed file needs to be regenerated")

if __name__ == "__main__":
    main()
