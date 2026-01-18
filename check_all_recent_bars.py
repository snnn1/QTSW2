import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("COMPREHENSIVE BAR CHECK")
print("=" * 80)
print()

now_utc = datetime.now(timezone.utc)
now_chicago = now_utc.astimezone(timezone(timedelta(hours=-6)))

print(f"Current UTC: {now_utc}")
print(f"Current Chicago: {now_chicago}")
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Check ALL bar events, not just recent
all_bar_events = []
for line in lines:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        
        if event in ['BAR_ACCEPTED', 'BAR_DATE_MISMATCH', 'ENGINE_BAR_HEARTBEAT', 'BAR_RECEIVED_BEFORE_DATE_LOCKED']:
            payload = entry.get('data', {}).get('payload', {})
            bar_chicago = payload.get('bar_timestamp_chicago', '')
            bar_date = payload.get('bar_trading_date', '')
            
            if bar_chicago and bar_date == '2026-01-16':
                # Parse Chicago time
                try:
                    if 'T' in bar_chicago:
                        time_part = bar_chicago.split('T')[1].split('.')[0]
                        hour_min = time_part[:5]  # HH:MM
                        all_bar_events.append({
                            'timestamp': entry.get('ts_utc', ''),
                            'event': event,
                            'bar_time': hour_min,
                            'bar_chicago': bar_chicago,
                            'bar_date': bar_date
                        })
                except:
                    pass
    except:
        pass

print(f"Found {len(all_bar_events)} bar events from 2026-01-16")
print()

if all_bar_events:
    # Group by hour
    bars_by_hour = {}
    for bar in all_bar_events:
        hour = bar['bar_time'][:2]  # HH
        if hour not in bars_by_hour:
            bars_by_hour[hour] = []
        bars_by_hour[hour].append(bar)
    
    print("Bars from 2026-01-16 by hour:")
    print("-" * 80)
    for hour in sorted(bars_by_hour.keys()):
        bars = bars_by_hour[hour]
        print(f"{hour}:00 - {len(bars)} bars")
        
        # Show first and last bar in this hour
        if bars:
            times = sorted([b['bar_time'] for b in bars])
            print(f"  First: {times[0]}, Last: {times[-1]}")
            print(f"  Events: {set(b['event'] for b in bars)}")
    
    # Check specifically for 10:40
    bars_1040 = [b for b in all_bar_events if '10:40' in b['bar_time'] or '10:39' in b['bar_time'] or '10:41' in b['bar_time']]
    if bars_1040:
        print(f"\n{'=' * 80}")
        print(f"BARS AROUND 10:40:")
        print("=" * 80)
        for bar in bars_1040:
            print(f"  [{bar['timestamp'][:19]}] {bar['event']} | {bar['bar_time']}")
    else:
        print(f"\n{'=' * 80}")
        print("NO BARS FOUND AROUND 10:40")
        print("=" * 80)
        print("\nThis means bars from 10:40 AM on 2026-01-16 have NOT arrived yet.")
        print("Possible reasons:")
        print("  1. NinjaTrader playback hasn't reached 10:40 AM yet")
        print("  2. Historical data doesn't include 10:40 AM")
        print("  3. Bars are arriving but not being logged (unlikely)")
        
        # Show what time range we have
        if all_bar_events:
            times = sorted([b['bar_time'] for b in all_bar_events])
            print(f"\nTime range of bars received:")
            print(f"  Earliest: {times[0]}")
            print(f"  Latest: {times[-1]}")
            print(f"  Total bars: {len(times)}")
else:
    print("No bars from 2026-01-16 found at all!")
    print("Only 1 bar was accepted (midnight), and no others have arrived.")
