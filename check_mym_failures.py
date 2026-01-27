#!/usr/bin/env python3
"""Check why MYM BarsRequest is failing"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
events = []

for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                if line.strip():
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except:
        pass

# Get latest MYM BARSREQUEST_FAILED
mym_failed = [e for e in events 
             if e.get('ts_utc', '').startswith('2026-01-26') and
             e.get('event') == 'BARSREQUEST_FAILED' and
             e.get('data', {}).get('instrument') == 'MYM']

mym_failed.sort(key=lambda x: x.get('ts_utc', ''), reverse=True)

print("="*80)
print("LATEST MYM BARSREQUEST_FAILED:")
print("="*80)

if mym_failed:
    latest = mym_failed[0]
    data = latest.get('data', {})
    print(f"\n  Time: {latest.get('ts_utc', '')[:19]}")
    print(f"  Reason: {data.get('reason', 'N/A')}")
    print(f"  Error: {data.get('error', 'N/A')}")
    print(f"  Error type: {data.get('error_type', 'N/A')}")
    if data.get('stack_trace'):
        print(f"\n  Stack trace (first 500 chars):")
        print(f"    {data.get('stack_trace', '')[:500]}")

# Check if GetAllExecutionInstrumentsForBarsRequest is being called
print(f"\n{'='*80}")
print("CHECKING IF NEW CODE PATH IS ACTIVE:")
print(f"{'='*80}")

# Look for logs that indicate the new code path
recent_events = [e for e in events if e.get('ts_utc', '').startswith('2026-01-26T18:')]
instruments_requested = set()
for e in recent_events:
    if e.get('event') == 'BARSREQUEST_REQUESTED':
        instrument = e.get('data', {}).get('instrument', '')
        if instrument:
            instruments_requested.add(instrument)

print(f"\n  Instruments requested in recent events:")
for inst in sorted(instruments_requested):
    print(f"    - {inst}")

print(f"\n{'='*80}")
