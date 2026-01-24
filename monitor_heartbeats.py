"""Continuously monitor for heartbeats"""
import json
import time
from pathlib import Path
from datetime import datetime, timezone
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")
feed_file = Path('logs/robot/frontend_feed.jsonl')

def get_latest_heartbeat():
    """Get the most recent heartbeat event."""
    if not feed_file.exists():
        return None
    
    with open(feed_file, 'r', encoding='utf-8') as f:
        lines = f.readlines()
    
    for line in reversed(lines):
        if line.strip():
            try:
                event = json.loads(line.strip())
                if event.get('event_type') == 'ENGINE_TICK_HEARTBEAT':
                    return event
            except:
                continue
    return None

def get_latest_start():
    """Get the most recent ENGINE_START event."""
    if not feed_file.exists():
        return None
    
    with open(feed_file, 'r', encoding='utf-8') as f:
        lines = f.readlines()
    
    for line in reversed(lines):
        if line.strip():
            try:
                event = json.loads(line.strip())
                if event.get('event_type') == 'ENGINE_START':
                    return event
            except:
                continue
    return None

print("=" * 80)
print("HEARTBEAT MONITOR - Watching for ENGINE_TICK_HEARTBEAT events")
print("Press Ctrl+C to stop")
print("=" * 80)

last_heartbeat_seen = None
check_count = 0

try:
    while True:
        check_count += 1
        latest_heartbeat = get_latest_heartbeat()
        latest_start = get_latest_start()
        
        now = datetime.now(timezone.utc)
        
        if latest_start:
            start_ts = latest_start.get('timestamp_utc', '')
            try:
                start_dt = datetime.fromisoformat(start_ts.replace('Z', '+00:00'))
                if start_dt.tzinfo is None:
                    start_dt = start_dt.replace(tzinfo=timezone.utc)
                elapsed = (now - start_dt).total_seconds()
            except:
                elapsed = 0
        else:
            elapsed = 0
        
        if latest_heartbeat:
            hb_ts = latest_heartbeat.get('timestamp_utc', '')
            try:
                hb_dt = datetime.fromisoformat(hb_ts.replace('Z', '+00:00'))
                if hb_dt.tzinfo is None:
                    hb_dt = hb_dt.replace(tzinfo=timezone.utc)
                hb_elapsed = (now - hb_dt).total_seconds()
                hb_chicago = hb_dt.astimezone(CHICAGO_TZ)
                
                if latest_heartbeat != last_heartbeat_seen:
                    print(f"\n[{now.astimezone(CHICAGO_TZ).strftime('%H:%M:%S')}] ✅ HEARTBEAT DETECTED!")
                    print(f"  Time: {hb_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
                    print(f"  Elapsed since heartbeat: {hb_elapsed:.1f}s")
                    last_heartbeat_seen = latest_heartbeat
                else:
                    # Same heartbeat, just show elapsed time
                    if check_count % 10 == 0:  # Every 10 checks (10 seconds)
                        print(f"[{now.astimezone(CHICAGO_TZ).strftime('%H:%M:%S')}] Last heartbeat: {hb_elapsed:.1f}s ago")
            except:
                pass
        else:
            if latest_start:
                if check_count == 1 or check_count % 10 == 0:
                    print(f"\n[{now.astimezone(CHICAGO_TZ).strftime('%H:%M:%S')}] ⚠️  No heartbeats found")
                    print(f"  Engine started: {elapsed:.1f}s ago ({elapsed/60:.1f} minutes)")
                    print(f"  Status: STALLED (no heartbeats)")
            else:
                if check_count == 1 or check_count % 10 == 0:
                    print(f"\n[{now.astimezone(CHICAGO_TZ).strftime('%H:%M:%S')}] ⚠️  No engine start found")
        
        time.sleep(1)  # Check every second

except KeyboardInterrupt:
    print("\n\nMonitoring stopped.")
