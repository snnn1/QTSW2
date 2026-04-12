#!/usr/bin/env python3
"""
Watch HeartbeatStrategy ENGINE_HEARTBEAT events in real-time

This script monitors robot_ENGINE.jsonl for new ENGINE_HEARTBEAT events
and displays them as they arrive.
"""

import json
import time
from pathlib import Path
from datetime import datetime, timezone
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")
ENGINE_LOG_FILE = Path("logs/robot/robot_ENGINE.jsonl")

def parse_timestamp(ts_str):
    """Parse ISO timestamp string to datetime."""
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

def watch_heartbeats():
    """Watch for new ENGINE_HEARTBEAT events."""
    print("=" * 80)
    print("WATCHING FOR ENGINE_HEARTBEAT EVENTS")
    print("=" * 80)
    print(f"Monitoring: {ENGINE_LOG_FILE}")
    print("Press Ctrl+C to stop")
    print()
    
    if not ENGINE_LOG_FILE.exists():
        print(f"[!] File not found: {ENGINE_LOG_FILE}")
        print(f"    Waiting for file to be created...")
        print()
    
    # Track last position
    last_position = 0
    heartbeat_count = 0
    
    try:
        while True:
            if not ENGINE_LOG_FILE.exists():
                time.sleep(1)
                continue
            
            # Read new lines
            with open(ENGINE_LOG_FILE, 'r', encoding='utf-8-sig') as f:
                f.seek(last_position)
                new_lines = f.readlines()
                last_position = f.tell()
            
            # Process new lines
            for line in new_lines:
                line = line.strip()
                if not line:
                    continue
                
                try:
                    event = json.loads(line)
                    event_type = event.get("event_type") or event.get("event", "")
                    
                    if event_type == "ENGINE_HEARTBEAT":
                        heartbeat_count += 1
                        ts_str = event.get("ts_utc") or event.get("timestamp_utc") or event.get("timestamp")
                        data = event.get("data", {})
                        instance_id = data.get("instance_id", "unknown")
                        strategy_state = data.get("strategy_state", "unknown")
                        connection_state = data.get("ninjatrader_connection_state", "unknown")
                        
                        ts = parse_timestamp(ts_str)
                        if ts:
                            chicago_time = ts.astimezone(CHICAGO_TZ)
                            now = datetime.now(timezone.utc)
                            elapsed = (now - ts).total_seconds()
                            
                            print(f"[{heartbeat_count}] ENGINE_HEARTBEAT")
                            print(f"    Time (CT): {chicago_time.strftime('%Y-%m-%d %H:%M:%S')}")
                            print(f"    Elapsed: {elapsed:.1f}s ago")
                            print(f"    Instance ID: {instance_id}")
                            print(f"    Strategy State: {strategy_state}")
                            print(f"    Connection State: {connection_state}")
                            print()
                except json.JSONDecodeError:
                    continue
                except Exception as e:
                    print(f"[!] Error processing line: {e}")
                    continue
            
            time.sleep(0.5)  # Check every 500ms
            
    except KeyboardInterrupt:
        print()
        print("=" * 80)
        print(f"Stopped. Total heartbeats seen: {heartbeat_count}")
        print("=" * 80)

if __name__ == "__main__":
    watch_heartbeats()
