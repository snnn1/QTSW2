#!/usr/bin/env python3
"""Check M2K flatten and re-entry issue around 18:41:02"""
import json
from pathlib import Path
from datetime import datetime, timezone

# Find log files
log_dirs = [
    Path("logs/robot"),
    Path("modules/robot/core/logs"),
    Path("modules/watchdog/logs"),
]

events = []
for log_dir in log_dirs:
    if not log_dir.exists():
        continue
    
    for log_file in log_dir.glob("*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line_num, line in enumerate(f, 1):
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line.strip())
                        ts_str = event.get('ts_utc', '') or event.get('timestamp_utc', '')
                        if ts_str and '18:4' in ts_str:
                            # Check if M2K related
                            instrument = event.get('instrument', '') or event.get('data', {}).get('instrument', '')
                            exec_inst = event.get('data', {}).get('execution_instrument', '')
                            event_type = event.get('event', '') or event.get('event_type', '')
                            
                            if 'M2K' in instrument.upper() or 'M2K' in exec_inst.upper() or 'M2K' in str(event):
                                events.append({
                                    'file': str(log_file),
                                    'line': line_num,
                                    'timestamp': ts_str,
                                    'event': event_type,
                                    'instrument': instrument or exec_inst,
                                    'data': event.get('data', {}),
                                    'full_event': event
                                })
                    except:
                        pass
        except:
            pass

# Sort by timestamp
events.sort(key=lambda x: x['timestamp'])

print("=" * 100)
print("M2K EVENTS AROUND 18:41:02 (Flatten Order Time)")
print("=" * 100)
print()

# Show events in time window
target_time = "2026-02-05T18:41:00"
window_start = datetime.fromisoformat(target_time.replace('Z', '+00:00'))
if window_start.tzinfo is None:
    window_start = window_start.replace(tzinfo=timezone.utc)

relevant_events = []
for e in events:
    try:
        ts = datetime.fromisoformat(e['timestamp'].replace('Z', '+00:00'))
        if ts.tzinfo is None:
            ts = ts.replace(tzinfo=timezone.utc)
        # 5 minutes before and after
        if abs((ts - window_start).total_seconds()) < 300:
            relevant_events.append((ts, e))
    except:
        pass

relevant_events.sort(key=lambda x: x[0])

print(f"Found {len(relevant_events)} events in ±5 minute window")
print()

for ts, e in relevant_events:
    print(f"[{ts.strftime('%H:%M:%S')}] {e['event']}")
    print(f"  Instrument: {e['instrument']}")
    print(f"  File: {e['file']}")
    if e['data']:
        # Show key fields
        for key in ['intent_id', 'broker_order_id', 'fill_price', 'fill_quantity', 'order_type', 
                    'stream', 'trading_date', 'error', 'note', 'action', 'flatten_success']:
            if key in e['data']:
                print(f"  {key}: {e['data'][key]}")
    print()

# Look for specific event types
print("=" * 100)
print("KEY EVENT TYPES:")
print("=" * 100)

event_types = {}
for ts, e in relevant_events:
    et = e['event']
    event_types[et] = event_types.get(et, 0) + 1

for et, count in sorted(event_types.items(), key=lambda x: x[1], reverse=True):
    print(f"  {et}: {count}")

# Look for flatten events
print()
print("=" * 100)
print("FLATTEN EVENTS:")
print("=" * 100)
flatten_events = [e for ts, e in relevant_events if 'FLATTEN' in e['event'].upper() or 'BUY TO COVER' in str(e['data']).upper()]
for ts, e in flatten_events:
    print(f"[{ts.strftime('%H:%M:%S')}] {e['event']}")
    print(json.dumps(e['data'], indent=2))
    print()

# Look for untracked fill events
print("=" * 100)
print("UNTRACKED FILL EVENTS:")
print("=" * 100)
untracked = [e for ts, e in relevant_events if 'UNTRACKED' in e['event'].upper() or 'UNKNOWN' in e['event'].upper()]
for ts, e in untracked:
    print(f"[{ts.strftime('%H:%M:%S')}] {e['event']}")
    print(json.dumps(e['data'], indent=2))
    print()

# Look for entry fill events
print("=" * 100)
print("ENTRY FILL EVENTS:")
print("=" * 100)
entry_fills = [e for ts, e in relevant_events if 'ENTRY' in e['event'].upper() and 'FILL' in e['event'].upper()]
for ts, e in entry_fills:
    print(f"[{ts.strftime('%H:%M:%S')}] {e['event']}")
    print(json.dumps(e['data'], indent=2))
    print()

# Look for protective stop fill events
print("=" * 100)
print("PROTECTIVE STOP FILL EVENTS:")
print("=" * 100)
stop_fills = [e for ts, e in relevant_events if ('STOP' in e['event'].upper() and 'FILL' in e['event'].upper()) or 
              (e['data'].get('order_type') == 'STOP' and 'FILL' in e['event'].upper())]
for ts, e in stop_fills:
    print(f"[{ts.strftime('%H:%M:%S')}] {e['event']}")
    print(json.dumps(e['data'], indent=2))
    print()
