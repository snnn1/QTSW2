import json
from pathlib import Path

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING EXACT BAR_DATE_MISMATCH ENTRIES")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Find BAR_DATE_MISMATCH events where bar_trading_date is 2026-01-16
mismatches_16 = []

for line in lines[-5000:]:
    try:
        entry = json.loads(line)
        if entry.get('event') == 'BAR_DATE_MISMATCH':
            payload = entry.get('data', {}).get('payload', {})
            locked = payload.get('locked_trading_date', '')
            bar_date = payload.get('bar_trading_date', '')
            
            # Check if bar is from 2026-01-16
            if '2026-01-16' in bar_date or '2026-01-16' in payload.get('bar_timestamp_chicago', ''):
                mismatches_16.append({
                    'timestamp': entry.get('ts_utc', ''),
                    'locked_date': locked,
                    'bar_date': bar_date,
                    'bar_time_chicago': payload.get('bar_timestamp_chicago', ''),
                    'bar_time_utc': payload.get('bar_timestamp_utc', ''),
                    'full_line': line[:500]  # First 500 chars
                })
    except:
        pass

print(f"Found {len(mismatches_16)} BAR_DATE_MISMATCH events involving 2026-01-16")
print()

if mismatches_16:
    print("First 10 entries:")
    for i, m in enumerate(mismatches_16[:10], 1):
        print(f"\n{i}. [{m['timestamp']}]")
        print(f"   Locked Date: {m['locked_date']}")
        print(f"   Bar Date: {m['bar_date']}")
        print(f"   Bar Time (Chicago): {m['bar_time_chicago']}")
        print(f"   Bar Time (UTC): {m['bar_time_utc']}")
        
        # Check if dates actually match
        if m['locked_date'] == m['bar_date']:
            print(f"   *** BUG: Dates match ({m['locked_date']} == {m['bar_date']}) but bar rejected! ***")
        else:
            print(f"   Dates differ: {m['locked_date']} != {m['bar_date']}")

# Also check what the actual bar timestamp dates are
print(f"\n{'=' * 80}")
print("ANALYZING BAR TIMESTAMPS:")
print("=" * 80)

from datetime import datetime
import pytz

chicago_tz = pytz.timezone('America/Chicago')

for m in mismatches_16[:5]:
    bar_time_str = m['bar_time_chicago']
    if bar_time_str:
        try:
            # Parse the timestamp
            if 'T' in bar_time_str:
                # Format: 2026-01-16T12:25:00.0000000-06:00
                dt_str = bar_time_str.split('T')[0]  # Get date part
                print(f"\nBar Time String: {bar_time_str}")
                print(f"  Extracted Date: {dt_str}")
                print(f"  Locked Date: {m['locked_date']}")
                print(f"  Bar Date Field: {m['bar_date']}")
                
                if dt_str == m['locked_date']:
                    print(f"  *** MISMATCH: Bar timestamp date ({dt_str}) matches locked date ({m['locked_date']}) but bar_date field is {m['bar_date']} ***")
        except Exception as e:
            print(f"  Error parsing: {e}")
