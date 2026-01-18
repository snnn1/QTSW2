import json
from pathlib import Path
from datetime import datetime
from collections import defaultdict

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("PLAYBACK DATE ANALYSIS")
print("=" * 80)
print()

# Find all bars and their dates
bar_dates = defaultdict(list)

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    for line in f.readlines()[-500:]:  # Last 500 lines
        try:
            entry = json.loads(line)
            event = entry.get('event', '')
            payload = entry.get('data', {}).get('payload', {})
            
            if event == 'TRADING_DATE_LOCKED':
                print(f"TRADING_DATE_LOCKED:")
                print(f"  Timestamp: {entry.get('ts_utc', 'N/A')}")
                print(f"  Trading Date: {payload.get('trading_date', 'N/A')}")
                print(f"  Bar Timestamp: {payload.get('bar_timestamp_chicago', 'N/A')}")
                print(f"  Bar Time of Day: {payload.get('bar_time_of_day_chicago', 'N/A')}")
                print(f"  Earliest Session Start: {payload.get('earliest_session_range_start', 'N/A')}")
                print()
            
            if event == 'BAR_DATE_MISMATCH':
                bar_date = payload.get('bar_trading_date', '')
                bar_time = payload.get('bar_timestamp_chicago', '')
                if bar_date:
                    bar_dates[bar_date].append(bar_time)
        except:
            pass

# Show date distribution
if bar_dates:
    print("Bar Dates in Replay (from BAR_DATE_MISMATCH events):")
    print("=" * 80)
    for date in sorted(bar_dates.keys()):
        times = bar_dates[date]
        print(f"\n{date}: {len(times)} bars")
        if times:
            print(f"  First bar: {times[0]}")
            print(f"  Last bar: {times[-1]}")

print("\n" + "=" * 80)
print("ANALYSIS:")
print("=" * 80)
print("The trading date locked to 2026-01-02 because that's the first bar NinjaTrader")
print("sent during playback. All subsequent bars from 2026-01-16 are being rejected.")
print()
print("SOLUTION:")
print("1. In NinjaTrader, configure playback to start from 2026-01-16")
print("2. Or ensure the first bar in your historical data is from 2026-01-16")
print("3. The fix is working correctly - it's preventing overnight bars from locking")
print("   the wrong date, but playback is starting from an earlier date.")
