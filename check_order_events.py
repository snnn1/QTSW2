#!/usr/bin/env python3
"""Check for order submission events"""
import json
from pathlib import Path

# Read latest robot events
log_dir = Path("logs/robot")
events = []
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
    except:
        pass

today_events = [e for e in events if e.get('ts_utc', '').startswith('2026-01-26')]
today_events.sort(key=lambda x: x.get('ts_utc', ''))

print("="*80)
print("ORDER SUBMISSION EVENTS CHECK")
print("="*80)

# Check for any order-related events
order_events = [e for e in today_events 
               if any(keyword in e.get('event', '') for keyword in 
                      ['ORDER', 'SUBMIT', 'ENTRY', 'BREAKOUT_DETECTED', 'EXECUTION'])]

print(f"\nTotal order-related events: {len(order_events)}")

# Group by event type
by_type = {}
for e in order_events:
    event_type = e.get('event', 'N/A')
    if event_type not in by_type:
        by_type[event_type] = []
    by_type[event_type].append(e)

print(f"\nEvent types found:")
for event_type in sorted(by_type.keys()):
    count = len(by_type[event_type])
    print(f"  {event_type}: {count}")

# Show latest BREAKOUT_DETECTED events
breakouts = [e for e in today_events if 'BREAKOUT_DETECTED' in e.get('event', '')]
if breakouts:
    print(f"\n{'='*80}")
    print("BREAKOUT_DETECTED EVENTS:")
    print("="*80)
    for b in breakouts[-10:]:
        print(f"\n  {b.get('ts_utc', '')[:19]} | {b.get('stream', 'N/A')} | {b.get('event', 'N/A')}")
        data = b.get('data', {})
        if isinstance(data, dict):
            print(f"    Direction: {data.get('direction', 'N/A')}")
            print(f"    Price: {data.get('breakout_price', 'N/A')}")
            print(f"    Entry submitted: {data.get('entry_submitted', 'N/A')}")

# Show latest ORDER_SUBMITTED events
orders = [e for e in today_events if 'ORDER_SUBMITTED' in e.get('event', '')]
if orders:
    print(f"\n{'='*80}")
    print("ORDER_SUBMITTED EVENTS:")
    print("="*80)
    for o in orders[-10:]:
        print(f"\n  {o.get('ts_utc', '')[:19]} | {o.get('stream', 'N/A')} | {o.get('event', 'N/A')}")
        data = o.get('data', {})
        if isinstance(data, dict):
            print(f"    Order type: {data.get('order_type', 'N/A')}")
            print(f"    Quantity: {data.get('quantity', 'N/A')}")
            print(f"    Price: {data.get('price', 'N/A')}")

print(f"\n{'='*80}")
