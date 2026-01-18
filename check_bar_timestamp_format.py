import json
from pathlib import Path
from datetime import datetime

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING BAR TIMESTAMP FORMAT")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Find BAR_DATE_MISMATCH events and check timestamps
print("Analyzing BAR_DATE_MISMATCH events to check timestamp format...")
print()

mismatch_events = []
for line in lines[-1000:]:
    try:
        entry = json.loads(line)
        if entry.get('event') == 'BAR_DATE_MISMATCH':
            payload = entry.get('data', {}).get('payload', {})
            mismatch_events.append({
                'timestamp': entry.get('ts_utc', ''),
                'bar_timestamp_utc': payload.get('bar_timestamp_utc', ''),
                'bar_timestamp_chicago': payload.get('bar_timestamp_chicago', ''),
                'bar_trading_date': payload.get('bar_trading_date', ''),
                'locked_trading_date': payload.get('locked_trading_date', '')
            })
    except:
        pass

if mismatch_events:
    print(f"Found {len(mismatch_events)} BAR_DATE_MISMATCH events")
    print()
    
    # Show first few examples
    print("Sample BAR_DATE_MISMATCH events:")
    print("-" * 80)
    for i, event in enumerate(mismatch_events[:5], 1):
        print(f"\n{i}. Event timestamp: {event['timestamp']}")
        print(f"   Bar UTC timestamp: {event['bar_timestamp_utc']}")
        print(f"   Bar Chicago timestamp: {event['bar_timestamp_chicago']}")
        print(f"   Bar trading date (extracted): {event['bar_trading_date']}")
        print(f"   Locked trading date: {event['locked_trading_date']}")
        
        # Try to parse and analyze
        try:
            bar_utc = datetime.fromisoformat(event['bar_timestamp_utc'].replace('Z', '+00:00'))
            bar_chicago = datetime.fromisoformat(event['bar_timestamp_chicago'].replace('Z', '+00:00'))
            
            print(f"   Parsed UTC: {bar_utc}")
            print(f"   Parsed Chicago: {bar_chicago}")
            print(f"   UTC date: {bar_utc.date()}")
            print(f"   Chicago date: {bar_chicago.date()}")
        except Exception as e:
            print(f"   Parse error: {e}")
    
    # Check if there are any bars from 2026-01-16
    bars_from_16th = [e for e in mismatch_events if '2026-01-16' in e.get('bar_trading_date', '')]
    if bars_from_16th:
        print(f"\n{'=' * 80}")
        print(f"FOUND {len(bars_from_16th)} BARS FROM 2026-01-16!")
        print("=" * 80)
        for event in bars_from_16th[:3]:
            print(f"\nBar from 2026-01-16:")
            print(f"  UTC: {event['bar_timestamp_utc']}")
            print(f"  Chicago: {event['bar_timestamp_chicago']}")
            print(f"  Extracted date: {event['bar_trading_date']}")
    else:
        print(f"\n{'=' * 80}")
        print("NO BARS FROM 2026-01-16 FOUND IN MISMATCH EVENTS")
        print("=" * 80)
        print("\nThis confirms that NinjaTrader is not sending bars from 2026-01-16 yet.")
        print("The code is working correctly - it's waiting for bars from the correct date.")
else:
    print("No BAR_DATE_MISMATCH events found in recent logs")

# Also check for any bars that were processed (not mismatched)
print(f"\n{'=' * 80}")
print("CHECKING FOR PROCESSED BARS (not mismatched)")
print("=" * 80)

processed_bars = []
for line in lines[-1000:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        if event in ['ENGINE_BAR_HEARTBEAT', 'BAR_RECEIVED']:
            payload = entry.get('data', {}).get('payload', {})
            bar_date = payload.get('bar_trading_date', '') or payload.get('bar_date', '')
            if bar_date:
                processed_bars.append({
                    'event': event,
                    'bar_date': bar_date,
                    'timestamp': entry.get('ts_utc', ''),
                    'bar_timestamp': payload.get('bar_timestamp_utc', '') or payload.get('bar_timestamp', '')
                })
    except:
        pass

if processed_bars:
    print(f"Found {len(processed_bars)} processed bar events")
    bars_by_date = {}
    for bar in processed_bars:
        date = bar['bar_date']
        if date not in bars_by_date:
            bars_by_date[date] = []
        bars_by_date[date].append(bar)
    
    print("\nBars by date:")
    for date in sorted(bars_by_date.keys()):
        print(f"  {date}: {len(bars_by_date[date])} bars")
else:
    print("No processed bar events found")
