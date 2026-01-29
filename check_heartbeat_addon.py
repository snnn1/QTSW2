#!/usr/bin/env python3
"""
Check for ENGINE_HEARTBEAT events from HeartbeatAddOn
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
    print("HEARTBEAT ADDON CHECK")
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
    
    print(f"Total events in ENGINE log: {len(events)}")
    
    # Find heartbeat events
    # Note: Events use "event" field (not "event_type") after conversion through RobotLoggingService
    heartbeat_events = []
    for evt in events:
        event_type = evt.get('event_type', '')
        event = evt.get('event', '')
        # Check both fields - RobotEvents.EngineBase() uses "event_type", but RobotLoggingService converts to "event"
        # Also check data.payload for nested event names
        data = evt.get('data', {})
        payload_event = ''
        if isinstance(data, dict):
            payload = data.get('payload', {})
            if isinstance(payload, dict):
                payload_event = payload.get('event', '')
        
        if (event_type == 'ENGINE_HEARTBEAT' or event == 'ENGINE_HEARTBEAT' or 
            payload_event == 'ENGINE_HEARTBEAT' or 'HEARTBEAT' in str(event).upper() or 
            'HEARTBEAT' in str(event_type).upper()):
            heartbeat_events.append(evt)
    
    print(f"\n[HEARTBEAT EVENTS] Found {len(heartbeat_events)} ENGINE_HEARTBEAT events")
    
    if not heartbeat_events:
        print("\n[X] NO HEARTBEAT EVENTS FOUND")
        print("\nPossible reasons:")
        print("  1. HeartbeatAddOn is not running")
        print("  2. HeartbeatAddOn is not enabled in NinjaTrader")
        print("  3. Logger initialization failed")
        print("  4. AddOn hasn't reached State.Active yet")
        return
    
    # Show most recent heartbeats
    recent_heartbeats = sorted([e for e in heartbeat_events if e.get('ts_utc')], 
                               key=lambda x: x.get('ts_utc', ''))[-20:]
    
    print(f"\n[RECENT HEARTBEATS] (last 20)")
    for evt in reversed(recent_heartbeats):
        ts_str = evt.get('ts_utc', '')
        ts = parse_timestamp(ts_str)
        if ts:
            chicago_time = ts.astimezone(CHICAGO_TZ)
            elapsed = (datetime.now(timezone.utc) - ts).total_seconds()
            
            data = evt.get('data', {})
            if isinstance(data, dict):
                instance_id = data.get('instance_id', 'N/A')
                addon_state = data.get('addon_state', 'N/A')
                connection_state = data.get('ninjatrader_connection_state', 'N/A')
            else:
                instance_id = 'N/A'
                addon_state = 'N/A'
                connection_state = 'N/A'
            
            print(f"\n  {chicago_time.strftime('%Y-%m-%d %H:%M:%S')} CT ({elapsed:.0f}s ago)")
            print(f"    Instance ID: {instance_id}")
            print(f"    AddOn State: {addon_state}")
            print(f"    Connection State: {connection_state}")
    
    # Check heartbeat frequency
    if len(recent_heartbeats) >= 2:
        first_ts = parse_timestamp(recent_heartbeats[0].get('ts_utc', ''))
        last_ts = parse_timestamp(recent_heartbeats[-1].get('ts_utc', ''))
        if first_ts and last_ts:
            time_diff = (last_ts - first_ts).total_seconds()
            count = len(recent_heartbeats)
            if count > 1:
                avg_interval = time_diff / (count - 1)
                print(f"\n[HEARTBEAT FREQUENCY]")
                print(f"  Time span: {time_diff:.0f} seconds")
                print(f"  Heartbeats: {count}")
                print(f"  Average interval: {avg_interval:.1f} seconds")
                print(f"  Expected interval: 5.0 seconds")
                
                if abs(avg_interval - 5.0) < 1.0:
                    print(f"  [OK] Heartbeat frequency is correct (~5 seconds)")
                else:
                    print(f"  [WARNING] Heartbeat frequency is off (expected ~5 seconds)")

if __name__ == "__main__":
    main()
