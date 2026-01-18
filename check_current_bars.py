import json
from pathlib import Path
from collections import defaultdict
from datetime import datetime

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CURRENT BARS BEING RECEIVED")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

print(f"Checking last 500 log lines for current bar activity...")
print()

# Find most recent trading date locked
trading_date = None
for line in reversed(lines):
    try:
        entry = json.loads(line)
        if entry.get('event') == 'TRADING_DATE_LOCKED':
            trading_date = entry.get('data', {}).get('payload', {}).get('trading_date')
            break
    except:
        pass

print(f"Trading Date (from timetable): {trading_date}")
print()

# Check last 500 lines for bar events
bars_by_date = defaultdict(list)
bar_times = []

for line in lines[-500:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        payload = entry.get('data', {}).get('payload', {})
        
        if event == 'BAR_DATE_MISMATCH':
            bar_date = payload.get('bar_trading_date', '')
            bar_time_chicago = payload.get('bar_timestamp_chicago', '')
            bars_by_date[bar_date].append({
                'timestamp': entry.get('ts_utc', ''),
                'bar_time': bar_time_chicago
            })
            bar_times.append({
                'date': bar_date,
                'time': bar_time_chicago,
                'timestamp': entry.get('ts_utc', '')
            })
    except:
        pass

print("Current bars being received:")
print("=" * 80)

if bars_by_date:
    for date in sorted(bars_by_date.keys()):
        bars = bars_by_date[date]
        match_status = "MATCHES" if date == trading_date else "WRONG DATE (rejected)"
        print(f"\n{date}: {len(bars)} bars ({match_status})")
        
        if bars:
            print(f"  First bar: {bars[0]['bar_time']}")
            print(f"  Last bar: {bars[-1]['bar_time']}")
            print(f"  First timestamp: {bars[0]['timestamp']}")
            print(f"  Last timestamp: {bars[-1]['timestamp']}")
else:
    print("No bar events found in last 500 lines")

# Show most recent bars
if bar_times:
    print(f"\n{'=' * 80}")
    print("MOST RECENT 10 BARS:")
    print("=" * 80)
    
    for i, bar in enumerate(bar_times[-10:], 1):
        status = "MATCH" if bar['date'] == trading_date else "REJECT"
        print(f"{i}. [{bar['timestamp']}] {bar['date']} {bar['time']} - {status}")

# Summary
print(f"\n{'=' * 80}")
print("SUMMARY:")
print("=" * 80)

if trading_date:
    bars_from_trading_date = len(bars_by_date.get(trading_date, []))
    bars_from_other = sum(len(bars_by_date[d]) for d in bars_by_date.keys() if d != trading_date)
    
    print(f"\nTrading Date: {trading_date}")
    print(f"Bars from {trading_date}: {bars_from_trading_date}")
    print(f"Bars from other dates: {bars_from_other}")
    
    if bars_from_trading_date == 0:
        print(f"\nStatus: Waiting for bars from {trading_date}")
        print(f"  Current bars are from: {', '.join(sorted(bars_by_date.keys()))}")
    else:
        print(f"\nStatus: Processing bars from {trading_date}")
