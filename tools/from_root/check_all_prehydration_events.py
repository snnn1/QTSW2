#!/usr/bin/env python3
"""Comprehensive check for ALL PRE_HYDRATION diagnostic events"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
all_events = []
log_files_checked = []

for log_file in log_dir.glob("robot_*.jsonl"):
    log_files_checked.append(log_file.name)
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        event = json.loads(line)
                        event_name = event.get('event', '')
                        if 'PRE_HYDRATION' in event_name.upper():
                            all_events.append({
                                'file': log_file.name,
                                'event': event_name,
                                'ts_utc': event.get('ts_utc', 'N/A'),
                                'stream': event.get('stream', event.get('data', {}).get('payload', {}).get('stream_id', 'N/A'))
                            })
                    except:
                        pass
    except Exception as e:
        print(f"Error reading {log_file.name}: {e}")

# Sort by timestamp
all_events.sort(key=lambda e: e['ts_utc'])

print("="*80)
print("COMPREHENSIVE PRE_HYDRATION EVENTS CHECK")
print("="*80)
print(f"\nLog files checked: {len(log_files_checked)}")
print(f"Total PRE_HYDRATION events found: {len(all_events)}")

# Group by event type
event_types = {}
for e in all_events:
    event_name = e['event']
    if event_name not in event_types:
        event_types[event_name] = []
    event_types[event_name].append(e)

print("\n=== Event Types Found ===")
for event_name, events in sorted(event_types.items()):
    print(f"  {event_name}: {len(events)} events")
    if events:
        latest = events[-1]
        ts = latest['ts_utc'][:19] if isinstance(latest['ts_utc'], str) else 'N/A'
        stream = latest['stream']
        file = latest['file']
        print(f"    Latest: {ts} | Stream: {stream} | File: {file}")

# Show recent events
if all_events:
    print("\n=== Recent PRE_HYDRATION Events (last 20) ===")
    for e in all_events[-20:]:
        ts = e['ts_utc'][:19] if isinstance(e['ts_utc'], str) else 'N/A'
        print(f"  {ts} | {e['file']} | {e['event']} | Stream: {e['stream']}")

# Check for specific diagnostic events we're looking for
target_events = [
    'PRE_HYDRATION_COMPLETE_SET',
    'PRE_HYDRATION_RANGE_START_DIAGNOSTIC',
    'PRE_HYDRATION_RANGE_START_DIAGNOSTIC_ERROR',
    'PRE_HYDRATION_WATCHDOG_STUCK',
    'PRE_HYDRATION_FORCED_TRANSITION',
    'PRE_HYDRATION_TIMEOUT_SKIPPED',
    'PRE_HYDRATION_HANDLER_TRACE',
    'PRE_HYDRATION_HANDLER_TRACE_ERROR'
]

print("\n=== Target Diagnostic Events Status ===")
for target in target_events:
    found = [e for e in all_events if target in e['event']]
    status = f"[FOUND: {len(found)}]" if found else "[NOT FOUND]"
    print(f"  {target}: {status}")
    if found:
        latest = found[-1]
        ts = latest['ts_utc'][:19] if isinstance(latest['ts_utc'], str) else 'N/A'
        print(f"    Latest: {ts} | Stream: {latest['stream']} | File: {latest['file']}")

print("\n" + "="*80)
