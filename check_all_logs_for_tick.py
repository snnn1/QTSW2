#!/usr/bin/env python3
"""Check all log files for TICK diagnostic events"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
if not log_dir.exists():
    print("Log directory not found")
    exit(1)

all_tick_events = []
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
                        if 'TICK' in event_name.upper() or 'METHOD_ENTERED' in event_name.upper():
                            all_tick_events.append({
                                'file': log_file.name,
                                'event': event_name,
                                'ts_utc': event.get('ts_utc', 'N/A'),
                                'stream': event.get('stream', event.get('data', {}).get('payload', {}).get('stream_id', 'N/A'))
                            })
                    except:
                        pass
    except Exception as e:
        print(f"Error reading {log_file.name}: {e}")

print("="*80)
print("TICK DIAGNOSTIC EVENTS SEARCH")
print("="*80)
print(f"\nLog files checked: {len(log_files_checked)}")
for f in log_files_checked:
    print(f"  - {f}")

print(f"\nTotal TICK-related events found: {len(all_tick_events)}")

if all_tick_events:
    print("\n=== TICK EVENTS (last 20) ===")
    for e in all_tick_events[-20:]:
        ts = e['ts_utc'][:19] if isinstance(e['ts_utc'], str) else 'N/A'
        print(f"  {ts} | {e['file']} | {e['event']} | Stream: {e['stream']}")
else:
    print("\n[WARN] No TICK-related events found in any log file!")
    print("  -> Code may not be compiled")
    print("  -> OR Tick() is not being called")
    print("  -> OR logging is failing silently")

print("\n" + "="*80)
