import json
from pathlib import Path
from datetime import datetime, timezone

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING NINJATRADER BAR BEHAVIOR")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Find ENGINE_START events
start_events = []
for line in lines:
    try:
        entry = json.loads(line)
        if entry.get('event') == 'ENGINE_START':
            start_events.append({
                'timestamp': entry.get('ts_utc', ''),
                'entry': entry
            })
    except:
        pass

print(f"Found {len(start_events)} ENGINE_START events")
print()

if start_events:
    # Check most recent start
    latest_start = start_events[-1]
    start_time = latest_start['timestamp']
    print(f"Most recent ENGINE_START: {start_time}")
    print()
    
    # Parse start time
    try:
        start_dt = datetime.fromisoformat(start_time.replace('Z', '+00:00'))
        print(f"Start time (UTC): {start_dt}")
        print(f"Start time (Chicago): {start_dt.astimezone(timezone(offset=-6*3600))}")
        print()
    except:
        pass
    
    # Find first bar received after this start
    print("=" * 80)
    print("BARS RECEIVED AFTER ENGINE_START:")
    print("=" * 80)
    
    bars_after_start = []
    for line in lines:
        try:
            entry = json.loads(line)
            ts = entry.get('ts_utc', '')
            if ts < start_time:
                continue
            
            event = entry.get('event', '')
            if event in ['BAR_ACCEPTED', 'BAR_DATE_MISMATCH', 'ENGINE_BAR_HEARTBEAT']:
                payload = entry.get('data', {}).get('payload', {})
                bar_utc = payload.get('bar_timestamp_utc', '') or payload.get('utc_time', '')
                bar_chicago = payload.get('bar_timestamp_chicago', '') or payload.get('chicago_time', '')
                
                if bar_utc or bar_chicago:
                    bars_after_start.append({
                        'event_time': ts,
                        'bar_utc': bar_utc,
                        'bar_chicago': bar_chicago,
                        'event': event
                    })
        except:
            pass
    
    if bars_after_start:
        print(f"Found {len(bars_after_start)} bar events after ENGINE_START")
        print()
        
        # Sort by bar time (not event time)
        bars_sorted = []
        for bar in bars_after_start[:100]:  # First 100
            try:
                if bar['bar_chicago']:
                    bar_dt = datetime.fromisoformat(bar['bar_chicago'].replace('Z', '+00:00'))
                    bars_sorted.append((bar_dt, bar))
            except:
                pass
        
        bars_sorted.sort(key=lambda x: x[0])
        
        if bars_sorted:
            print("First 20 bars received (sorted by bar time):")
            print("-" * 80)
            for i, (bar_dt, bar) in enumerate(bars_sorted[:20], 1):
                event_dt = datetime.fromisoformat(bar['event_time'].replace('Z', '+00:00'))
                time_diff = (event_dt - start_dt).total_seconds()
                
                print(f"{i}. Bar time: {bar_dt} | Event time: {bar['event_time'][:19]} | Delay: {time_diff:.1f}s | {bar['event']}")
            
            # Check if first bar is before or after start time
            first_bar_time = bars_sorted[0][0]
            print()
            print("=" * 80)
            print("ANALYSIS:")
            print("=" * 80)
            
            if first_bar_time < start_dt:
                print("YES - NinjaTrader IS sending historical bars!")
                print(f"  First bar time: {first_bar_time}")
                print(f"  Strategy start: {start_dt}")
                print(f"  Difference: {(start_dt - first_bar_time).total_seconds()/60:.1f} minutes")
                print()
                print("This means:")
                print("  - NinjaTrader sends bars from the beginning of the day")
                print("  - File pre-hydration is REDUNDANT")
                print("  - We just need to buffer bars starting from ARMED state")
            else:
                print("NO - NinjaTrader is NOT sending historical bars")
                print(f"  First bar time: {first_bar_time}")
                print(f"  Strategy start: {start_dt}")
                print(f"  Difference: {(first_bar_time - start_dt).total_seconds()/60:.1f} minutes")
                print()
                print("This means:")
                print("  - NinjaTrader only sends bars going forward")
                print("  - File pre-hydration IS NEEDED for early bars")
                print("  - We need to load historical bars from files")
        else:
            print("Could not parse bar times")
    else:
        print("No bars found after ENGINE_START")
        print("This suggests bars aren't being received or logged")
