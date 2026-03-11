#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Generate today's issues summary"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

today = datetime.now(timezone.utc)
yesterday = today - timedelta(days=1)

# Collect events
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

# Categorize
critical = []
execution_failures = []
flatten_failures = []
be_issues = []
position_problems = []

for e in all_events:
    event_type = e.get('event', '').upper()
    data = e.get('data', {})
    
    if 'CRITICAL' in event_type or 'FLATTEN_FAILED_ALL_RETRIES' in event_type:
        critical.append(e)
    
    if any(k in event_type for k in ['EXECUTION_ERROR', 'ORDER_SUBMIT_FAIL', 'PROTECTIVE_ORDERS_FAILED']):
        execution_failures.append(e)
    
    if 'FLATTEN_FAILED' in event_type or 'FLATTEN_RETRY' in event_type:
        flatten_failures.append(e)
    
    if 'BE_TRIGGER_FAILED' in event_type or ('INTENT_REGISTERED' in event_type and data.get('has_be_trigger') == False):
        be_issues.append(e)
    
    if 'POSITION' in event_type and any(k in event_type for k in ['FAIL', 'ERROR', 'MISMATCH']):
        position_problems.append(e)

# Generate summary
summary = []
summary.append("=" * 100)
summary.append("TODAY'S ISSUES SUMMARY")
summary.append("=" * 100)
summary.append("")
summary.append(f"Analyzed {len(all_events):,} events from last 24 hours")
summary.append("")

