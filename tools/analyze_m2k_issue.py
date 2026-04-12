#!/usr/bin/env python3
"""Analyze M2K issue around 18:41:02"""
import json
from pathlib import Path

intent_id = "fa1708e718e939d9"
broker_ids = ["357414288151", "357414288148", "357414288233"]

log_file = Path("logs/robot/robot_M2K.jsonl")
events = []

if log_file.exists():
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            if not line.strip():
                continue
            try:
                event = json.loads(line.strip())
                ts = event.get('ts_utc', '')
                if '18:4' in ts:
                    data = event.get('data', {})
                    # Check if relevant
                    if (intent_id in str(event) or 
                        any(bid in str(event) for bid in broker_ids) or
                        'TARGET' in str(event) or
                        'FLATTEN' in event.get('event', '').upper()):
                        events.append({
                            'ts': ts[:19],
                            'event': event.get('event', ''),
                            'data': data
                        })
            except:
                pass

events.sort(key=lambda x: x['ts'])

print("=" * 100)
print("M2K ISSUE ANALYSIS - Timeline")
print("=" * 100)
print()

for e in events:
    print(f"[{e['ts']}] {e['event']}")
    data = e['data']
    for key in ['intent_id', 'broker_order_id', 'tag', 'order_type', 'fill_price', 
                'fill_quantity', 'order_state', 'error', 'note', 'flatten_success']:
        if key in data:
            print(f"  {key}: {data[key]}")
    print()
