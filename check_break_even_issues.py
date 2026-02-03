#!/usr/bin/env python3
"""Check break-even issues - missing BE triggers or incorrect stop modifications"""
import json
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

print("=" * 100)
print("BREAK-EVEN ISSUE INVESTIGATION")
print("=" * 100)
print()

# Check recent logs for BE-related events
instruments = ['ES', 'NQ', 'MNQ', 'MES', 'GC', 'CL', 'NG', 'YM', 'RTY']
be_events = defaultdict(list)

for instrument in instruments:
    log_file = Path(f"logs/robot/robot_{instrument}.jsonl")
    if not log_file.exists():
        continue
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            try:
                e = json.loads(line.strip())
                events.append(e)
            except:
                pass
    
    # Filter recent events (last 7 days)
    now = datetime.now(timezone.utc)
    recent_events = []
    for e in events:
        ts_str = e.get('ts_utc', '')
        if ts_str:
            try:
                ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                if (now - ts).total_seconds() < 604800:  # Last 7 days
                    recent_events.append(e)
            except:
                pass
    
    # Look for BE-related events
    for e in recent_events:
        event_type = e.get('event', '')
        if any(keyword in event_type.upper() for keyword in ['BE', 'BREAK_EVEN', 'BREAK_EVEN', 'BE_TRIGGER', 'STOP_MODIFY']):
            be_events[instrument].append(e)

print("BREAK-EVEN RELATED EVENTS:")
print()
for instrument, events in be_events.items():
    if events:
        print(f"{instrument}: {len(events)} BE-related events")
        for e in events[-5:]:  # Last 5
            ts = e.get('ts_utc', '')[:19]
            event = e.get('event', 'N/A')
            data = e.get('data', {})
            print(f"  [{ts}] {event}")
            if 'be_trigger_price' in str(data):
                print(f"    BE Trigger: {data.get('be_trigger_price', 'N/A')}")
            if 'be_stop_price' in str(data):
                print(f"    BE Stop: {data.get('be_stop_price', 'N/A')}")
            if 'entry_price' in str(data):
                print(f"    Entry: {data.get('entry_price', 'N/A')}")
            if 'error' in str(data):
                print(f"    Error: {data.get('error', 'N/A')}")
        print()

print("=" * 100)
print("CHECKING INTENT REGISTRATION WITH BE TRIGGER")
print("=" * 100)
print()

# Check for INTENT_REGISTERED events and see if they have beTrigger
for instrument in instruments:
    log_file = Path(f"logs/robot/robot_{instrument}.jsonl")
    if not log_file.exists():
        continue
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            try:
                e = json.loads(line.strip())
                if 'INTENT_REGISTERED' in str(e.get('event', '')):
                    events.append(e)
            except:
                pass
    
    if events:
        print(f"{instrument}: Found {len(events)} INTENT_REGISTERED events")
        for e in events[-3:]:  # Last 3
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            has_be_trigger = 'be_trigger' in str(data) or 'beTrigger' in str(data)
            print(f"  [{ts}] Has BE Trigger: {has_be_trigger}")
            if has_be_trigger:
                print(f"    BE Trigger: {data.get('be_trigger', data.get('beTrigger', 'N/A'))}")
            print(f"    Entry Price: {data.get('entry_price', 'N/A')}")
            print(f"    Direction: {data.get('direction', 'N/A')}")
        print()

print("=" * 100)
print("CHECKING FOR BE TRIGGER DETECTION EVENTS")
print("=" * 100)
print()

# Check for BE trigger reached events
for instrument in instruments:
    log_file = Path(f"logs/robot/robot_{instrument}.jsonl")
    if not log_file.exists():
        continue
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            try:
                e = json.loads(line.strip())
                event_type = e.get('event', '')
                if 'BE_TRIGGER' in event_type or 'BREAK_EVEN' in event_type.upper():
                    events.append(e)
            except:
                pass
    
    if events:
        print(f"{instrument}: Found {len(events)} BE trigger events")
        for e in events[-5:]:  # Last 5
            ts = e.get('ts_utc', '')[:19]
            event = e.get('event', 'N/A')
            data = e.get('data', {})
            print(f"  [{ts}] {event}")
            print(f"    BE Trigger Price: {data.get('be_trigger_price', 'N/A')}")
            print(f"    BE Stop Price: {data.get('be_stop_price', 'N/A')}")
            print(f"    Entry Price: {data.get('entry_price', 'N/A')}")
            print(f"    Tick Price: {data.get('tick_price', 'N/A')}")
            if 'error' in str(data):
                print(f"    Error: {data.get('error', 'N/A')}")
        print()
    else:
        print(f"{instrument}: No BE trigger events found")
        print()

print("=" * 100)
