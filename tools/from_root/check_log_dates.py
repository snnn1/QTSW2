#!/usr/bin/env python3
"""Quick check of trading dates in logs."""

import json
from pathlib import Path
from collections import Counter

logs_dir = Path("logs/robot")
engine_log = logs_dir / "robot_ENGINE.jsonl"

if not engine_log.exists():
    print(f"Log file not found: {engine_log}")
    exit(1)

events = []
with open(engine_log, 'r', encoding='utf-8') as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
            events.append(event)
        except:
            pass

dates = Counter(e.get('trading_date', 'NO_DATE') for e in events)
print('Trading dates in logs:')
for date, count in sorted(dates.items()):
    print(f'  {date}: {count} events')

# Check for BAR events
bar_events = [e for e in events if e.get('event') in ['BAR_ACCEPTED', 'BAR_DATE_MISMATCH', 'BAR_PARTIAL_REJECTED']]
print(f'\nTotal bar events: {len(bar_events)}')
if bar_events:
    bar_dates = Counter(e.get('trading_date', 'NO_DATE') for e in bar_events)
    print('Bar events by date:')
    for date, count in sorted(bar_dates.items()):
        print(f'  {date}: {count} events')
