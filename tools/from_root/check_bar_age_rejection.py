#!/usr/bin/env python3
"""Check if bars are being rejected due to age"""
import json
from pathlib import Path
from datetime import datetime

log_dir = Path("logs/robot")
events = []

# Read all robot log files
for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except Exception as e:
        pass

# Find BAR_PARTIAL_REJECTED_BUFFER events
bar_rejected = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        'PARTIAL_REJECTED' in e.get('event', '')):
        bar_rejected.append(e)

if bar_rejected:
    print("="*80)
    print(f"BAR_PARTIAL_REJECTED EVENTS (Found {len(bar_rejected)}):")
    print("="*80)
    for e in bar_rejected[:5]:
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_time = data.get('bar_timestamp_chicago', 'N/A')
            current_time = data.get('current_time_chicago', 'N/A')
            bar_age = data.get('bar_age_minutes', 'N/A')
            source = data.get('bar_source', 'N/A')
            print(f"  {bar_time} | Age: {bar_age} min | Source: {source} | Current: {current_time}")

# Check if bars are being added but then removed
print(f"\n{'='*80}")
print("CHECKING IF BARS ARE BEING ADDED:")
print(f"{'='*80}")

# Find any events that mention buffer count > 0
buffer_events = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2'):
        data = e.get('data', {})
        if isinstance(data, dict):
            buffer_count = data.get('bar_buffer_count', data.get('buffer_count', None))
            if buffer_count is not None and buffer_count > 0:
                buffer_events.append((e.get('ts_utc', 'N/A'), e.get('event', 'N/A'), buffer_count))

if buffer_events:
    print(f"  Found {len(buffer_events)} events with buffer_count > 0")
    for ts, event, count in buffer_events[:10]:
        print(f"    {ts[:19]} | {event} | Buffer: {count}")
else:
    print("  No events found with buffer_count > 0")

print(f"\n{'='*80}")
