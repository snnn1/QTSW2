import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING TIME AWARENESS AND CURRENT BARS")
print("=" * 80)
print()

# Get current time
now_utc = datetime.now(timezone.utc)
print(f"Current UTC time: {now_utc}")
print(f"Current Chicago time: {now_utc.astimezone(timezone(timedelta(hours=-6)))}")
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Get last 200 lines
recent_lines = lines[-200:] if len(lines) > 200 else lines

print(f"Checking last {len(recent_lines)} log lines...")
print()

# Find most recent events
events = []
for line in recent_lines:
    try:
        entry = json.loads(line)
        ts_str = entry.get('ts_utc', '')
        if ts_str:
            try:
                ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                events.append({
                    'timestamp': ts,
                    'timestamp_str': ts_str,
                    'event': entry.get('event', ''),
                    'payload': entry.get('data', {}).get('payload', {})
                })
            except:
                pass
    except:
        pass

if events:
    # Sort by timestamp
    events.sort(key=lambda x: x['timestamp'])
    
    print("=" * 80)
    print("MOST RECENT 30 EVENTS:")
    print("=" * 80)
    
    for i, evt in enumerate(events[-30:], 1):
        ts_str = evt['timestamp_str'][:19]
        event_type = evt['event']
        age_seconds = (now_utc - evt['timestamp']).total_seconds()
        age_str = f"{age_seconds:.0f}s ago" if age_seconds < 60 else f"{age_seconds/60:.1f}m ago"
        
        bar_info = ""
        payload = evt['payload']
        if 'bar_timestamp_chicago' in payload:
            bar_time = payload['bar_timestamp_chicago']
            if isinstance(bar_time, str):
                bar_info = f" | Bar: {bar_time[:19]}"
        
        print(f"{i:2}. [{ts_str}] ({age_str:8}) {event_type:30}{bar_info}")

# Check for bar events specifically
print(f"\n{'=' * 80}")
print("BAR EVENTS IN LAST 5 MINUTES:")
print("=" * 80)

bar_events = []
for evt in events:
    if 'BAR' in evt['event']:
        age_seconds = (now_utc - evt['timestamp']).total_seconds()
        if age_seconds < 300:  # Last 5 minutes
            bar_events.append(evt)

if bar_events:
    print(f"Found {len(bar_events)} bar events in last 5 minutes")
    print()
    for i, evt in enumerate(bar_events[-20:], 1):
        ts_str = evt['timestamp_str'][:19]
        event_type = evt['event']
        payload = evt['payload']
        
        bar_chicago = payload.get('bar_timestamp_chicago', '')
        bar_date = payload.get('bar_trading_date', '')
        
        print(f"{i}. [{ts_str}] {event_type}")
        if bar_chicago:
            print(f"   Bar Chicago: {bar_chicago[:19]}")
        if bar_date:
            print(f"   Bar Date: {bar_date}")
        print()
else:
    print("No bar events found in last 5 minutes")
    print("\nThis suggests bars are not arriving or being processed")

# Check log file age
import os
mtime = os.path.getmtime(log_file)
last_modified = datetime.fromtimestamp(mtime, tz=timezone.utc)
age_seconds = (now_utc - last_modified).total_seconds()

print(f"\n{'=' * 80}")
print(f"LOG FILE STATUS:")
print("=" * 80)
print(f"Last modified: {last_modified}")
print(f"Age: {age_seconds:.0f} seconds ({age_seconds/60:.1f} minutes)")

if age_seconds > 60:
    print("\nWARNING: Log file hasn't been updated recently!")
    print("This could mean:")
    print("  1. NinjaTrader is not running or paused")
    print("  2. Strategy is not receiving bars")
    print("  3. Logging service has stopped")

# Check for any tick/heartbeat events
print(f"\n{'=' * 80}")
print("RECENT TICK/HEARTBEAT EVENTS:")
print("=" * 80)

tick_events = [e for e in events if 'TICK' in e['event'] or 'HEARTBEAT' in e['event']]
if tick_events:
    print(f"Found {len(tick_events)} tick/heartbeat events")
    for evt in tick_events[-10:]:
        ts_str = evt['timestamp_str'][:19]
        age_seconds = (now_utc - evt['timestamp']).total_seconds()
        print(f"  [{ts_str}] ({age_seconds:.0f}s ago) {evt['event']}")
else:
    print("No tick/heartbeat events found")
    print("This suggests the Tick() method is not being called")
