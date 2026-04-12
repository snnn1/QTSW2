#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check data stall status from watchdog"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

print("=" * 100)
print("DATA STALL DIAGNOSTICS")
print("=" * 100)
print()

# Check watchdog state file
state_file = Path("automation/logs/orchestrator_state.json")
if not state_file.exists():
    print(f"[ERROR] Watchdog state file not found: {state_file}")
    exit(1)

try:
    with open(state_file, 'r') as f:
        state = json.load(f)
except Exception as e:
    print(f"[ERROR] Failed to read state file: {e}")
    exit(1)

# Check data stall status
data_stall_detected = state.get('data_stall_detected', {})
worst_last_bar_age_seconds = state.get('worst_last_bar_age_seconds')
market_open = state.get('market_open', False)
data_status = state.get('data_status', 'UNKNOWN')

print(f"Market Open: {market_open}")
print(f"Data Status: {data_status}")
print(f"Worst Last Bar Age: {worst_last_bar_age_seconds:.1f} seconds" if worst_last_bar_age_seconds else "Worst Last Bar Age: No bars received")
print()

if data_stall_detected:
    print(f"[WARNING] DATA STALL DETECTED for {len(data_stall_detected)} instrument(s):")
    print("-" * 100)
    
    now = datetime.now(timezone.utc)
    
    for instrument, info in data_stall_detected.items():
        stall_detected = info.get('stall_detected', False)
        last_bar_chicago = info.get('last_bar_chicago')
        market_open_flag = info.get('market_open', False)
        
        print(f"  Instrument: {instrument}")
        print(f"    Stall Detected: {stall_detected}")
        print(f"    Market Open: {market_open_flag}")
        
        if last_bar_chicago:
            try:
                last_bar_utc = datetime.fromisoformat(last_bar_chicago.replace('Z', '+00:00'))
                if last_bar_utc.tzinfo is None:
                    last_bar_utc = last_bar_utc.replace(tzinfo=timezone.utc)
                age_seconds = (now - last_bar_utc).total_seconds()
                print(f"    Last Bar: {last_bar_chicago}")
                print(f"    Age: {age_seconds:.1f} seconds ({age_seconds/60:.1f} minutes)")
            except:
                print(f"    Last Bar: {last_bar_chicago}")
        else:
            print(f"    Last Bar: None (no bars received yet)")
        print()
else:
    print("[OK] No data stalls detected")
    print()

# Check recent bar events from robot logs
print("Recent Bar Events (last 5 minutes):")
print("-" * 100)

recent = datetime.now(timezone.utc) - timedelta(minutes=5)
bar_events = []

for log_file in Path("logs/robot").glob("*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                try:
                    e = json.loads(line.strip())
                    ts_str = e.get('ts_utc', '')
                    if ts_str:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                        if ts >= recent:
                            event_type = e.get('event', '')
                            if any(k in event_type.upper() for k in ['BAR', 'ONBARUPDATE']):
                                bar_events.append(e)
                except:
                    pass
    except:
        pass

if bar_events:
    # Sort by timestamp
    bar_events.sort(key=lambda e: e.get('ts_utc', ''), reverse=True)
    
    print(f"Found {len(bar_events)} bar events in last 5 minutes:")
    print()
    
    # Group by instrument
    by_instrument = {}
    for e in bar_events[:20]:  # Show last 20
        data = e.get('data', {})
        instrument = data.get('instrument') or data.get('execution_instrument') or 'UNKNOWN'
        if instrument not in by_instrument:
            by_instrument[instrument] = []
        by_instrument[instrument].append(e)
    
    for instrument, events in sorted(by_instrument.items()):
        print(f"  {instrument}: {len(events)} events")
        for e in events[:3]:  # Show last 3 per instrument
            ts = e.get('ts_utc', '')[:19]
            event_type = e.get('event', '')
            print(f"    [{ts}] {event_type}")
else:
    print("No bar events found in last 5 minutes")
    print("This could indicate:")
    print("  - Market is closed")
    print("  - Robot is not receiving data")
    print("  - Robot is not running")
    print()

# Check engine tick events
print("Engine Activity (last 5 minutes):")
print("-" * 100)

tick_events = []
for log_file in Path("logs/robot").glob("*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                try:
                    e = json.loads(line.strip())
                    ts_str = e.get('ts_utc', '')
                    if ts_str:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                        if ts >= recent:
                            event_type = e.get('event', '')
                            if 'ENGINE_TICK' in event_type.upper():
                                tick_events.append(e)
                except:
                    pass
    except:
        pass

if tick_events:
    tick_events.sort(key=lambda e: e.get('ts_utc', ''), reverse=True)
    print(f"Found {len(tick_events)} engine tick events in last 5 minutes")
    print(f"Most recent: {tick_events[0].get('ts_utc', '')[:19]}")
else:
    print("[WARNING] No engine tick events in last 5 minutes")
    print("This could indicate the robot is not running or not processing ticks")
    print()

print("=" * 100)
print("RECOMMENDATIONS")
print("=" * 100)
print()

if data_stall_detected:
    print("1. Check NinjaTrader connection:")
    print("   - Is NinjaTrader connected to data feed?")
    print("   - Check connection status in NinjaTrader")
    print()
    print("2. Check if market is open:")
    print("   - Market hours: 8:00 AM - 4:00 PM Chicago time")
    print("   - Data stalls are only flagged when market is open")
    print()
    print("3. Check robot logs for errors:")
    print("   - Look for connection errors")
    print("   - Look for data feed errors")
    print()
    print("4. Restart NinjaTrader if needed:")
    print("   - Sometimes data feed needs reconnection")
    print()
else:
    print("[OK] No action needed - data is flowing normally")
    print()
