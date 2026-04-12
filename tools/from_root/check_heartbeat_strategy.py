#!/usr/bin/env python3
"""
Check HeartbeatStrategy ENGINE_HEARTBEAT events in robot_ENGINE.jsonl

This script checks if the HeartbeatStrategy is emitting ENGINE_HEARTBEAT events
every ~5 seconds as expected.
"""

import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")

# Paths
ROBOT_LOGS_DIR = Path("logs/robot")
ENGINE_LOG_FILE = ROBOT_LOGS_DIR / "robot_ENGINE.jsonl"

def parse_timestamp(ts_str):
    """Parse ISO timestamp string to datetime."""
    if not ts_str:
        return None
    try:
        # Handle various timestamp formats
        ts_str = ts_str.replace('Z', '+00:00')
        dt = datetime.fromisoformat(ts_str)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except Exception as e:
        return None

def check_heartbeat_events():
    """Check robot_ENGINE.jsonl for ENGINE_HEARTBEAT events."""
    print("=" * 80)
    print("HEARTBEAT STRATEGY CHECK")
    print("=" * 80)
    print(f"Checking: {ENGINE_LOG_FILE}")
    print()
    
    if not ENGINE_LOG_FILE.exists():
        print(f"[X] File not found: {ENGINE_LOG_FILE}")
        print(f"    Make sure HeartbeatStrategy is running and logging to this file.")
        return
    
    # Check file modification time
    mtime = datetime.fromtimestamp(ENGINE_LOG_FILE.stat().st_mtime, tz=timezone.utc)
    now = datetime.now(timezone.utc)
    age_seconds = (now - mtime).total_seconds()
    print(f"File last modified: {mtime.isoformat()} ({age_seconds:.1f} seconds ago)")
    print()
    
    heartbeats = []
    all_engine_events = []
    
    try:
        with open(ENGINE_LOG_FILE, 'r', encoding='utf-8-sig') as f:
            for line_num, line in enumerate(f, 1):
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    
                    # Check for ENGINE_HEARTBEAT events
                    event_type = event.get("event_type") or event.get("event", "")
                    stream = event.get("stream", "")
                    
                    # Track all engine-level events
                    if stream == "__engine__":
                        all_engine_events.append({
                            "line": line_num,
                            "event_type": event_type,
                            "timestamp": event.get("ts_utc") or event.get("timestamp_utc") or event.get("timestamp"),
                            "raw": event
                        })
                    
                    # Specifically look for ENGINE_HEARTBEAT
                    if event_type == "ENGINE_HEARTBEAT":
                        ts_str = event.get("ts_utc") or event.get("timestamp_utc") or event.get("timestamp")
                        data = event.get("data", {})
                        instance_id = data.get("instance_id", "unknown")
                        strategy_state = data.get("strategy_state", "unknown")
                        connection_state = data.get("ninjatrader_connection_state", "unknown")
                        
                        if ts_str:
                            ts = parse_timestamp(ts_str)
                            heartbeats.append({
                                "timestamp": ts,
                                "timestamp_str": ts_str,
                                "instance_id": instance_id,
                                "strategy_state": strategy_state,
                                "connection_state": connection_state,
                                "line": line_num,
                                "raw": event
                            })
                except json.JSONDecodeError as e:
                    print(f"[!] JSON decode error at line {line_num}: {e}")
                    continue
                except Exception as e:
                    print(f"[!] Error processing line {line_num}: {e}")
                    continue
    except Exception as e:
        print(f"[X] Error reading file: {e}")
        return
    
    # Report findings
    print(f"[ENGINE EVENTS SUMMARY]")
    print(f"  Total engine-level events: {len(all_engine_events)}")
    
    if all_engine_events:
        event_types = {}
        for evt in all_engine_events:
            et = evt["event_type"]
            event_types[et] = event_types.get(et, 0) + 1
        print(f"  Event types found:")
        for et, count in sorted(event_types.items()):
            print(f"    {et}: {count}")
    
    print()
    print(f"[ENGINE_HEARTBEAT EVENTS]")
    print(f"  Found {len(heartbeats)} ENGINE_HEARTBEAT events")
    
    if not heartbeats:
        print()
        print("[X] No ENGINE_HEARTBEAT events found!")
        print()
        print("Possible reasons:")
        print("  1. HeartbeatStrategy is not running")
        print("  2. HeartbeatStrategy failed to initialize RobotLogger")
        print("  3. Strategy is not in Realtime state")
        print("  4. Timer callback is failing silently")
        print()
        print("Check NinjaTrader Output window for errors.")
        return
    
    # Analyze heartbeat timing
    print()
    print(f"[HEARTBEAT ANALYSIS]")
    
    # Sort by timestamp
    heartbeats.sort(key=lambda x: x["timestamp"] if x["timestamp"] else datetime.min.replace(tzinfo=timezone.utc))
    
    # Show first and last few heartbeats
    print(f"  First heartbeat:")
    if heartbeats[0]["timestamp"]:
        first_ts = heartbeats[0]["timestamp"]
        first_chicago = first_ts.astimezone(CHICAGO_TZ)
        print(f"    Time (UTC): {first_ts.isoformat()}")
        print(f"    Time (CT):  {first_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
        print(f"    Instance ID: {heartbeats[0]['instance_id']}")
        print(f"    Strategy State: {heartbeats[0]['strategy_state']}")
        print(f"    Connection State: {heartbeats[0]['connection_state']}")
    
    print()
    print(f"  Latest heartbeat:")
    latest = heartbeats[-1]
    if latest["timestamp"]:
        latest_ts = latest["timestamp"]
        latest_chicago = latest_ts.astimezone(CHICAGO_TZ)
        now = datetime.now(timezone.utc)
        elapsed = (now - latest_ts).total_seconds()
        
        print(f"    Time (UTC): {latest_ts.isoformat()}")
        print(f"    Time (CT):  {latest_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
        print(f"    Instance ID: {latest['instance_id']}")
        print(f"    Strategy State: {latest['strategy_state']}")
        print(f"    Connection State: {latest['connection_state']}")
        print(f"    Elapsed: {elapsed:.1f} seconds ago")
        
        if elapsed > 120:
            print(f"    [!] WARNING: Heartbeat is {elapsed:.1f}s old (>120s threshold)")
            print(f"        HeartbeatStrategy may have stopped or NinjaTrader may be frozen")
        elif elapsed > 10:
            print(f"    [!] WARNING: Heartbeat is {elapsed:.1f}s old (>10s expected interval)")
            print(f"        Timer may not be firing every 5 seconds")
        else:
            print(f"    [OK] Heartbeat is recent ({elapsed:.1f}s ago)")
    
    # Calculate intervals between heartbeats
    if len(heartbeats) > 1:
        print()
        print(f"[HEARTBEAT INTERVALS]")
        intervals = []
        for i in range(1, len(heartbeats)):
            if heartbeats[i]["timestamp"] and heartbeats[i-1]["timestamp"]:
                interval = (heartbeats[i]["timestamp"] - heartbeats[i-1]["timestamp"]).total_seconds()
                intervals.append(interval)
        
        if intervals:
            avg_interval = sum(intervals) / len(intervals)
            min_interval = min(intervals)
            max_interval = max(intervals)
            
            print(f"  Total intervals analyzed: {len(intervals)}")
            print(f"  Average interval: {avg_interval:.2f} seconds")
            print(f"  Min interval: {min_interval:.2f} seconds")
            print(f"  Max interval: {max_interval:.2f} seconds")
            
            # Check if intervals are close to 5 seconds
            if 4.0 <= avg_interval <= 6.0:
                print(f"  [OK] Intervals are close to expected 5 seconds")
            else:
                print(f"  [!] WARNING: Intervals are not close to expected 5 seconds")
                print(f"      Expected: ~5 seconds, Got: {avg_interval:.2f} seconds")
            
            # Check for gaps (missing heartbeats)
            gaps = [iv for iv in intervals if iv > 10]
            if gaps:
                print(f"  [!] WARNING: Found {len(gaps)} gaps >10 seconds (possible missing heartbeats)")
                for gap in gaps[:5]:  # Show first 5 gaps
                    print(f"      Gap: {gap:.1f} seconds")
    
    # Show recent heartbeats
    print()
    print(f"[RECENT HEARTBEATS] (last 10)")
    recent = heartbeats[-10:] if len(heartbeats) >= 10 else heartbeats
    for hb in recent:
        if hb["timestamp"]:
            elapsed = (datetime.now(timezone.utc) - hb["timestamp"]).total_seconds()
            chicago_time = hb["timestamp"].astimezone(CHICAGO_TZ)
            print(f"  {chicago_time.strftime('%H:%M:%S')} CT ({elapsed:.0f}s ago) - "
                  f"Instance: {hb['instance_id']}, State: {hb['strategy_state']}, "
                  f"Connection: {hb['connection_state']}")
    
    print()
    print("=" * 80)
    print("SUMMARY")
    print("=" * 80)
    
    if not heartbeats:
        print("[X] No ENGINE_HEARTBEAT events found - HeartbeatStrategy is not emitting events")
    elif latest["timestamp"]:
        elapsed = (datetime.now(timezone.utc) - latest["timestamp"]).total_seconds()
        if elapsed > 120:
            print(f"[X] Heartbeats found but stale ({elapsed:.1f}s old) - Strategy may have stopped")
        elif elapsed > 10:
            print(f"[!] Heartbeats found but interval is long ({elapsed:.1f}s) - Timer may not be firing correctly")
        else:
            print(f"[OK] HeartbeatStrategy is working correctly!")
            print(f"     Latest heartbeat: {elapsed:.1f}s ago")
            print(f"     Total heartbeats: {len(heartbeats)}")
            if intervals and 4.0 <= avg_interval <= 6.0:
                print(f"     Average interval: {avg_interval:.2f}s (expected ~5s)")

if __name__ == "__main__":
    check_heartbeat_events()
