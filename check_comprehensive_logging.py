#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Comprehensive logging check to verify all fixes are working"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

recent = datetime.now(timezone.utc) - timedelta(hours=6)

events_by_type = defaultdict(list)
errors = []
warnings = []

print("=" * 100)
print("COMPREHENSIVE LOGGING CHECK")
print("=" * 100)
print(f"Checking logs from last 6 hours (since {recent.strftime('%Y-%m-%d %H:%M:%S UTC')})")
print()

for log_file in Path("logs/robot").glob("*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line_num, line in enumerate(f, 1):
                try:
                    e = json.loads(line.strip())
                    ts_str = e.get('ts_utc', '')
                    if ts_str:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                        if ts >= recent:
                            event_type = e.get('event', '')
                            events_by_type[event_type].append(e)
                            
                            # Check for errors
                            if 'ERROR' in event_type.upper() or 'FAIL' in event_type.upper():
                                errors.append(e)
                            
                            # Check for warnings
                            if 'WARN' in event_type.upper():
                                warnings.append(e)
                except json.JSONDecodeError:
                    pass
                except Exception as ex:
                    pass
    except Exception:
        pass

print("1. INTENT REGISTRATION (BE Trigger Logging)")
print("-" * 100)
intents = events_by_type.get('INTENT_REGISTERED', [])
if intents:
    print(f"Found {len(intents)} INTENT_REGISTERED events")
    
    with_be_field = sum(1 for e in intents if 'has_be_trigger' in str(e.get('data', {})))
    with_be_trigger = sum(1 for e in intents if e.get('data', {}).get('has_be_trigger') == True)
    without_be_trigger = sum(1 for e in intents if e.get('data', {}).get('has_be_trigger') == False)
    
    print(f"  Events with BE trigger field: {with_be_field}/{len(intents)}")
    print(f"  Events WITH BE trigger set: {with_be_trigger}")
    print(f"  Events WITHOUT BE trigger: {without_be_trigger}")
    
    if with_be_field > 0:
        print("  [OK] NEW LOGGING FORMAT ACTIVE")
        print()
        print("  Recent intents:")
        for e in intents[-3:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Intent: {data.get('intent_id', 'N/A')[:25]}...")
            print(f"      Instrument: {data.get('instrument', 'N/A')}")
            print(f"      Direction: {data.get('direction', 'N/A')}")
            print(f"      Entry Price: {data.get('entry_price', 'N/A')}")
            print(f"      Has BE Trigger: {data.get('has_be_trigger', 'N/A')}")
            if data.get('be_trigger'):
                print(f"      BE Trigger Price: {data.get('be_trigger', 'N/A')}")
            print()
    else:
        print("  [WARNING] OLD LOGGING FORMAT - DLL needs restart")
        print()
else:
    print("No INTENT_REGISTERED events found (may be normal if no trades)")
    print()

print("2. BREAK-EVEN TRIGGER EVENTS")
print("-" * 100)
be_events = [e for et in events_by_type.keys() if 'BE_TRIGGER' in et.upper() for e in events_by_type[et]]
if be_events:
    reached = [e for e in be_events if 'REACHED' in e.get('event', '').upper()]
    failed = [e for e in be_events if 'FAILED' in e.get('event', '').upper()]
    retry = [e for e in be_events if 'RETRY' in e.get('event', '').upper()]
    
    print(f"Found {len(be_events)} BE trigger events:")
    print(f"  BE Triggers Reached: {len(reached)}")
    print(f"  BE Triggers Failed: {len(failed)}")
    print(f"  BE Triggers Retrying: {len(retry)}")
    print()
    
    if reached:
        print("  Recent BE Triggers Reached:")
        for e in reached[-3:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Intent: {data.get('intent_id', 'N/A')[:25]}...")
            print(f"      Instrument: {data.get('instrument', 'N/A')}")
            print(f"      Breakout Level: {data.get('breakout_level', 'N/A')}")
            print(f"      BE Stop Price: {data.get('be_stop_price', 'N/A')}")
            if data.get('actual_fill_price'):
                print(f"      Actual Fill Price: {data.get('actual_fill_price', 'N/A')}")
            print()
    
    if failed:
        print("  BE Trigger Failures:")
        for e in failed[-3:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Error: {data.get('error', 'N/A')}")
            print()
else:
    print("No BE trigger events found (may be normal if no BE triggers reached)")
    print()

print("3. POSITION TRACKING")
print("-" * 100)
position_events = [e for et in events_by_type.keys() if any(k in et.upper() for k in ['FILL', 'POSITION', 'EXECUTION']) for e in events_by_type[et]]
if position_events:
    print(f"Found {len(position_events)} position-related events")
    
    # Check for position problems
    problems = [e for e in position_events if any(k in e.get('event', '').upper() for k in ['ERROR', 'MISMATCH', 'FAIL'])]
    if problems:
        print(f"  [WARNING] Position Problems: {len(problems)}")
        for e in problems[-3:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] {e.get('event', 'N/A')}")
            print(f"      Error: {data.get('error', 'N/A')}")
    else:
        print("  [OK] No position problems detected")
    print()
else:
    print("No position events found")
    print()

print("4. FLATTEN OPERATIONS")
print("-" * 100)
flatten_events = [e for et in events_by_type.keys() if 'FLATTEN' in et.upper() for e in events_by_type[et]]
if flatten_events:
    failed = [e for e in flatten_events if 'FAILED' in e.get('event', '').upper()]
    success = [e for e in flatten_events if 'SUCCESS' in e.get('event', '').upper()]
    
    print(f"Found {len(flatten_events)} flatten operations:")
    print(f"  Successful: {len(success)}")
    print(f"  Failed: {len(failed)}")
    
    if failed:
        print("  [WARNING] Flatten Failures:")
        for e in failed[-3:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Error: {data.get('error', 'N/A')}")
    else:
        print("  [OK] No flatten failures")
    print()
else:
    print("No flatten events found")
    print()

print("5. ERRORS AND WARNINGS")
print("-" * 100)
if errors:
    print(f"Found {len(errors)} error events:")
    error_types = defaultdict(int)
    for e in errors:
        error_types[e.get('event', 'UNKNOWN')] += 1
    
    for event_type, count in sorted(error_types.items(), key=lambda x: x[1], reverse=True)[:5]:
        print(f"  {event_type}: {count}")
    print()
else:
    print("[OK] No errors found")
    print()

if warnings:
    print(f"Found {len(warnings)} warning events:")
    warning_types = defaultdict(int)
    for e in warnings:
        warning_types[e.get('event', 'UNKNOWN')] += 1
    
    for event_type, count in sorted(warning_types.items(), key=lambda x: x[1], reverse=True)[:5]:
        print(f"  {event_type}: {count}")
    print()
else:
    print("[OK] No warnings found")
    print()

print("=" * 100)
print("SUMMARY")
print("=" * 100)
print()

# Summary checks
checks = []

if intents:
    with_field = sum(1 for e in intents if 'has_be_trigger' in str(e.get('data', {})))
    if with_field > 0:
        checks.append(("[OK]", "BE Trigger Logging: NEW FORMAT ACTIVE"))
    else:
        checks.append(("[WARNING]", "BE Trigger Logging: OLD FORMAT (restart needed)"))
else:
    checks.append(("[?]", "BE Trigger Logging: No recent events"))

if be_events:
    failed_count = sum(1 for e in be_events if 'FAILED' in e.get('event', '').upper())
    if failed_count == 0:
        checks.append(("[OK]", f"BE Detection: {len([e for e in be_events if 'REACHED' in e.get('event', '').upper()])} triggers reached"))
    else:
        checks.append(("[WARNING]", f"BE Detection: {failed_count} failures"))
else:
    checks.append(("[?]", "BE Detection: No recent events"))

if flatten_events:
    failed_count = sum(1 for e in flatten_events if 'FAILED' in e.get('event', '').upper())
    if failed_count == 0:
        checks.append(("[OK]", "Flatten Operations: Working"))
    else:
        checks.append(("[WARNING]", f"Flatten Operations: {failed_count} failures"))
else:
    checks.append(("[?]", "Flatten Operations: No recent events"))

if not errors:
    checks.append(("[OK]", "No errors detected"))
else:
    checks.append(("[WARNING]", f"{len(errors)} errors found"))

for status, message in checks:
    print(f"{status} {message}")

print()
