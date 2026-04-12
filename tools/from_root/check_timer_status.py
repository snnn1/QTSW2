"""Check timer and Tick() status"""
import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")

feed_file = Path('logs/robot/frontend_feed.jsonl')
if not feed_file.exists():
    print("Feed file not found")
    exit(1)

events = []
with open(feed_file, 'r', encoding='utf-8') as f:
    for line in f:
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except:
                continue

# Get last 500 events
recent = events[-500:] if len(events) > 500 else events

print("=" * 80)
print("TIMER & TICK() STATUS CHECK")
print("=" * 80)

# Check for ENGINE_START events
start_events = [e for e in recent if e.get('event_type') == 'ENGINE_START']
print(f"\n[INFO] Found {len(start_events)} ENGINE_START events in last {len(recent)} events")

if start_events:
    latest_start = start_events[-1]
    ts_str = latest_start.get('timestamp_utc', '')
    try:
        ts_utc = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
        if ts_utc.tzinfo is None:
            ts_utc = ts_utc.replace(tzinfo=timezone.utc)
        ts_chicago = ts_utc.astimezone(CHICAGO_TZ)
        now = datetime.now(timezone.utc)
        elapsed = (now - ts_utc).total_seconds()
        print(f"  Most recent ENGINE_START: {ts_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
        print(f"  Elapsed since start: {elapsed:.1f} seconds ({elapsed/60:.1f} minutes)")
    except Exception as e:
        print(f"  [ERROR parsing timestamp: {e}]")

# Check for ENGINE_TICK_HEARTBEAT events
heartbeat_events = [e for e in recent if e.get('event_type') == 'ENGINE_TICK_HEARTBEAT']
print(f"\n[INFO] Found {len(heartbeat_events)} ENGINE_TICK_HEARTBEAT events in last {len(recent)} events")

if heartbeat_events:
    print("\n  Last 5 heartbeats:")
    for e in heartbeat_events[-5:]:
        ts_str = e.get('timestamp_utc', '')
        try:
            ts_utc = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
            if ts_utc.tzinfo is None:
                ts_utc = ts_utc.replace(tzinfo=timezone.utc)
            ts_chicago = ts_utc.astimezone(CHICAGO_TZ)
            now = datetime.now(timezone.utc)
            elapsed = (now - ts_utc).total_seconds()
            print(f"    {ts_chicago.strftime('%H:%M:%S %Z')} (elapsed: {elapsed:.1f}s)")
        except:
            print(f"    [ERROR parsing timestamp]")
else:
    print("  [WARNING] No heartbeats found!")

# Check for any error events
error_events = [e for e in recent if 'ERROR' in e.get('event_type', '') or 'ERROR' in str(e.get('data', {})).upper()]
print(f"\n[INFO] Found {len(error_events)} error-related events in last {len(recent)} events")
if error_events:
    print("  Recent errors:")
    for e in error_events[-5:]:
        et = e.get('event_type', '')
        ts = e.get('timestamp_chicago', e.get('timestamp_utc', ''))[:19]
        print(f"    {ts} {et}")

# Check for timer-related events
timer_events = [e for e in recent if 'TIMER' in e.get('event_type', '').upper() or 'TIMER' in str(e.get('data', {})).upper()]
print(f"\n[INFO] Found {len(timer_events)} timer-related events")
if timer_events:
    print("  Timer events:")
    for e in timer_events[-5:]:
        et = e.get('event_type', '')
        ts = e.get('timestamp_chicago', e.get('timestamp_utc', ''))[:19]
        print(f"    {ts} {et}")

# Check for any events after the last ENGINE_START
if start_events:
    latest_start_ts = start_events[-1].get('timestamp_utc', '')
    try:
        latest_start_dt = datetime.fromisoformat(latest_start_ts.replace('Z', '+00:00'))
        if latest_start_dt.tzinfo is None:
            latest_start_dt = latest_start_dt.replace(tzinfo=timezone.utc)
        
        events_after_start = [e for e in recent if e.get('timestamp_utc', '') > latest_start_ts]
        print(f"\n[INFO] Found {len(events_after_start)} events after last ENGINE_START")
        if events_after_start:
            print("  Event types after start:")
            event_types = {}
            for e in events_after_start[:50]:  # First 50 events after start
                et = e.get('event_type', 'UNKNOWN')
                event_types[et] = event_types.get(et, 0) + 1
            for et, count in sorted(event_types.items(), key=lambda x: x[1], reverse=True)[:10]:
                print(f"    {et}: {count}")
    except Exception as e:
        print(f"  [ERROR parsing: {e}]")

print("\n" + "=" * 80)
