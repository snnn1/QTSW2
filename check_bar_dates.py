#!/usr/bin/env python3
"""Check bar dates and what's causing rollover spam"""
import json
import glob
from datetime import datetime, timezone, timedelta
from collections import Counter

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("BAR DATE ANALYSIS")
    print("=" * 80)
    
    now_utc = datetime.now(timezone.utc)
    five_minutes_ago = now_utc - timedelta(minutes=5)
    
    # Get all events
    all_events = []
    for log_file in log_files:
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        entry = json.loads(line)
                        all_events.append(entry)
                    except:
                        continue
        except:
            continue
    
    # Get recent rollover events with details
    rollover_events = []
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= five_minutes_ago and entry.get('event') == 'TRADING_DAY_ROLLOVER':
                    rollover_events.append((ts, entry))
        except:
            continue
    
    rollover_events.sort(key=lambda x: x[0])
    
    if rollover_events:
        print(f"\nFound {len(rollover_events)} rollover events")
        print("\nFirst 5 rollover events with full data:")
        for ts, entry in rollover_events[:5]:
            data = entry.get('data', {})
            print(f"\n  [{ts.strftime('%H:%M:%S')}]")
            print(f"    Previous: {data.get('previous_trading_date', 'NONE')}")
            print(f"    New: {data.get('new_trading_date', 'NONE')}")
            print(f"    Bar UTC: {data.get('bar_timestamp_utc', 'NONE')}")
            print(f"    Bar Chicago: {data.get('bar_timestamp_chicago', 'NONE')}")
    
    # Check for bar events
    bar_events = []
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= five_minutes_ago:
                    event = entry.get('event', '')
                    if 'BAR' in event.upper() or 'bar' in str(entry.get('data', {})).lower():
                        bar_events.append((ts, entry))
        except:
            continue
    
    bar_events.sort(key=lambda x: x[0])
    
    print(f"\n" + "=" * 80)
    print(f"BAR EVENTS")
    print("=" * 80)
    print(f"Found {len(bar_events)} bar-related events in last 5 minutes")
    
    if bar_events:
        print("\nFirst 10 bar events:")
        for ts, entry in bar_events[:10]:
            event = entry.get('event', '')
            data = entry.get('data', {})
            print(f"  [{ts.strftime('%H:%M:%S')}] {event}")
            if 'bar' in data:
                print(f"    Bar data: {data.get('bar', {})}")
    
    # Check if _activeTradingDate is being set
    print(f"\n" + "=" * 80)
    print(f"ENGINE STATE")
    print("=" * 80)
    
    # Look for ENGINE_START to see when engine started
    engine_starts = []
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= five_minutes_ago and entry.get('event') == 'ENGINE_START':
                    engine_starts.append(ts)
        except:
            continue
    
    if engine_starts:
        print(f"\nENGINE_START events: {len(engine_starts)}")
        for ts in engine_starts:
            print(f"  [{ts.strftime('%H:%M:%S')}]")
    
    # The issue: if _activeTradingDate is null, every bar will trigger rollover
    # Check if we're getting bars before _activeTradingDate is set
    if engine_starts and rollover_events:
        first_start = min(engine_starts)
        first_rollover = rollover_events[0][0]
        
        if first_rollover < first_start + timedelta(seconds=5):
            print(f"\n[ISSUE] Rollovers happening immediately after ENGINE_START")
            print(f"        This suggests _activeTradingDate is null and bars are triggering rollovers")

if __name__ == '__main__':
    main()
