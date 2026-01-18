import json
from pathlib import Path

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING BARS FROM 2026-01-16 AFTER TRADING DATE LOCKED")
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

# Find all bar events AFTER the lock
print("Looking for bar events AFTER trading date was locked...")
print()

bars_after_lock = []
for line in lines:
    try:
        entry = json.loads(line)
        ts = entry.get('ts_utc', '')
        
        # Only check events after lock
        if ts < lock_time:
            continue
        
        event = entry.get('event', '')
        payload = entry.get('data', {}).get('payload', {})
        
        if event == 'BAR_DATE_MISMATCH':
            bar_date = payload.get('bar_trading_date', '')
            locked_date = payload.get('locked_trading_date', '')
            bar_utc = payload.get('bar_timestamp_utc', '')
            bar_chicago = payload.get('bar_timestamp_chicago', '')
            
            bars_after_lock.append({
                'timestamp': ts,
                'event': 'BAR_DATE_MISMATCH',
                'bar_date': bar_date,
                'locked_date': locked_date,
                'bar_utc': bar_utc,
                'bar_chicago': bar_chicago
            })
        elif event == 'ENGINE_BAR_HEARTBEAT':
            bar_utc = payload.get('utc_time', '')
            bar_chicago = payload.get('chicago_time', '')
            if '2026-01-16' in bar_utc or '2026-01-16' in bar_chicago:
                bars_after_lock.append({
                    'timestamp': ts,
                    'event': 'ENGINE_BAR_HEARTBEAT',
                    'bar_utc': bar_utc,
                    'bar_chicago': bar_chicago
                })
    except:
        pass

print(f"Found {len(bars_after_lock)} bar events after lock")
print()

# Filter bars from 2026-01-16
bars_from_16 = [b for b in bars_after_lock if b.get('bar_date') == '2026-01-16' or '2026-01-16' in b.get('bar_utc', '') or '2026-01-16' in b.get('bar_chicago', '')]

if bars_from_16:
    print(f"{'=' * 80}")
    print(f"BARS FROM 2026-01-16 AFTER LOCK:")
    print("=" * 80)
    
    # Group by event type
    mismatches = [b for b in bars_from_16 if b['event'] == 'BAR_DATE_MISMATCH']
    heartbeats = [b for b in bars_from_16 if b['event'] == 'ENGINE_BAR_HEARTBEAT']
    
    print(f"\nBAR_DATE_MISMATCH events: {len(mismatches)}")
    if mismatches:
        print("Sample mismatches:")
        for i, bar in enumerate(mismatches[:10], 1):
            print(f"  {i}. [{bar['timestamp']}]")
            print(f"     Bar Date: {bar['bar_date']} | Locked Date: {bar['locked_date']}")
            print(f"     Bar UTC: {bar['bar_utc']}")
            print(f"     Bar Chicago: {bar['bar_chicago']}")
            if bar['bar_date'] == bar['locked_date']:
                print(f"     *** WARNING: Bar date matches locked date but still mismatched! ***")
    
    print(f"\nENGINE_BAR_HEARTBEAT events: {len(heartbeats)}")
    if heartbeats:
        print("Sample heartbeats (bars that were processed):")
        for i, bar in enumerate(heartbeats[:10], 1):
            print(f"  {i}. [{bar['timestamp']}]")
            print(f"     Bar UTC: {bar['bar_utc']}")
            print(f"     Bar Chicago: {bar['bar_chicago']}")
    
    # Check specifically for 08:25:00 bar
    bar_0825 = [b for b in bars_from_16 if '08:25' in b.get('bar_chicago', '') or '14:25' in b.get('bar_utc', '')]
    if bar_0825:
        print(f"\n{'=' * 80}")
        print(f"BAR AT 08:25:00 CHICAGO (14:25 UTC):")
        print("=" * 80)
        for bar in bar_0825:
            print(f"  [{bar['timestamp']}] {bar['event']}")
            print(f"     Bar UTC: {bar.get('bar_utc', 'N/A')}")
            print(f"     Bar Chicago: {bar.get('bar_chicago', 'N/A')}")
            print(f"     Bar Date: {bar.get('bar_date', 'N/A')}")
            print(f"     Locked Date: {bar.get('locked_date', 'N/A')}")
            if bar['event'] == 'BAR_DATE_MISMATCH' and bar.get('bar_date') == bar.get('locked_date'):
                print(f"     *** BUG: Date matches but still rejected! ***")
else:
    print("No bars from 2026-01-16 found after lock")
    print("\nThis means bars from 2026-01-16 are not arriving, or they're being silently ignored")
