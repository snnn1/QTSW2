import json
from pathlib import Path
from collections import defaultdict

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING FOR BAR_ACCEPTED EVENTS")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Find trading date
trading_date = None
for line in reversed(lines):
    try:
        entry = json.loads(line)
        if entry.get('event') == 'TRADING_DATE_LOCKED':
            payload = entry.get('data', {}).get('payload', {})
            if payload.get('source') == 'TIMETABLE':
                trading_date = payload.get('trading_date')
                break
    except:
        pass

print(f"Trading Date (locked): {trading_date}")
print()

# Find BAR_ACCEPTED events
accepted_bars = []
for line in lines[-2000:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        
        if event == 'BAR_ACCEPTED':
            payload = entry.get('data', {}).get('payload', {})
            accepted_bars.append({
                'timestamp': entry.get('ts_utc', ''),
                'bar_utc': payload.get('bar_timestamp_utc', ''),
                'bar_chicago': payload.get('bar_timestamp_chicago', ''),
                'bar_date': payload.get('bar_trading_date', ''),
                'locked_date': payload.get('locked_trading_date', ''),
                'instrument': payload.get('instrument', '')
            })
    except:
        pass

print(f"Found {len(accepted_bars)} BAR_ACCEPTED events")
print()

if accepted_bars:
    print("=" * 80)
    print("BAR_ACCEPTED EVENTS:")
    print("=" * 80)
    
    # Group by date
    bars_by_date = defaultdict(list)
    for bar in accepted_bars:
        bars_by_date[bar['bar_date']].append(bar)
    
    for date in sorted(bars_by_date.keys()):
        bars = bars_by_date[date]
        match_status = "MATCHES" if date == trading_date else "WRONG DATE"
        print(f"\n{date}: {len(bars)} bars ({match_status})")
        
        if bars:
            print(f"  First bar: {bars[0]['bar_chicago']}")
            print(f"  Last bar: {bars[-1]['bar_chicago']}")
            print(f"  First timestamp: {bars[0]['timestamp']}")
            print(f"  Last timestamp: {bars[-1]['timestamp']}")
    
    # Show most recent accepted bars
    print(f"\n{'=' * 80}")
    print("MOST RECENT 10 BAR_ACCEPTED EVENTS:")
    print("=" * 80)
    
    for i, bar in enumerate(accepted_bars[-10:], 1):
        print(f"{i}. [{bar['timestamp']}]")
        print(f"   Bar UTC: {bar['bar_utc']}")
        print(f"   Bar Chicago: {bar['bar_chicago']}")
        print(f"   Bar Date: {bar['bar_date']} | Locked Date: {bar['locked_date']}")
        print(f"   Instrument: {bar['instrument']}")
        print()
    
    # Check specifically for 08:25:00 bar
    bar_0825 = [b for b in accepted_bars if '08:25' in b.get('bar_chicago', '') or '14:25' in b.get('bar_utc', '')]
    if bar_0825:
        print(f"{'=' * 80}")
        print(f"BAR AT 08:25:00 CHICAGO (14:25 UTC) - ACCEPTED:")
        print("=" * 80)
        for bar in bar_0825:
            print(f"  [{bar['timestamp']}]")
            print(f"     Bar UTC: {bar['bar_utc']}")
            print(f"     Bar Chicago: {bar['bar_chicago']}")
            print(f"     Bar Date: {bar['bar_date']}")
            print(f"     Locked Date: {bar['locked_date']}")
            print(f"     Instrument: {bar['instrument']}")
    else:
        print(f"\nNo BAR_ACCEPTED events found for bar at 08:25:00")
        print("This could mean:")
        print("  1. The bar hasn't arrived yet")
        print("  2. The bar was rejected (check BAR_DATE_MISMATCH)")
        print("  3. The code hasn't been synced/restarted yet")
else:
    print("No BAR_ACCEPTED events found")
    print("\nThis could mean:")
    print("  1. No bars from the trading date have arrived yet")
    print("  2. The code hasn't been synced/restarted yet")
    print("  3. All bars are being rejected (check BAR_DATE_MISMATCH)")

# Also check recent BAR_DATE_MISMATCH to see what's being rejected
print(f"\n{'=' * 80}")
print("RECENT BAR_DATE_MISMATCH EVENTS (last 10):")
print("=" * 80)

mismatch_count = 0
for line in reversed(lines[-1000:]):
    try:
        entry = json.loads(line)
        if entry.get('event') == 'BAR_DATE_MISMATCH':
            mismatch_count += 1
            if mismatch_count <= 10:
                payload = entry.get('data', {}).get('payload', {})
                print(f"  [{entry.get('ts_utc', '')[:19]}]")
                print(f"     Bar Date: {payload.get('bar_trading_date', '')} | Locked Date: {payload.get('locked_trading_date', '')}")
                print(f"     Bar Chicago: {payload.get('bar_timestamp_chicago', '')[:19]}")
                print()
    except:
        pass

if mismatch_count == 0:
    print("  No BAR_DATE_MISMATCH events found")
