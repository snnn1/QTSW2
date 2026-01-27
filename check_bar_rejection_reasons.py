#!/usr/bin/env python3
"""Check why bars are being rejected"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
events = []

# Read all robot log files
for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except Exception as e:
        pass

# Find BAR_INVALID events
bar_invalid = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_INVALID'):
        bar_invalid.append(e)

if bar_invalid:
    print("="*80)
    print(f"BAR_INVALID EVENTS (Found {len(bar_invalid)}):")
    print("="*80)
    for e in bar_invalid[:5]:
        data = e.get('data', {})
        if isinstance(data, dict):
            print(f"  {e.get('ts_utc', 'N/A')[:19]} | Reason: {data.get('reason', 'N/A')}")

# Find BAR_DUPLICATE_REJECTED events
bar_duplicate = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        'DUPLICATE' in e.get('event', '')):
        bar_duplicate.append(e)

if bar_duplicate:
    print(f"\n{'='*80}")
    print(f"BAR_DUPLICATE EVENTS (Found {len(bar_duplicate)}):")
    print(f"{'='*80}")
    for e in bar_duplicate[:5]:
        data = e.get('data', {})
        if isinstance(data, dict):
            print(f"  {e.get('ts_utc', 'N/A')[:19]} | {data.get('precedence_rule', 'N/A')}")

# Find BAR_TOO_RECENT events
bar_too_recent = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        ('TOO_RECENT' in e.get('event', '') or 'PARTIAL' in e.get('event', ''))):
        bar_too_recent.append(e)

if bar_too_recent:
    print(f"\n{'='*80}")
    print(f"BAR_TOO_RECENT/PARTIAL EVENTS (Found {len(bar_too_recent)}):")
    print(f"{'='*80}")
    for e in bar_too_recent[:5]:
        data = e.get('data', {})
        if isinstance(data, dict):
            print(f"  {e.get('ts_utc', 'N/A')[:19]} | Reason: {data.get('reason', 'N/A')}")

# Check BAR_ADMISSION_PROOF vs actual buffer
print(f"\n{'='*80}")
print("BAR ADMISSION ANALYSIS:")
print(f"{'='*80}")

# Count bars that passed admission check
bar_proof = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_ADMISSION_PROOF'):
        bar_proof.append(e)

if bar_proof:
    accepted_count = sum(1 for e in bar_proof if e.get('data', {}).get('comparison_result', False) == True)
    print(f"  Bars that passed admission check: {accepted_count}")
    print(f"  Total bars checked: {len(bar_proof)}")
    
    # Check if any bars in the 08:00-11:00 window passed
    bars_in_window_accepted = []
    for e in bar_proof:
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_time_str = data.get('bar_time_chicago', '')
            result = data.get('comparison_result', False)
            if bar_time_str and result == True:
                try:
                    from datetime import datetime
                    bar_time = datetime.fromisoformat(bar_time_str.replace('Z', '+00:00'))
                    if bar_time.hour >= 8 and (bar_time.hour < 11 or (bar_time.hour == 11 and bar_time.minute == 0)):
                        bars_in_window_accepted.append(bar_time_str)
                except:
                    pass
    
    print(f"  Bars in [08:00, 11:00) that passed: {len(bars_in_window_accepted)}")
    if bars_in_window_accepted:
        print(f"  Sample: {bars_in_window_accepted[:5]}")

print(f"\n{'='*80}")
