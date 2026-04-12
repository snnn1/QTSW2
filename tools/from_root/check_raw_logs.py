"""Check raw robot logs for timer messages"""
import json
from pathlib import Path

log_file = Path('logs/robot/robot_ENGINE.jsonl')
if not log_file.exists():
    print("Log file not found")
    exit(1)

lines = log_file.read_text().splitlines()[-200:] if log_file.exists() else []
events = []
for line in lines:
    if line.strip():
        try:
            events.append(json.loads(line))
        except:
            continue

print("=" * 80)
print("RAW ROBOT LOGS - TIMER & TICK CHECK")
print("=" * 80)

# Check for timer-related messages
timer_msgs = [e for e in events if 'timer' in str(e).lower() or 'Tick timer' in str(e)]
print(f"\n[INFO] Found {len(timer_msgs)} timer-related messages in last {len(events)} events")
if timer_msgs:
    print("\n  Timer messages:")
    for e in timer_msgs[-10:]:
        ts = e.get('ts_chicago', e.get('ts_utc', ''))[:19]
        et = e.get('event_type', '')
        msg = str(e.get('data', {}))
        print(f"    {ts} {et}")
        if msg:
            print(f"      {msg[:150]}")

# Check for ENGINE_START
start_events = [e for e in events if e.get('event_type') == 'ENGINE_START']
print(f"\n[INFO] Found {len(start_events)} ENGINE_START events")
if start_events:
    latest = start_events[-1]
    print(f"  Most recent: {latest.get('ts_chicago', '')[:19]}")

# Check for any errors or exceptions
error_msgs = [e for e in events if 'error' in str(e).lower() or 'exception' in str(e).lower() or 'failed' in str(e).lower()]
print(f"\n[INFO] Found {len(error_msgs)} error-related messages")
if error_msgs:
    print("  Recent errors:")
    for e in error_msgs[-10:]:
        ts = e.get('ts_chicago', e.get('ts_utc', ''))[:19]
        et = e.get('event_type', '')
        print(f"    {ts} {et}")

print("\n" + "=" * 80)
