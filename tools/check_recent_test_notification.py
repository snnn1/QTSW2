"""Check for recent test notification events"""
import json
from pathlib import Path
from datetime import datetime

log_dir = Path('logs/robot')
all_logs = sorted([f for f in log_dir.glob('robot_ENGINE*.jsonl')], key=lambda p: p.stat().st_mtime, reverse=True)

print(f"Found {len(all_logs)} log files")
print(f"Checking most recent: {[f.name for f in all_logs[:3]]}\n")

events = []
for f in all_logs[:2]:  # Check 2 most recent log files
    try:
        with open(f, 'r', encoding='utf-8-sig') as file:
            for line in file:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except:
        pass

# Sort by timestamp
events.sort(key=lambda e: e.get('ts_utc', ''))

# Get recent events
recent = events[-100:] if len(events) > 100 else events

# Find test and pushover events
test_events = [e for e in recent if 'TEST' in e.get('event', '').upper()]
pushover_events = [e for e in recent if 'PUSHOVER' in e.get('event', '').upper()]

print(f"Recent TEST events: {len(test_events)}")
print(f"Recent PUSHOVER events: {len(pushover_events)}\n")

if test_events:
    print("Latest TEST events:")
    for e in test_events[-5:]:
        ts = e.get('ts_utc', '')[:19]
        event = e.get('event', 'N/A')
        print(f"  {ts} | {event}")

if pushover_events:
    print("\nLatest PUSHOVER events:")
    for e in pushover_events[-5:]:
        ts = e.get('ts_utc', '')[:19]
        event = e.get('event', 'N/A')
        print(f"  {ts} | {event}")

# Check for TEST_NOTIFICATION_SENT specifically
test_sent = [e for e in recent if e.get('event') == 'TEST_NOTIFICATION_SENT']
if test_sent:
    print(f"\n[SUCCESS] Found {len(test_sent)} TEST_NOTIFICATION_SENT event(s)")
    latest = test_sent[-1]
    print(f"  Time: {latest.get('ts_utc', '')[:19]}")
    print(f"  Run ID: {latest.get('run_id', 'N/A')[:32]}...")
    data = latest.get('data', {})
    print(f"  Title: {data.get('title', 'N/A')}")
    print(f"  Message: {data.get('message', 'N/A')[:60]}...")
