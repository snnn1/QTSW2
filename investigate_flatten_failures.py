#!/usr/bin/env python3
"""Investigate flatten failures"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

print("=" * 100)
print("FLATTEN FAILURE INVESTIGATION")
print("=" * 100)
print()

today = datetime.now(timezone.utc)
yesterday = today - timedelta(days=1)

# Find flatten-related events
flatten_events = []
intent_registered = []

for log_file in Path("logs/robot").glob("*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                try:
                    e = json.loads(line.strip())
                    ts_str = e.get('ts_utc', '')
                    if ts_str:
                        try:
                            ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                            if ts >= yesterday:
                                event_type = e.get('event', '')
                                if 'FLATTEN' in event_type:
                                    e['_log_file'] = log_file.name
                                    flatten_events.append(e)
                                elif 'INTENT_REGISTERED' in event_type:
                                    e['_log_file'] = log_file.name
                                    intent_registered.append(e)
                        except:
                            pass
                except:
                    pass
    except:
        pass

print(f"Found {len(flatten_events)} flatten-related events")
print(f"Found {len(intent_registered)} INTENT_REGISTERED events")
print()

# Group flatten events by intent
flatten_by_intent = {}
for e in flatten_events:
    intent_id = e.get('data', {}).get('intent_id', 'UNKNOWN')
    if intent_id not in flatten_by_intent:
        flatten_by_intent[intent_id] = []
    flatten_by_intent[intent_id].append(e)

print("FLATTEN FAILURES BY INTENT:")
print()
for intent_id, events in flatten_by_intent.items():
    failed = [e for e in events if 'FAILED' in e.get('event', '')]
    if failed:
        print(f"Intent: {intent_id[:20]}...")
        print(f"  Total flatten events: {len(events)}")
        print(f"  Failed: {len(failed)}")
        
        # Show timeline
        events_sorted = sorted(events, key=lambda x: x.get('ts_utc', ''))
        for e in events_sorted[-10:]:  # Last 10
            ts = e.get('ts_utc', '')[:19]
            event = e.get('event', 'N/A')
            data = e.get('data', {})
            print(f"    [{ts}] {event}")
            if 'error' in str(data):
                print(f"      Error: {data.get('error', 'N/A')}")
            if 'instrument' in str(data):
                print(f"      Instrument: {data.get('instrument', 'N/A')}")
        print()

print("=" * 100)
print("INTENT REGISTRATION STATUS")
print("=" * 100)
print()

# Check intents registered
if intent_registered:
    print(f"Found {len(intent_registered)} INTENT_REGISTERED events")
    
    with_be = [e for e in intent_registered if e.get('data', {}).get('has_be_trigger') == True]
    without_be = [e for e in intent_registered if e.get('data', {}).get('has_be_trigger') == False]
    no_be_field = [e for e in intent_registered if 'has_be_trigger' not in str(e.get('data', {}))]
    
    print(f"  With BE Trigger: {len(with_be)}")
    print(f"  Without BE Trigger: {len(without_be)}")
    print(f"  No BE Field (old format?): {len(no_be_field)}")
    
    if without_be:
        print("\n  ⚠️  Intents WITHOUT BE Trigger:")
        for e in without_be[-10:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Intent: {data.get('intent_id', 'N/A')[:20]}...")
            print(f"      Instrument: {data.get('instrument', 'N/A')}")
            print(f"      Direction: {data.get('direction', 'N/A')}")
    
    if no_be_field:
        print("\n  ⚠️  Intents with old log format (no BE trigger field):")
        for e in no_be_field[-5:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Intent: {data.get('intent_id', 'N/A')[:20]}...")
            print(f"      Instrument: {data.get('instrument', 'N/A')}")
else:
    print("No INTENT_REGISTERED events found in last 24 hours")

print("\n" + "=" * 100)
