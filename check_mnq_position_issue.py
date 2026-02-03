#!/usr/bin/env python3
"""Investigate MNQ position of 63 contracts - critical error analysis"""
import json
import glob
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

print("=" * 100)
print("MNQ POSITION ERROR INVESTIGATION")
print("=" * 100)
print()

# Check execution journals
journal_dir = Path("logs/robot/journal")
mnq_journals = list(journal_dir.glob("*_NQ*.json")) + list(journal_dir.glob("*_MNQ*.json"))

print(f"Found {len(mnq_journals)} NQ/MNQ journal files:")
for jf in sorted(mnq_journals):
    print(f"  - {jf.name}")

print()
print("=" * 100)
print("CHECKING RECENT MNQ EXECUTION EVENTS")
print("=" * 100)
print()

# Check MNQ log file
mnq_log = Path("logs/robot/robot_MNQ.jsonl")
if not mnq_log.exists():
    mnq_log = Path("logs/robot/robot_NQ.jsonl")

if mnq_log.exists():
    print(f"Reading events from: {mnq_log}")
    events = []
    with open(mnq_log, 'r', encoding='utf-8-sig') as f:
        for line in f:
            try:
                e = json.loads(line.strip())
                events.append(e)
            except:
                pass
    
    # Filter recent events (last 24 hours)
    now = datetime.now(timezone.utc)
    recent_events = []
    for e in events:
        ts_str = e.get('ts_utc', '')
        if ts_str:
            try:
                ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                if (now - ts).total_seconds() < 86400:  # Last 24 hours
                    recent_events.append(e)
            except:
                pass
    
    print(f"Found {len(recent_events)} events in last 24 hours")
    print()
    
    # Group by event type
    by_event_type = defaultdict(list)
    for e in recent_events:
        event_type = e.get('event', 'UNKNOWN')
        by_event_type[event_type].append(e)
    
    # Key events to check
    key_events = [
        'INTENT_REGISTERED',
        'ENTRY_SUBMITTED',
        'ENTRY_FILLED',
        'EXECUTION_FILLED',
        'PROTECTIVE_ORDERS_SUBMITTED',
        'PROTECTIVE_ORDER_FAILED',
        'POSITION_FLATTEN',
        'POSITION_FLATTEN_FAIL_CLOSED',
        'UNPROTECTED_POSITION_TIMEOUT',
        'INTENT_INCOMPLETE_UNPROTECTED_POSITION',
        'ORDER_SUBMIT',
        'ORDER_FILLED',
        'RANGE_LOCKED',
        'ENTRY_DETECTED'
    ]
    
    print("KEY EVENTS SUMMARY:")
    print()
    for event_type in key_events:
        if event_type in by_event_type:
            count = len(by_event_type[event_type])
            print(f"  {event_type}: {count} occurrences")
            if count > 0 and count <= 10:
                # Show details for small counts
                for e in by_event_type[event_type][-5:]:  # Last 5
                    ts = e.get('ts_utc', '')[:19]
                    stream = e.get('stream', 'N/A')
                    intent_id = e.get('intent_id', 'N/A')
                    quantity = e.get('quantity', e.get('fill_quantity', 'N/A'))
                    print(f"    [{ts}] Stream: {stream}, Intent: {intent_id[:20] if intent_id != 'N/A' else 'N/A'}, Qty: {quantity}")
    
    print()
    print("=" * 100)
    print("CHECKING FOR POSITION ACCUMULATION ISSUES")
    print("=" * 100)
    print()
    
    # Track intents and fills
    intent_fills = defaultdict(lambda: {'fills': [], 'total_qty': 0, 'direction': None})
    
    for e in recent_events:
        event_type = e.get('event', '')
        intent_id = e.get('intent_id', '')
        
        if 'FILLED' in event_type or 'EXECUTION_FILLED' in event_type:
            quantity = e.get('quantity', e.get('fill_quantity', 0))
            direction = e.get('direction', e.get('side', ''))
            if intent_id and quantity:
                intent_fills[intent_id]['fills'].append({
                    'ts': e.get('ts_utc', ''),
                    'qty': quantity,
                    'direction': direction,
                    'event': event_type
                })
                intent_fills[intent_id]['total_qty'] += quantity
                if direction:
                    intent_fills[intent_id]['direction'] = direction
        
        if 'INTENT_REGISTERED' in event_type:
            direction = e.get('direction', '')
            if direction:
                intent_fills[intent_id]['direction'] = direction
    
    print("INTENT FILL SUMMARY:")
    print()
    total_long = 0
    total_short = 0
    
    for intent_id, data in intent_fills.items():
        if data['total_qty'] > 0:
            direction = data['direction'] or 'UNKNOWN'
            qty = data['total_qty']
            if direction.upper() in ['LONG', 'BUY']:
                total_long += qty
            elif direction.upper() in ['SHORT', 'SELL']:
                total_short += qty
            
            print(f"  Intent: {intent_id[:40]}...")
            print(f"    Direction: {direction}")
            print(f"    Total Filled: {qty}")
            print(f"    Fill Events: {len(data['fills'])}")
            for fill in data['fills']:
                print(f"      [{fill['ts'][:19]}] {fill['event']}: {fill['qty']} ({fill['direction']})")
            print()
    
    print(f"TOTAL LONG POSITION: {total_long}")
    print(f"TOTAL SHORT POSITION: {total_short}")
    print(f"NET POSITION: {total_long - total_short}")
    print()
    
    # Check for duplicate fills or multiple intents
    print("=" * 100)
    print("CHECKING FOR DUPLICATE FILLS OR MULTIPLE INTENTS")
    print("=" * 100)
    print()
    
    # Group by stream
    by_stream = defaultdict(list)
    for intent_id, data in intent_fills.items():
        # Extract stream from intent_id if possible
        stream = 'UNKNOWN'
        for e in recent_events:
            if e.get('intent_id') == intent_id:
                stream = e.get('stream', 'UNKNOWN')
                break
        by_stream[stream].append((intent_id, data))
    
    for stream, intents in by_stream.items():
        if len(intents) > 1:
            print(f"Stream {stream} has {len(intents)} intents with fills:")
            total_qty = sum(d['total_qty'] for _, d in intents)
            print(f"  Total quantity across all intents: {total_qty}")
            for intent_id, data in intents:
                print(f"    - {intent_id[:40]}...: {data['total_qty']} contracts")
            print()

else:
    print(f"MNQ log file not found: {mnq_log}")

print()
print("=" * 100)
print("CHECKING EXECUTION JOURNALS FOR MNQ STREAMS")
print("=" * 100)
print()

for journal_file in sorted(mnq_journals):
    with open(journal_file, 'r', encoding='utf-8') as f:
        data = json.load(f)
    
    stream = journal_file.stem.split('_', 1)[1] if '_' in journal_file.stem else journal_file.stem
    print(f"\n{stream}:")
    print(f"  State: {data.get('LastState', 'N/A')}")
    print(f"  Entry Detected: {data.get('EntryDetected', False)}")
    print(f"  Last Update: {data.get('LastUpdateUtc', 'N/A')[:19]}")
    
    # Check for execution entries
    if 'ExecutionEntries' in data:
        entries = data['ExecutionEntries']
        print(f"  Execution Entries: {len(entries)}")
        for entry in entries[-5:]:  # Last 5
            print(f"    - {entry.get('EventType', 'N/A')}: {entry.get('Quantity', 'N/A')} @ {entry.get('TimestampUtc', 'N/A')[:19]}")

print()
print("=" * 100)
