import json
from pathlib import Path
from datetime import datetime, timezone

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING 10:40 BARS WITH CORRECT TRADING DATE")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Find when trading date was locked to 2026-01-16
lock_time = None
for line in reversed(lines):
    try:
        entry = json.loads(line)
        if entry.get('event') == 'TRADING_DATE_LOCKED':
            payload = entry.get('data', {}).get('payload', {})
            if payload.get('trading_date') == '2026-01-16' and payload.get('source') == 'TIMETABLE':
                lock_time = entry.get('ts_utc', '')
                print(f"Trading date locked to 2026-01-16 at: {lock_time}")
                break
    except:
        pass

if not lock_time:
    print("Could not find when trading date was locked to 2026-01-16")
    exit(1)

print()

# Find bars from 10:40 AFTER the lock
bars_1040_after_lock = []
for line in lines:
    try:
        entry = json.loads(line)
        ts = entry.get('ts_utc', '')
        
        # Only check events after lock
        if ts < lock_time:
            continue
        
        event = entry.get('event', '')
        payload = entry.get('data', {}).get('payload', {})
        
        bar_chicago = payload.get('bar_timestamp_chicago', '')
        bar_date = payload.get('bar_trading_date', '')
        
        if bar_chicago and ('10:40' in bar_chicago or '10:39' in bar_chicago or '10:41' in bar_chicago):
            bars_1040_after_lock.append({
                'timestamp': ts,
                'event': event,
                'bar_chicago': bar_chicago,
                'bar_date': bar_date,
                'locked_date': payload.get('locked_trading_date', '')
            })
    except:
        pass

print(f"Found {len(bars_1040_after_lock)} bars around 10:40 AFTER trading date was locked to 2026-01-16")
print()

if bars_1040_after_lock:
    print("Bars around 10:40 after lock:")
    print("-" * 80)
    for i, bar in enumerate(bars_1040_after_lock, 1):
        print(f"{i}. [{bar['timestamp'][:19]}] {bar['event']}")
        print(f"   Bar Chicago: {bar['bar_chicago'][:19]}")
        print(f"   Bar Date: {bar['bar_date']} | Locked Date: {bar['locked_date']}")
        
        if bar['event'] == 'BAR_DATE_MISMATCH' and bar['bar_date'] == bar['locked_date']:
            print(f"   *** BUG: Date matches but still rejected! ***")
        elif bar['event'] == 'BAR_ACCEPTED':
            print(f"   ✓ ACCEPTED")
        print()
else:
    print("No bars around 10:40 found AFTER trading date was locked to 2026-01-16")
    print("\nThis means:")
    print("  1. Bars from 10:40 were already processed BEFORE the lock")
    print("  2. NinjaTrader playback hasn't reached 10:40 again yet")
    print("  3. Bars are not being replayed")
    
    # Check what bars ARE arriving after lock
    print(f"\n{'=' * 80}")
    print("BARS ARRIVING AFTER LOCK:")
    print("=" * 80)
    
    bars_after_lock = []
    for line in lines:
        try:
            entry = json.loads(line)
            ts = entry.get('ts_utc', '')
            if ts < lock_time:
                continue
            
            event = entry.get('event', '')
            if 'BAR' in event:
                payload = entry.get('data', {}).get('payload', {})
                bar_chicago = payload.get('bar_timestamp_chicago', '')
                bar_date = payload.get('bar_trading_date', '')
                
                if bar_chicago and bar_date == '2026-01-16':
                    bars_after_lock.append({
                        'timestamp': ts,
                        'event': event,
                        'bar_chicago': bar_chicago
                    })
        except:
            pass
    
    if bars_after_lock:
        print(f"Found {len(bars_after_lock)} bars from 2026-01-16 after lock")
        print("\nFirst 10:")
        for i, bar in enumerate(bars_after_lock[:10], 1):
            print(f"  {i}. [{bar['timestamp'][:19]}] {bar['event']} | {bar['bar_chicago'][:19]}")
        
        print("\nLast 10:")
        for i, bar in enumerate(bars_after_lock[-10:], 1):
            print(f"  {i}. [{bar['timestamp'][:19]}] {bar['event']} | {bar['bar_chicago'][:19]}")
    else:
        print("No bars from 2026-01-16 found after lock!")
