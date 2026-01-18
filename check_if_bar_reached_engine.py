import json
from pathlib import Path

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING IF BAR REACHED ENGINE OnBar()")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Look for ENGINE_BAR_HEARTBEAT events - these are logged INSIDE OnBar()
# If we see these, the bar reached OnBar()
print("Looking for ENGINE_BAR_HEARTBEAT events (logged inside OnBar())...")
print()

heartbeat_events = []
for line in lines[-5000:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        if event == 'ENGINE_BAR_HEARTBEAT':
            payload = entry.get('data', {}).get('payload', {})
            heartbeat_events.append({
                'timestamp': entry.get('ts_utc', ''),
                'bar_utc': payload.get('utc_time', ''),
                'bar_chicago': payload.get('chicago_time', ''),
                'instrument': payload.get('instrument', '')
            })
    except:
        pass

print(f"Found {len(heartbeat_events)} ENGINE_BAR_HEARTBEAT events")
print()

if heartbeat_events:
    print("Recent ENGINE_BAR_HEARTBEAT events (bars that reached OnBar()):")
    print("-" * 80)
    for i, hb in enumerate(heartbeat_events[-20:], 1):
        print(f"{i}. [{hb['timestamp']}]")
        print(f"   Bar UTC: {hb['bar_utc']}")
        print(f"   Bar Chicago: {hb['bar_chicago']}")
        print(f"   Instrument: {hb['instrument']}")
        print()
    
    # Check if any are from 2026-01-16
    bars_from_16 = [hb for hb in heartbeat_events if '2026-01-16' in hb.get('bar_utc', '') or '2026-01-16' in hb.get('bar_chicago', '')]
    if bars_from_16:
        print(f"\n{'=' * 80}")
        print(f"FOUND {len(bars_from_16)} BARS FROM 2026-01-16 THAT REACHED OnBar()!")
        print("=" * 80)
        for bar in bars_from_16[:10]:
            print(f"  [{bar['timestamp']}] UTC: {bar['bar_utc']} | Chicago: {bar['bar_chicago']}")
    else:
        print("\nNo bars from 2026-01-16 found in ENGINE_BAR_HEARTBEAT events")
        print("This means bars from 2026-01-16 are NOT reaching OnBar()")
else:
    print("No ENGINE_BAR_HEARTBEAT events found")
    print("This suggests diagnostic logs might be disabled, or bars aren't reaching OnBar()")

# Also check for BAR_DATE_MISMATCH events from 2026-01-16 when trading date is 2026-01-16
print(f"\n{'=' * 80}")
print("CHECKING BAR_DATE_MISMATCH EVENTS FROM 2026-01-16")
print("=" * 80)

mismatch_from_16 = []
for line in lines[-5000:]:
    try:
        entry = json.loads(line)
        if entry.get('event') == 'BAR_DATE_MISMATCH':
            payload = entry.get('data', {}).get('payload', {})
            bar_date = payload.get('bar_trading_date', '')
            locked_date = payload.get('locked_trading_date', '')
            
            if bar_date == '2026-01-16' and locked_date == '2026-01-16':
                # This would be weird - bar date matches locked date but still mismatched?
                mismatch_from_16.append({
                    'timestamp': entry.get('ts_utc', ''),
                    'bar_utc': payload.get('bar_timestamp_utc', ''),
                    'bar_chicago': payload.get('bar_timestamp_chicago', ''),
                    'bar_date': bar_date,
                    'locked_date': locked_date
                })
    except:
        pass

if mismatch_from_16:
    print(f"WARNING: Found {len(mismatch_from_16)} BAR_DATE_MISMATCH events where bar date == locked date == 2026-01-16!")
    print("This would indicate a bug in date comparison logic")
    for mm in mismatch_from_16[:5]:
        print(f"  [{mm['timestamp']}] Bar: {mm['bar_chicago']} | Bar Date: {mm['bar_date']} | Locked: {mm['locked_date']}")
else:
    print("No mismatches found where bar date matches locked date (good)")
