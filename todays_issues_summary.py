#!/usr/bin/env python3
"""Comprehensive summary of today's issues from logs"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict, Counter

print("=" * 100)
print("TODAY'S ISSUES SUMMARY - COMPREHENSIVE ANALYSIS")
print("=" * 100)
print()

today = datetime.now(timezone.utc)
yesterday = today - timedelta(days=1)

# Collect all events
all_events = []
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
                                e['_log_file'] = log_file.name
                                all_events.append(e)
                        except:
                            pass
                except:
                    pass
    except:
        pass

print(f"Analyzing {len(all_events):,} events from last 24 hours")
print()

# Categorize issues
critical_issues = []
errors = []
warnings = []
execution_failures = []
data_issues = []
position_issues = []
be_issues = []

for e in all_events:
    event_type = e.get('event', '').upper()
    data = e.get('data', {})
    
    # Critical issues
    if 'CRITICAL' in event_type or 'CRITICAL' in str(data).upper() or 'EMERGENCY' in event_type:
        critical_issues.append(e)
    
    # Errors
    if any(keyword in event_type for keyword in ['ERROR', 'FAIL', 'BLOCKED', 'REJECTED', 'EXCEPTION']):
        errors.append(e)
    
    # Warnings
    if 'WARN' in event_type or 'WARNING' in event_type:
        warnings.append(e)
    
    # Execution failures
    if any(keyword in event_type for keyword in ['EXECUTION_ERROR', 'ORDER_SUBMIT_FAIL', 'PROTECTIVE', 'FLATTEN_FAILED']):
        execution_failures.append(e)
    
    # Data issues
    if any(keyword in event_type for keyword in ['DATA_FEED', 'BAR_REJECT', 'GAP', 'STALL', 'MISSING']):
        data_issues.append(e)
    
    # Position issues
    if any(keyword in event_type for keyword in ['POSITION', 'EXPOSURE', 'FLATTEN']):
        position_issues.append(e)
    
    # Break-even issues
    if any(keyword in event_type for keyword in ['BE', 'BREAK_EVEN', 'BREAK_EVEN']):
        be_issues.append(e)

print("=" * 100)
print("1. CRITICAL ISSUES")
print("=" * 100)
print()

if critical_issues:
    print(f"Found {len(critical_issues)} critical issues:")
    print()
    
    # Group by type
    critical_by_type = defaultdict(list)
    for e in critical_issues:
        event_type = e.get('event', 'UNKNOWN')
        critical_by_type[event_type].append(e)
    
    for event_type, events in sorted(critical_by_type.items(), key=lambda x: len(x[1]), reverse=True):
        print(f"  {event_type}: {len(events)} occurrences")
        
        # Show details of most recent
        for e in events[-3:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}]")
            if 'intent_id' in str(data):
                print(f"      Intent: {data.get('intent_id', 'N/A')[:30]}...")
            if 'instrument' in str(data):
                print(f"      Instrument: {data.get('instrument', 'N/A')}")
            if 'error' in str(data):
                print(f"      Error: {data.get('error', 'N/A')}")
            if 'message' in str(data):
                print(f"      Message: {data.get('message', 'N/A')[:100]}")
            if 'note' in str(data):
                print(f"      Note: {data.get('note', 'N/A')[:100]}")
        print()
else:
    print("No critical issues found")
    print()

print("=" * 100)
print("2. EXECUTION FAILURES")
print("=" * 100)
print()

if execution_failures:
    print(f"Found {len(execution_failures)} execution-related failures:")
    print()
    
    # Group by type
    failures_by_type = defaultdict(list)
    for e in execution_failures:
        event_type = e.get('event', 'UNKNOWN')
        failures_by_type[event_type].append(e)
    
    for event_type, events in sorted(failures_by_type.items(), key=lambda x: len(x[1]), reverse=True)[:15]:
        print(f"  {event_type}: {len(events)} occurrences")
        
        # Show details
        for e in events[-2:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}]")
            if 'intent_id' in str(data):
                print(f"      Intent: {data.get('intent_id', 'N/A')[:30]}...")
            if 'instrument' in str(data):
                print(f"      Instrument: {data.get('instrument', 'N/A')}")
            if 'error' in str(data):
                error_msg = str(data.get('error', 'N/A'))
                if len(error_msg) > 150:
                    error_msg = error_msg[:150] + "..."
                print(f"      Error: {error_msg}")
            if 'reason' in str(data):
                print(f"      Reason: {data.get('reason', 'N/A')}")
        print()
else:
    print("No execution failures found")
    print()

print("=" * 100)
print("3. POSITION TRACKING ISSUES")
print("=" * 100)
print()

if position_issues:
    # Filter for actual problems
    position_problems = [e for e in position_issues if any(keyword in e.get('event', '').upper() 
                                                          for keyword in ['FAIL', 'ERROR', 'MISMATCH', 'CRITICAL'])]
    
    if position_problems:
        print(f"Found {len(position_problems)} position-related problems:")
        print()
        
        for e in position_problems[-10:]:
            ts = e.get('ts_utc', '')[:19]
            event = e.get('event', 'N/A')
            data = e.get('data', {})
            print(f"  [{ts}] {event}")
            if 'intent_id' in str(data):
                print(f"    Intent: {data.get('intent_id', 'N/A')[:30]}...")
            if 'instrument' in str(data):
                print(f"    Instrument: {data.get('instrument', 'N/A')}")
            if 'position' in str(data):
                print(f"    Position: {data.get('position', 'N/A')}")
            if 'error' in str(data):
                print(f"    Error: {data.get('error', 'N/A')}")
            print()
    else:
        print("No position tracking problems found")
        print()
else:
    print("No position issues found")
    print()

print("=" * 100)
print("4. BREAK-EVEN DETECTION ISSUES")
print("=" * 100)
print()

if be_issues:
    be_failed = [e for e in be_issues if 'FAILED' in e.get('event', '').upper()]
    be_retry = [e for e in be_issues if 'RETRY' in e.get('event', '').upper()]
    intent_no_be = [e for e in be_issues if 'INTENT_REGISTERED' in e.get('event', '') 
                    and e.get('data', {}).get('has_be_trigger') == False]
    
    print(f"Break-even related events: {len(be_issues)}")
    print(f"  BE Triggers Failed: {len(be_failed)}")
    print(f"  BE Triggers Retrying: {len(be_retry)}")
    print(f"  Intents Without BE Trigger: {len(intent_no_be)}")
    print()
    
    if be_failed:
        print("  Recent BE Failures:")
        for e in be_failed[-5:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Intent: {data.get('intent_id', 'N/A')[:30]}...")
            print(f"      Error: {data.get('error', 'N/A')}")
        print()
    
    if intent_no_be:
        print("  ⚠️  Intents Registered Without BE Trigger:")
        for e in intent_no_be[-5:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Intent: {data.get('intent_id', 'N/A')[:30]}...")
            print(f"      Instrument: {data.get('instrument', 'N/A')}")
        print()
else:
    print("No break-even issues found (may be normal if no trades today)")
    print()

print("=" * 100)
print("5. DATA FEED ISSUES")
print("=" * 100)
print()

if data_issues:
    # Filter for actual problems (not just warnings)
    data_problems = [e for e in data_issues if any(keyword in e.get('event', '').upper() 
                                                   for keyword in ['FAIL', 'ERROR', 'STALL', 'CRITICAL'])]
    
    if data_problems:
        print(f"Found {len(data_problems)} data feed problems:")
        print()
        
        # Group by type
        problems_by_type = defaultdict(list)
        for e in data_problems:
            event_type = e.get('event', 'UNKNOWN')
            problems_by_type[event_type].append(e)
        
        for event_type, events in sorted(problems_by_type.items(), key=lambda x: len(x[1]), reverse=True)[:10]:
            print(f"  {event_type}: {len(events)} occurrences")
            # Show sample
            if events:
                e = events[-1]
                data = e.get('data', {})
                if 'instrument' in str(data):
                    print(f"    Sample Instrument: {data.get('instrument', 'N/A')}")
                if 'error' in str(data):
                    print(f"    Sample Error: {data.get('error', 'N/A')[:100]}")
            print()
    else:
        print("No data feed problems found (warnings are expected for historical data)")
        print()
else:
    print("No data feed issues found")
    print()

print("=" * 100)
print("6. SUMMARY OF FIXES APPLIED TODAY")
print("=" * 100)
print()

print("1. POSITION ACCUMULATION BUG (MNQ/CL2) - FIXED")
print("   - Issue: Using filledTotal (cumulative) instead of fillQuantity (delta)")
print("   - Impact: Exponential position growth (1 → 2 → 4 → 8 → 16 → 32 → 64)")
print("   - Fixed in: OnEntryFill, HandleEntryFill, OnExitFill calls")
print("   - Status: Code fixed, requires DLL rebuild")
print()

print("2. BREAK-EVEN DETECTION - FIXED")
print("   - Issue: BE trigger not computed when range unavailable")
print("   - Fix: Always compute BE trigger (doesn't require range)")
print("   - Issue: BE stop using intended entry price instead of actual fill price")
print("   - Fix: Use actual fill price from execution journal")
print("   - Status: Code fixed, requires DLL rebuild")
print()

print("3. FLATTEN NULL REFERENCE EXCEPTION - FIXED")
print("   - Issue: NullReferenceException when flattening positions")
print("   - Root Cause: Accessing MasterInstrument.Name without null check")
print("   - Fix: Added comprehensive null checks and fallbacks")
print("   - Status: Code fixed, requires DLL rebuild")
print()

print("4. MISSING GetEntry METHOD - FIXED")
print("   - Issue: ExecutionJournal.GetEntry() method missing")
print("   - Fix: Added GetEntry() method to retrieve journal entries")
print("   - Status: Code fixed, requires DLL rebuild")
print()

print("=" * 100)
print("7. ACTION REQUIRED")
print("=" * 100)
print()

print("⚠️  CRITICAL: Rebuild Robot.Core.dll to deploy all fixes")
print()
print("After rebuild, verify:")
print("  [ ] Position tracking works correctly (no -6 position issues)")
print("  [ ] Break-even triggers are detected and stop modified")
print("  [ ] Flatten operations work without null reference exceptions")
print("  [ ] INTENT_REGISTERED events show has_be_trigger: true")
print()

print("=" * 100)
print("ANALYSIS COMPLETE")
print("=" * 100)
