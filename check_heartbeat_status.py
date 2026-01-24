"""
Diagnostic script to check heartbeat status
"""
import json
import sys
from pathlib import Path
from datetime import datetime, timezone
import pytz

# Add project root to path
QTSW2_ROOT = Path(__file__).parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.watchdog.config import FRONTEND_FEED_FILE, ENGINE_TICK_STALL_THRESHOLD_SECONDS
from modules.watchdog.state_manager import WatchdogStateManager

CHICAGO_TZ = pytz.timezone("America/Chicago")

def check_heartbeat_status():
    """Check if heartbeats are being received and processed."""
    print("=" * 80)
    print("HEARTBEAT STATUS DIAGNOSTIC")
    print("=" * 80)
    
    # Check if feed file exists
    if not FRONTEND_FEED_FILE.exists():
        print(f"[ERROR] Feed file not found: {FRONTEND_FEED_FILE}")
        return
    
    print(f"\n[INFO] Feed file: {FRONTEND_FEED_FILE}")
    print(f"[INFO] Stall threshold: {ENGINE_TICK_STALL_THRESHOLD_SECONDS} seconds")
    
    # Read recent events from feed
    print("\n[INFO] Reading recent events from feed...")
    events = []
    try:
        with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8') as f:
            lines = f.readlines()
            # Get last 100 lines
            for line in lines[-100:]:
                try:
                    event = json.loads(line.strip())
                    events.append(event)
                except json.JSONDecodeError:
                    continue
    except Exception as e:
        print(f"[ERROR] Failed to read feed file: {e}")
        return
    
    print(f"[INFO] Read {len(events)} recent events")
    
    # Find ENGINE_TICK_HEARTBEAT events
    heartbeat_events = [e for e in events if e.get("event_type") == "ENGINE_TICK_HEARTBEAT"]
    print(f"\n[INFO] Found {len(heartbeat_events)} ENGINE_TICK_HEARTBEAT events in recent feed")
    
    if heartbeat_events:
        print("\n[INFO] Recent heartbeat events:")
        for i, event in enumerate(heartbeat_events[-5:], 1):  # Show last 5
            timestamp_utc_str = event.get("timestamp_utc", "")
            try:
                timestamp_utc = datetime.fromisoformat(timestamp_utc_str.replace('Z', '+00:00'))
                if timestamp_utc.tzinfo is None:
                    timestamp_utc = timestamp_utc.replace(tzinfo=timezone.utc)
                timestamp_chicago = timestamp_utc.astimezone(CHICAGO_TZ)
                now = datetime.now(timezone.utc)
                elapsed = (now - timestamp_utc).total_seconds()
                print(f"  {i}. {timestamp_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')} "
                      f"(elapsed: {elapsed:.1f}s)")
            except Exception as e:
                print(f"  {i}. [ERROR parsing timestamp: {e}]")
        
        # Check most recent heartbeat
        latest_heartbeat = heartbeat_events[-1]
        timestamp_utc_str = latest_heartbeat.get("timestamp_utc", "")
        try:
            timestamp_utc = datetime.fromisoformat(timestamp_utc_str.replace('Z', '+00:00'))
            if timestamp_utc.tzinfo is None:
                timestamp_utc = timestamp_utc.replace(tzinfo=timezone.utc)
            now = datetime.now(timezone.utc)
            elapsed = (now - timestamp_utc).total_seconds()
            
            print(f"\n[INFO] Most recent heartbeat:")
            print(f"  Timestamp (UTC): {timestamp_utc.isoformat()}")
            print(f"  Timestamp (Chicago): {timestamp_utc.astimezone(CHICAGO_TZ).isoformat()}")
            print(f"  Elapsed since heartbeat: {elapsed:.1f} seconds")
            print(f"  Stall threshold: {ENGINE_TICK_STALL_THRESHOLD_SECONDS} seconds")
            
            if elapsed < ENGINE_TICK_STALL_THRESHOLD_SECONDS:
                print(f"  [OK] Engine should be considered ALIVE (elapsed < threshold)")
            else:
                print(f"  [WARNING] Engine should be considered STALLED (elapsed >= threshold)")
        except Exception as e:
            print(f"[ERROR] Failed to parse latest heartbeat timestamp: {e}")
    else:
        print("\n[WARNING] No ENGINE_TICK_HEARTBEAT events found in recent feed!")
        print("  This could mean:")
        print("  1. Robot is not emitting heartbeats")
        print("  2. Heartbeats are not reaching the feed")
        print("  3. Feed file is empty or corrupted")
    
    # Check state manager
    print("\n" + "=" * 80)
    print("STATE MANAGER STATUS")
    print("=" * 80)
    
    try:
        state_manager = WatchdogStateManager()
        
        # Process recent events to update state
        print("\n[INFO] Processing recent events to update state...")
        from modules.watchdog.event_processor import EventProcessor
        processor = EventProcessor(state_manager)
        
        for event in events:
            processor.process_event(event)
        
        # Check engine alive status
        engine_alive = state_manager.compute_engine_alive()
        last_tick_utc = state_manager._last_engine_tick_utc
        
        print(f"\n[INFO] State Manager Status:")
        print(f"  engine_alive: {engine_alive}")
        if last_tick_utc:
            now = datetime.now(timezone.utc)
            elapsed = (now - last_tick_utc).total_seconds()
            print(f"  last_engine_tick_utc: {last_tick_utc.isoformat()}")
            print(f"  last_engine_tick_chicago: {last_tick_utc.astimezone(CHICAGO_TZ).isoformat()}")
            print(f"  elapsed since last tick: {elapsed:.1f} seconds")
            print(f"  stall threshold: {ENGINE_TICK_STALL_THRESHOLD_SECONDS} seconds")
            
            if engine_alive:
                print(f"  [OK] Engine is ALIVE")
            else:
                print(f"  [WARNING] Engine is STALLED (elapsed >= threshold)")
        else:
            print(f"  last_engine_tick_utc: None")
            print(f"  [WARNING] No engine tick timestamp recorded")
            print(f"  [WARNING] Engine should be considered STALLED")
    except Exception as e:
        print(f"[ERROR] Failed to check state manager: {e}")
        import traceback
        traceback.print_exc()
    
    # Check for ENGINE_START events
    start_events = [e for e in events if e.get("event_type") == "ENGINE_START"]
    print(f"\n[INFO] Found {len(start_events)} ENGINE_START events in recent feed")
    if start_events:
        latest_start = start_events[-1]
        timestamp_utc_str = latest_start.get("timestamp_utc", "")
        try:
            timestamp_utc = datetime.fromisoformat(timestamp_utc_str.replace('Z', '+00:00'))
            if timestamp_utc.tzinfo is None:
                timestamp_utc = timestamp_utc.replace(tzinfo=timezone.utc)
            print(f"  Most recent ENGINE_START: {timestamp_utc.astimezone(CHICAGO_TZ).isoformat()}")
        except Exception as e:
            print(f"  [ERROR parsing timestamp: {e}]")
    
    print("\n" + "=" * 80)
    print("DIAGNOSTIC COMPLETE")
    print("=" * 80)

if __name__ == "__main__":
    check_heartbeat_status()