# Critical Issues
summary.append("1. CRITICAL ISSUES")
summary.append("-" * 100)
if critical:
    summary.append(f"Found {len(critical)} critical issues:")
    by_type = defaultdict(list)
    for e in critical:
        by_type[e.get('event', 'UNKNOWN')].append(e)
    for event_type, events in sorted(by_type.items(), key=lambda x: len(x[1]), reverse=True)[:5]:
        summary.append(f"  {event_type}: {len(events)} occurrences")
        for e in events[-2:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            summary.append(f"    [{ts}] Intent: {data.get('intent_id', 'N/A')[:30] if data.get('intent_id') else 'N/A'}...")
            summary.append(f"      Instrument: {data.get('instrument', 'N/A')}")
            if data.get('error'):
                summary.append(f"      Error: {str(data.get('error', 'N/A'))[:150]}")
        summary.append("")
else:
    summary.append("No critical issues found")
    summary.append("")

# Execution Failures
summary.append("2. EXECUTION FAILURES")
summary.append("-" * 100)
if execution_failures:
    summary.append(f"Found {len(execution_failures)} execution failures:")
    by_type = defaultdict(list)
    for e in execution_failures:
        by_type[e.get('event', 'UNKNOWN')].append(e)
    for event_type, events in sorted(by_type.items(), key=lambda x: len(x[1]), reverse=True)[:10]:
        summary.append(f"  {event_type}: {len(events)} occurrences")
        if events:
            e = events[-1]
            data = e.get('data', {})
            summary.append(f"    Latest: Intent {data.get('intent_id', 'N/A')[:30] if data.get('intent_id') else 'N/A'}...")
            summary.append(f"      Instrument: {data.get('instrument', 'N/A')}")
            if data.get('error'):
                summary.append(f"      Error: {str(data.get('error', 'N/A'))[:100]}")
        summary.append("")
else:
    summary.append("No execution failures found")
    summary.append("")

# Flatten Failures
summary.append("3. FLATTEN FAILURES")
summary.append("-" * 100)
if flatten_failures:
    summary.append(f"Found {len(flatten_failures)} flatten failures:")
    for e in flatten_failures[-5:]:
        ts = e.get('ts_utc', '')[:19]
        data = e.get('data', {})
        summary.append(f"  [{ts}] Intent: {data.get('intent_id', 'N/A')[:30] if data.get('intent_id') else 'N/A'}...")
        summary.append(f"    Instrument: {data.get('instrument', 'N/A')}")
        summary.append(f"    Error: {data.get('error', 'N/A')}")
    summary.append("")
else:
    summary.append("No flatten failures found")
    summary.append("")

# Position Problems
summary.append("4. POSITION TRACKING PROBLEMS")
summary.append("-" * 100)
if position_problems:
    summary.append(f"Found {len(position_problems)} position problems:")
    for e in position_problems[-5:]:
        ts = e.get('ts_utc', '')[:19]
        data = e.get('data', {})
        summary.append(f"  [{ts}] {e.get('event', 'N/A')}")
        summary.append(f"    Intent: {data.get('intent_id', 'N/A')[:30] if data.get('intent_id') else 'N/A'}...")
        summary.append(f"    Instrument: {data.get('instrument', 'N/A')}")
    summary.append("")
else:
    summary.append("No position tracking problems found")
    summary.append("")

# Break-Even Issues
summary.append("5. BREAK-EVEN DETECTION ISSUES")
summary.append("-" * 100)
be_events = [e for e in all_events if 'BE' in e.get('event', '').upper() or 'BREAK_EVEN' in e.get('event', '').upper()]
be_failed = [e for e in be_events if 'FAILED' in e.get('event', '').upper()]
intent_no_be = [e for e in be_events if 'INTENT_REGISTERED' in e.get('event', '') and e.get('data', {}).get('has_be_trigger') == False]

summary.append(f"Break-even events: {len(be_events)}")
summary.append(f"  BE Triggers Failed: {len(be_failed)}")
summary.append(f"  Intents Without BE Trigger: {len(intent_no_be)}")
if be_failed:
    summary.append("  Recent BE Failures:")
    for e in be_failed[-3:]:
        data = e.get('data', {})
        summary.append(f"    Intent: {data.get('intent_id', 'N/A')[:30] if data.get('intent_id') else 'N/A'}...")
        summary.append(f"      Error: {data.get('error', 'N/A')}")
if intent_no_be:
    summary.append("  WARNING: Intents registered without BE trigger:")
    for e in intent_no_be[-3:]:
        data = e.get('data', {})
        summary.append(f"    Intent: {data.get('intent_id', 'N/A')[:30] if data.get('intent_id') else 'N/A'}...")
        summary.append(f"      Instrument: {data.get('instrument', 'N/A')}")
summary.append("")

# Fixes Summary
summary.append("6. FIXES APPLIED TODAY")
summary.append("-" * 100)
summary.append("1. POSITION ACCUMULATION BUG (MNQ/CL2) - FIXED")
summary.append("   Issue: Using filledTotal (cumulative) instead of fillQuantity (delta)")
summary.append("   Impact: Exponential position growth")
summary.append("   Fixed: OnEntryFill, HandleEntryFill, OnExitFill calls")
summary.append("   Status: Code fixed, requires DLL rebuild")
summary.append("")
summary.append("2. BREAK-EVEN DETECTION - FIXED")
summary.append("   Issue: BE trigger not computed when range unavailable")
summary.append("   Fix: Always compute BE trigger (doesn't require range)")
summary.append("   Issue: BE stop using intended entry price instead of actual fill price")
summary.append("   Fix: Use actual fill price from execution journal")
summary.append("   Status: Code fixed, requires DLL rebuild")
summary.append("")
summary.append("3. FLATTEN NULL REFERENCE EXCEPTION - FIXED")
summary.append("   Issue: NullReferenceException when flattening positions")
summary.append("   Root Cause: Accessing MasterInstrument.Name without null check")
summary.append("   Fix: Added comprehensive null checks and fallbacks")
summary.append("   Status: Code fixed, requires DLL rebuild")
summary.append("")
summary.append("4. MISSING GetEntry METHOD - FIXED")
summary.append("   Issue: ExecutionJournal.GetEntry() method missing")
summary.append("   Fix: Added GetEntry() method to retrieve journal entries")
summary.append("   Status: Code fixed, requires DLL rebuild")
summary.append("")

summary.append("=" * 100)
summary.append("ACTION REQUIRED")
summary.append("=" * 100)
summary.append("CRITICAL: Rebuild Robot.Core.dll to deploy all fixes")
summary.append("")
summary.append("After rebuild, verify:")
summary.append("  [ ] Position tracking works correctly (no -6 position issues)")
summary.append("  [ ] Break-even triggers are detected and stop modified")
summary.append("  [ ] Flatten operations work without null reference exceptions")
summary.append("  [ ] INTENT_REGISTERED events show has_be_trigger: true")
summary.append("")

# Write to file
with open("TODAYS_ISSUES_SUMMARY.md", "w", encoding="utf-8") as f:
    f.write("\n".join(summary))

print("\n".join(summary))
