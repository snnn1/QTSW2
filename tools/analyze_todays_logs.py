#!/usr/bin/env python3
"""Analyze today's logs for issues"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict, Counter
import re

print("=" * 100)
print("TODAY'S LOG ANALYSIS - ISSUE DETECTION")
print("=" * 100)
print()

# Get today's date
today = datetime.now(timezone.utc)
yesterday = today - timedelta(days=1)

# Collect all events from today
all_events = []
log_files = list(Path("logs/robot").glob("*.jsonl"))

print(f"Scanning {len(log_files)} log files...")
print()

for log_file in log_files:
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line_num, line in enumerate(f, 1):
                try:
                    e = json.loads(line.strip())
                    ts_str = e.get('ts_utc', '')
                    if ts_str:
                        try:
                            ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                            # Include events from last 24 hours
                            if ts >= yesterday:
                                e['_log_file'] = log_file.name
                                e['_line_num'] = line_num
                                all_events.append(e)
                        except:
                            pass
                except:
                    pass
    except Exception as ex:
        print(f"Error reading {log_file.name}: {ex}")

print(f"Found {len(all_events)} events from last 24 hours")
print()

# Categorize events by type
event_types = Counter()
error_events = []
warning_events = []
critical_events = []
be_events = []
execution_errors = []
data_issues = []

for e in all_events:
    event_type = e.get('event', '').upper()
    event_types[event_type] += 1
    
    # Check for errors
    if 'ERROR' in event_type or 'FAIL' in event_type or 'BLOCKED' in event_type or 'REJECTED' in event_type:
        error_events.append(e)
    
    # Check for warnings
    if 'WARN' in event_type or 'WARNING' in event_type:
        warning_events.append(e)
    
    # Check for critical issues
    if 'CRITICAL' in event_type or 'CRITICAL' in str(e.get('data', {})).upper():
        critical_events.append(e)
    
    # Break-even related
    if 'BE' in event_type or 'BREAK_EVEN' in event_type or 'BREAK_EVEN' in event_type:
        be_events.append(e)
    
    # Execution errors
    if any(keyword in event_type for keyword in ['EXECUTION_ERROR', 'ORDER_SUBMIT_FAIL', 'PROTECTIVE', 'FLATTEN']):
        execution_errors.append(e)
    
    # Data issues
    if any(keyword in event_type for keyword in ['DATA_FEED', 'BAR_REJECT', 'GAP', 'STALL', 'MISSING']):
        data_issues.append(e)

print("=" * 100)
print("1. CRITICAL ISSUES")
print("=" * 100)
if critical_events:
    print(f"Found {len(critical_events)} critical events:")
    for e in critical_events[-10:]:  # Last 10
        ts = e.get('ts_utc', '')[:19]
        event = e.get('event', 'N/A')
        data = e.get('data', {})
        print(f"\n  [{ts}] {event}")
        print(f"    File: {e.get('_log_file', 'N/A')}")
        if 'error' in str(data):
            print(f"    Error: {data.get('error', 'N/A')}")
        if 'message' in str(data):
            print(f"    Message: {data.get('message', 'N/A')}")
        if 'note' in str(data):
            print(f"    Note: {data.get('note', 'N/A')}")
else:
    print("No critical issues found ✓")

print("\n" + "=" * 100)
print("2. EXECUTION ERRORS")
print("=" * 100)
if execution_errors:
    print(f"Found {len(execution_errors)} execution-related errors:")
    
    # Group by error type
    error_groups = defaultdict(list)
    for e in execution_errors:
        event_type = e.get('event', '')
        error_groups[event_type].append(e)
    
    for event_type, events in sorted(error_groups.items(), key=lambda x: len(x[1]), reverse=True)[:10]:
        print(f"\n  {event_type}: {len(events)} occurrences")
        for e in events[-3:]:  # Last 3 of each type
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}]")
            if 'error' in str(data):
                print(f"      Error: {data.get('error', 'N/A')}")
            if 'intent_id' in str(data):
                print(f"      Intent: {data.get('intent_id', 'N/A')}")
            if 'instrument' in str(data):
                print(f"      Instrument: {data.get('instrument', 'N/A')}")
else:
    print("No execution errors found ✓")

print("\n" + "=" * 100)
print("3. DATA FEED ISSUES")
print("=" * 100)
if data_issues:
    print(f"Found {len(data_issues)} data-related issues:")
    
    # Group by issue type
    issue_groups = defaultdict(list)
    for e in data_issues:
        event_type = e.get('event', '')
        issue_groups[event_type].append(e)
    
    for event_type, events in sorted(issue_groups.items(), key=lambda x: len(x[1]), reverse=True)[:10]:
        print(f"\n  {event_type}: {len(events)} occurrences")
        for e in events[-3:]:  # Last 3 of each type
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}]")
            if 'instrument' in str(data):
                print(f"      Instrument: {data.get('instrument', 'N/A')}")
            if 'error' in str(data):
                print(f"      Error: {data.get('error', 'N/A')}")
            if 'note' in str(data):
                print(f"      Note: {data.get('note', 'N/A')}")
else:
    print("No data feed issues found ✓")

print("\n" + "=" * 100)
print("4. BREAK-EVEN STATUS")
print("=" * 100)
if be_events:
    print(f"Found {len(be_events)} break-even related events:")
    
    # Check for BE trigger reached
    be_triggered = [e for e in be_events if 'BE_TRIGGER_REACHED' in e.get('event', '')]
    be_failed = [e for e in be_events if 'BE_TRIGGER_FAILED' in e.get('event', '')]
    be_retry = [e for e in be_events if 'BE_TRIGGER_RETRY' in e.get('event', '')]
    intent_registered = [e for e in be_events if 'INTENT_REGISTERED' in e.get('event', '')]
    
    print(f"\n  BE Triggers Reached: {len(be_triggered)}")
    print(f"  BE Triggers Failed: {len(be_failed)}")
    print(f"  BE Triggers Retrying: {len(be_retry)}")
    print(f"  Intents Registered: {len(intent_registered)}")
    
    # Check intents with BE trigger
    intents_with_be = [e for e in intent_registered if e.get('data', {}).get('has_be_trigger') == True]
    intents_without_be = [e for e in intent_registered if e.get('data', {}).get('has_be_trigger') == False]
    
    print(f"\n  Intents WITH BE Trigger: {len(intents_with_be)}")
    print(f"  Intents WITHOUT BE Trigger: {len(intents_without_be)}")
    
    if intents_without_be:
        print("\n  ⚠️  WARNING: Some intents registered without BE trigger!")
        for e in intents_without_be[-5:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Intent: {data.get('intent_id', 'N/A')[:20]}... Instrument: {data.get('instrument', 'N/A')}")
    
    # Show recent BE trigger events
    if be_triggered:
        print("\n  Recent BE Triggers Reached:")
        for e in be_triggered[-5:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Intent: {data.get('intent_id', 'N/A')[:20]}...")
            print(f"      BE Stop Price: {data.get('be_stop_price', 'N/A')}")
            print(f"      Fill Price Used: {data.get('fill_price_used_for_be', 'N/A')}")
    
    if be_failed:
        print("\n  Recent BE Trigger Failures:")
        for e in be_failed[-5:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Intent: {data.get('intent_id', 'N/A')[:20]}...")
            print(f"      Error: {data.get('error', 'N/A')}")
else:
    print("No break-even events found (may be normal if no trades today)")

print("\n" + "=" * 100)
print("5. TOP EVENT TYPES")
print("=" * 100)
for event_type, count in event_types.most_common(20):
    print(f"  {event_type}: {count}")

print("\n" + "=" * 100)
print("6. WARNINGS SUMMARY")
print("=" * 100)
if warning_events:
    print(f"Found {len(warning_events)} warnings:")
    
    # Group by type
    warn_groups = defaultdict(list)
    for e in warning_events:
        event_type = e.get('event', '')
        warn_groups[event_type].append(e)
    
    for event_type, events in sorted(warn_groups.items(), key=lambda x: len(x[1]), reverse=True)[:10]:
        print(f"\n  {event_type}: {len(events)} occurrences")
        # Show sample
        if events:
            e = events[-1]
            data = e.get('data', {})
            if 'note' in str(data):
                print(f"    Sample: {data.get('note', 'N/A')[:100]}")
else:
    print("No warnings found ✓")

print("\n" + "=" * 100)
print("ANALYSIS COMPLETE")
print("=" * 100)
