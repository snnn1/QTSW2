import json
from pathlib import Path

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("INVESTIGATING DATE MISMATCH BUG")
print("=" * 80)
print()

# Find bars from 2026-01-16 that are being rejected
with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

print("Looking for bars from 2026-01-16 that are being rejected...")
print()

problematic_bars = []

for line in lines[-5000:]:
    try:
        entry = json.loads(line)
        if entry.get('event') == 'BAR_DATE_MISMATCH':
            payload = entry.get('data', {}).get('payload', {})
            locked_date = payload.get('locked_trading_date', '')
            bar_date = payload.get('bar_trading_date', '')
            
            # This is the bug - bars from 2026-01-16 matching locked date are being rejected
            if locked_date == '2026-01-16' and bar_date == '2026-01-16':
                problematic_bars.append({
                    'timestamp': entry.get('ts_utc', ''),
                    'locked_date': locked_date,
                    'bar_date': bar_date,
                    'bar_time_chicago': payload.get('bar_timestamp_chicago', ''),
                    'bar_time_utc': payload.get('bar_timestamp_utc', ''),
                    'full_payload': payload
                })
    except:
        pass

if problematic_bars:
    print(f"BUG FOUND: {len(problematic_bars)} bars from 2026-01-16 are being rejected!")
    print(f"  Locked Trading Date: {problematic_bars[0]['locked_date']}")
    print(f"  Bar Trading Date: {problematic_bars[0]['bar_date']}")
    print(f"  These dates MATCH but bars are still being rejected!")
    print()
    print("First 5 problematic bars:")
    for i, bar in enumerate(problematic_bars[:5], 1):
        print(f"\n  {i}. [{bar['timestamp']}]")
        print(f"     Bar Time (Chicago): {bar['bar_time_chicago']}")
        print(f"     Bar Time (UTC): {bar['bar_time_utc']}")
        print(f"     Locked Date: {bar['locked_date']}")
        print(f"     Bar Date: {bar['bar_date']}")
    
    print(f"\n{'=' * 80}")
    print("ROOT CAUSE ANALYSIS:")
    print("=" * 80)
    print("\nThe dates match (2026-01-16 == 2026-01-16) but bars are still rejected.")
    print("This suggests a bug in the date comparison logic in OnBar().")
    print("\nPossible causes:")
    print("  1. DateOnly comparison issue")
    print("  2. Timezone conversion issue")
    print("  3. String vs DateOnly comparison issue")
else:
    print("No problematic bars found - all bars from 2026-01-16 are being processed correctly")
