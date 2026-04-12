#!/usr/bin/env python3
"""Check RTY brackets submission issue in detail"""
import json
from pathlib import Path
from datetime import datetime, timezone

log_file = Path("logs/robot/robot_RTY.jsonl")
if not log_file.exists():
    print("RTY log file not found")
    exit(1)

print("="*80)
print("RTY BRACKETS SUBMISSION DETAILED ANALYSIS")
print("="*80)

events = []
with open(log_file, 'r', encoding='utf-8-sig') as f:
    for line in f:
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except:
                pass

# Find STOP_BRACKETS_SUBMIT_ENTERED event
entered_events = [e for e in events if e.get('event') == 'STOP_BRACKETS_SUBMIT_ENTERED']
print(f"\nFound {len(entered_events)} STOP_BRACKETS_SUBMIT_ENTERED events")

for entered in entered_events[-3:]:  # Check last 3
    ts = entered.get('ts_utc', '')[:19]
    data = entered.get('data', {})
    
    print(f"\n{'='*80}")
    print(f"STOP_BRACKETS_SUBMIT_ENTERED at {ts}")
    print(f"{'='*80}")
    print(f"Precondition checks:")
    print(f"  _stopBracketsSubmittedAtLock: {data.get('_stopBracketsSubmittedAtLock', 'N/A')}")
    print(f"  journal_committed: {data.get('journal_committed', 'N/A')}")
    print(f"  state: {data.get('state', 'N/A')}")
    print(f"  range_invalidated: {data.get('range_invalidated', 'N/A')}")
    print(f"  execution_adapter_null: {data.get('execution_adapter_null', 'N/A')}")
    print(f"  execution_journal_null: {data.get('execution_journal_null', 'N/A')}")
    print(f"  risk_gate_null: {data.get('risk_gate_null', 'N/A')}")
    print(f"  brk_long_has_value: {data.get('brk_long_has_value', 'N/A')}")
    print(f"  brk_short_has_value: {data.get('brk_short_has_value', 'N/A')}")
    print(f"  range_high_has_value: {data.get('range_high_has_value', 'N/A')}")
    print(f"  range_low_has_value: {data.get('range_low_has_value', 'N/A')}")
    
    # Check for early return after this
    entered_time = datetime.fromisoformat(entered['ts_utc'].replace('Z', '+00:00'))
    after_events = [e for e in events 
                   if datetime.fromisoformat(e['ts_utc'].replace('Z', '+00:00')) > entered_time]
    
    early_return = [e for e in after_events[:20] if e.get('event') == 'STOP_BRACKETS_EARLY_RETURN']
    skipped = [e for e in after_events[:20] if e.get('event') == 'STOP_BRACKETS_SKIPPED_AT_LOCK']
    attempt = [e for e in after_events[:20] if 'STOP_BRACKETS_SUBMIT_ATTEMPT' in e.get('event', '')]
    
    if early_return:
        print(f"\n  [FOUND] STOP_BRACKETS_EARLY_RETURN:")
        for er in early_return:
            reason = er.get('data', {}).get('reason', 'N/A')
            print(f"    Reason: {reason}")
            print(f"    Data: {json.dumps(er.get('data', {}), indent=4)}")
    elif skipped:
        print(f"\n  [FOUND] STOP_BRACKETS_SKIPPED_AT_LOCK:")
        for sk in skipped:
            reasons = sk.get('data', {}).get('skip_reasons', [])
            print(f"    Skip reasons: {reasons}")
    elif attempt:
        print(f"\n  [FOUND] STOP_BRACKETS_SUBMIT_ATTEMPT - brackets were submitted!")
    else:
        print(f"\n  [MISSING] No early return or attempt found - function may have crashed or returned silently")

print("\n" + "="*80)
