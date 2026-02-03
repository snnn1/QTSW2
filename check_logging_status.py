#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check if logging fixes are working"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

today = datetime.now(timezone.utc)
recent = today - timedelta(hours=1)  # Last hour

print("=" * 100)
print("LOGGING STATUS CHECK - VERIFYING FIXES")
print("=" * 100)
print()

# Check for INTENT_REGISTERED events with BE trigger field
intent_registered = []
be_trigger_events = []
position_events = []
flatten_events = []

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
                            if ts >= recent:
                                event_type = e.get('event', '')
                                data = e.get('data', {})
                                
                                if 'INTENT_REGISTERED' in event_type:
                                    intent_registered.append(e)
                                
                                if 'BE_TRIGGER' in event_type:
                                    be_trigger_events.append(e)
                                
                                if 'POSITION' in event_type and any(k in event_type for k in ['FILL', 'EXECUTION']):
                                    position_events.append(e)
                                
                                if 'FLATTEN' in event_type:
                                    flatten_events.append(e)
                        except:
                            pass
                except:
                    pass
    except:
        pass

print("1. INTENT_REGISTERED EVENTS (BE Trigger Logging)")
print("-" * 100)
if intent_registered:
    print(f"Found {len(intent_registered)} INTENT_REGISTERED events in last hour:")
    print()
    
    with_be_field = 0
    with_be_trigger = 0
    without_be_trigger = 0
    
    for e in intent_registered:
        data = e.get('data', {})
        has_field = 'has_be_trigger' in str(data) or 'be_trigger' in str(data)
        if has_field:
            with_be_field += 1
            if data.get('has_be_trigger') == True:
                with_be_trigger += 1
            elif data.get('has_be_trigger') == False:
                without_be_trigger += 1
    
    print(f"  Events WITH be_trigger/has_be_trigger field: {with_be_field}")
    print(f"  Events WITH BE trigger set (has_be_trigger: true): {with_be_trigger}")
    print(f"  Events WITHOUT BE trigger (has_be_trigger: false): {without_be_trigger}")
    print()
    
    if with_be_field > 0:
        print("  Sample events with BE trigger field:")
        for e in intent_registered[-5:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Intent: {data.get('intent_id', 'N/A')[:30] if data.get('intent_id') else 'N/A'}...")
            print(f"      Instrument: {data.get('instrument', 'N/A')}")
            print(f"      Direction: {data.get('direction', 'N/A')}")
            if 'has_be_trigger' in str(data):
                print(f"      Has BE Trigger: {data.get('has_be_trigger', 'N/A')}")
            if 'be_trigger' in str(data):
                print(f"      BE Trigger Price: {data.get('be_trigger', 'N/A')}")
            print()
    else:
        print("  WARNING: No events found with be_trigger/has_be_trigger field")
        print("  This means the new logging hasn't been deployed yet (DLL needs rebuild)")
        print()
else:
    print("No INTENT_REGISTERED events in last hour (may be normal if no trades)")
    print()

print("2. BREAK-EVEN TRIGGER EVENTS")
print("-" * 100)
if be_trigger_events:
    print(f"Found {len(be_trigger_events)} BE trigger events in last hour:")
    print()
    
    reached = [e for e in be_trigger_events if 'REACHED' in e.get('event', '').upper()]
    failed = [e for e in be_trigger_events if 'FAILED' in e.get('event', '').upper()]
    retry = [e for e in be_trigger_events if 'RETRY' in e.get('event', '').upper()]
    
    print(f"  BE Triggers Reached: {len(reached)}")
    print(f"  BE Triggers Failed: {len(failed)}")
    print(f"  BE Triggers Retrying: {len(retry)}")
    print()
    
    if reached:
        print("  Recent BE Triggers Reached:")
        for e in reached[-3:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Intent: {data.get('intent_id', 'N/A')[:30] if data.get('intent_id') else 'N/A'}...")
            print(f"      Breakout Level: {data.get('breakout_level', 'N/A')}")
            print(f"      BE Stop Price: {data.get('be_stop_price', 'N/A')}")
            print()
else:
    print("No BE trigger events in last hour (may be normal if no BE triggers reached)")
    print()

print("3. POSITION TRACKING EVENTS")
print("-" * 100)
if position_events:
    print(f"Found {len(position_events)} position-related events in last hour")
    # Check for any position mismatches or errors
    problems = [e for e in position_events if any(k in e.get('event', '').upper() for k in ['ERROR', 'MISMATCH', 'FAIL'])]
    if problems:
        print(f"  Position Problems: {len(problems)}")
        for e in problems[-3:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] {e.get('event', 'N/A')}")
            print(f"      Error: {data.get('error', 'N/A')}")
    else:
        print("  No position problems found")
    print()
else:
    print("No position events in last hour")
    print()

print("4. FLATTEN OPERATIONS")
print("-" * 100)
if flatten_events:
    failed = [e for e in flatten_events if 'FAILED' in e.get('event', '').upper()]
    success = [e for e in flatten_events if 'SUCCESS' in e.get('event', '').upper()]
    
    print(f"  Flatten Operations: {len(flatten_events)}")
    print(f"    Successful: {len(success)}")
    print(f"    Failed: {len(failed)}")
    
    if failed:
        print("  Recent Failures:")
        for e in failed[-3:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Error: {data.get('error', 'N/A')}")
    print()
else:
    print("No flatten events in last hour")
    print()

print("=" * 100)
print("SUMMARY")
print("=" * 100)
print()

if intent_registered:
    with_field = sum(1 for e in intent_registered if 'has_be_trigger' in str(e.get('data', {})))
    if with_field > 0:
        print("✓ BE Trigger Logging: WORKING (new format detected)")
    else:
        print("⚠ BE Trigger Logging: OLD FORMAT (DLL needs rebuild/restart)")
else:
    print("? BE Trigger Logging: No recent events to check")

if be_trigger_events:
    print("✓ BE Detection: ACTIVE (events found)")
else:
    print("? BE Detection: No recent events (may be normal)")

if flatten_events:
    failed_count = sum(1 for e in flatten_events if 'FAILED' in e.get('event', '').upper())
    if failed_count == 0:
        print("✓ Flatten Operations: WORKING (no failures)")
    else:
        print(f"⚠ Flatten Operations: {failed_count} failures found")
else:
    print("? Flatten Operations: No recent events")

print()
