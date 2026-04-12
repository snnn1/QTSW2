"""
Diagnostic script to check heartbeat flow from robot to watchdog.

Checks:
1. Are ENGINE_TICK_HEARTBEAT events in robot_ENGINE.jsonl?
2. Are they in frontend_feed.jsonl?
3. What's the last heartbeat timestamp?
4. Is the watchdog processing them?
"""
import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")

# Paths
ROBOT_LOGS_DIR = Path("logs/robot")
FRONTEND_FEED_FILE = ROBOT_LOGS_DIR / "frontend_feed.jsonl"
ENGINE_LOG_FILE = ROBOT_LOGS_DIR / "robot_ENGINE.jsonl"

def parse_timestamp(ts_str):
    """Parse ISO timestamp."""
    try:
        dt = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except:
        return None

def check_robot_logs():
    """Check robot_ENGINE.jsonl for heartbeats."""
    print("=" * 60)
    print("1. CHECKING ROBOT LOGS (robot_ENGINE.jsonl)")
    print("=" * 60)
    
    if not ENGINE_LOG_FILE.exists():
        print(f"[X] File not found: {ENGINE_LOG_FILE}")
        return None
    
    heartbeats = []
    start_events = []
    try:
        with open(ENGINE_LOG_FILE, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    event_type = event.get("event") or event.get("event_type") or event.get("@event", "")
                    if event_type == "ENGINE_TICK_HEARTBEAT":
                        ts_str = event.get("ts_utc") or event.get("timestamp_utc") or event.get("timestamp")
                        run_id = event.get("run_id") or event.get("runId")
                        if ts_str:
                            heartbeats.append({
                                "timestamp_utc": ts_str,
                                "run_id": run_id,
                                "raw": event
                            })
                    elif event_type == "ENGINE_START":
                        ts_str = event.get("ts_utc") or event.get("timestamp_utc") or event.get("timestamp")
                        run_id = event.get("run_id") or event.get("runId")
                        if ts_str:
                            start_events.append({
                                "timestamp_utc": ts_str,
                                "run_id": run_id
                            })
                except json.JSONDecodeError:
                    continue
    except Exception as e:
        print(f"[X] Error reading file: {e}")
        return None
    
    print(f"Found {len(start_events)} ENGINE_START events")
    print(f"Found {len(heartbeats)} ENGINE_TICK_HEARTBEAT events")
    
    if start_events:
        latest_start = start_events[-1]
        ts_start = parse_timestamp(latest_start["timestamp_utc"])
        if ts_start:
            now = datetime.now(timezone.utc)
            elapsed_start = (now - ts_start).total_seconds()
            print(f"\n[INFO] Latest ENGINE_START:")
            print(f"   Timestamp (UTC): {ts_start.isoformat()}")
            print(f"   Run ID: {latest_start['run_id']}")
            print(f"   Elapsed: {elapsed_start:.1f} seconds ago")
    
    if heartbeats:
        latest = heartbeats[-1]
        ts = parse_timestamp(latest["timestamp_utc"])
        if ts:
            now = datetime.now(timezone.utc)
            elapsed = (now - ts).total_seconds()
            chicago_time = ts.astimezone(CHICAGO_TZ)
            print(f"\n[OK] Latest heartbeat:")
            print(f"   Timestamp (UTC): {ts.isoformat()}")
            print(f"   Timestamp (CT):  {chicago_time.isoformat()}")
            print(f"   Run ID: {latest['run_id']}")
            print(f"   Elapsed: {elapsed:.1f} seconds ago")
            if elapsed > 120:
                print(f"   [!] WARNING: Heartbeat is {elapsed:.1f}s old (>120s threshold)")
            return ts
        else:
            print(f"[X] Could not parse timestamp: {latest['timestamp_utc']}")
    else:
        print("[X] No heartbeats found in robot logs")
        if start_events:
            print("[!] DIAGNOSIS: Engine started but Tick() may not be running or heartbeats not being logged")
    
    return None

def check_frontend_feed():
    """Check frontend_feed.jsonl for heartbeats."""
    print("\n" + "=" * 60)
    print("2. CHECKING FRONTEND FEED (frontend_feed.jsonl)")
    print("=" * 60)
    
    if not FRONTEND_FEED_FILE.exists():
        print(f"[X] File not found: {FRONTEND_FEED_FILE}")
        return None
    
    heartbeats = []
    try:
        with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    event_type = event.get("event_type") or event.get("event")
                    if event_type == "ENGINE_TICK_HEARTBEAT":
                        ts_str = event.get("timestamp_utc")
                        run_id = event.get("run_id")
                        event_seq = event.get("event_seq", 0)
                        if ts_str:
                            heartbeats.append({
                                "timestamp_utc": ts_str,
                                "run_id": run_id,
                                "event_seq": event_seq
                            })
                except json.JSONDecodeError:
                    continue
    except Exception as e:
        print(f"[X] Error reading file: {e}")
        return None
    
    print(f"Found {len(heartbeats)} ENGINE_TICK_HEARTBEAT events")
    
    if heartbeats:
        latest = heartbeats[-1]
        ts = parse_timestamp(latest["timestamp_utc"])
        if ts:
            now = datetime.now(timezone.utc)
            elapsed = (now - ts).total_seconds()
            chicago_time = ts.astimezone(CHICAGO_TZ)
            print(f"\n[OK] Latest heartbeat:")
            print(f"   Timestamp (UTC): {ts.isoformat()}")
            print(f"   Timestamp (CT):  {chicago_time.isoformat()}")
            print(f"   Run ID: {latest['run_id']}")
            print(f"   Event Seq: {latest['event_seq']}")
            print(f"   Elapsed: {elapsed:.1f} seconds ago")
            if elapsed > 120:
                print(f"   [!] WARNING: Heartbeat is {elapsed:.1f}s old (>120s threshold)")
            return ts
        else:
            print(f"[X] Could not parse timestamp: {latest['timestamp_utc']}")
    else:
        print("[X] No heartbeats found in frontend feed")
    
    return None

def check_watchdog_cursor():
    """Check watchdog cursor state."""
    print("\n" + "=" * 60)
    print("3. CHECKING WATCHDOG CURSOR")
    print("=" * 60)
    
    cursor_file = Path("data/frontend_cursor.json")
    if not cursor_file.exists():
        print(f"[X] Cursor file not found: {cursor_file}")
        return
    
    try:
        with open(cursor_file, 'r') as f:
            cursor = json.load(f)
        print(f"[OK] Cursor state:")
        for run_id, seq in cursor.items():
            print(f"   Run ID: {run_id}, Last Seq: {seq}")
    except Exception as e:
        print(f"[X] Error reading cursor: {e}")

def main():
    print("\nHEARTBEAT FLOW DIAGNOSTIC")
    print("=" * 60)
    print(f"Current time (UTC): {datetime.now(timezone.utc).isoformat()}")
    print(f"Current time (CT):  {datetime.now(CHICAGO_TZ).isoformat()}")
    
    robot_ts = check_robot_logs()
    feed_ts = check_frontend_feed()
    check_watchdog_cursor()
    
    print("\n" + "=" * 60)
    print("SUMMARY")
    print("=" * 60)
    
    if robot_ts is None:
        print("[X] No heartbeats in robot logs - Robot may not be running or not emitting heartbeats")
    elif feed_ts is None:
        print("[!] Heartbeats in robot logs but NOT in frontend feed - EventFeedGenerator may not be processing")
    elif (datetime.now(timezone.utc) - feed_ts).total_seconds() > 120:
        print("[!] Heartbeats exist but are stale (>120s) - Robot may have stopped or watchdog not processing")
    else:
        print("[OK] Heartbeats are flowing correctly")
        print(f"   Latest heartbeat: {(datetime.now(timezone.utc) - feed_ts).total_seconds():.1f}s ago")

if __name__ == "__main__":
    main()
