#!/usr/bin/env python3
"""Check what's happening with bar dates"""
import json
import glob
from datetime import datetime, timezone, timedelta

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("BAR DATE ISSUE CHECK")
    print("=" * 80)
    
    now_utc = datetime.now(timezone.utc)
    two_minutes_ago = now_utc - timedelta(minutes=2)
    
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
    
    # Check ENGINE-level rollover events
    engine_rollovers = []
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= two_minutes_ago:
                    event = entry.get('event', '')
                    state = entry.get('state', '')
                    if event == 'TRADING_DAY_ROLLOVER' and state == 'ENGINE':
                        engine_rollovers.append((ts, entry))
        except:
            continue
    
    engine_rollovers.sort(key=lambda x: x[0])
    
    print(f"\nFound {len(engine_rollovers)} ENGINE-level rollover events")
    
    if engine_rollovers:
        print("\nFirst 3 ENGINE rollover events:")
        for i, (ts, entry) in enumerate(engine_rollovers[:3]):
            data = entry.get('data', {})
            print(f"\n[{i+1}] [{ts.strftime('%H:%M:%S')}]")
            print(f"  Previous: '{data.get('previous_trading_date', 'NONE')}'")
            print(f"  New: '{data.get('new_trading_date', 'NONE')}'")
            print(f"  Bar UTC: {data.get('bar_timestamp_utc', 'NONE')}")
            print(f"  Bar Chicago: {data.get('bar_timestamp_chicago', 'NONE')}")
            print(f"  Trading Date: {entry.get('trading_date', 'NONE')}")
    
    # Check if bars are being received
    bar_heartbeats = []
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= two_minutes_ago:
                    event = entry.get('event', '')
                    if 'BAR' in event.upper():
                        bar_heartbeats.append((ts, entry))
        except:
            continue
    
    print(f"\n" + "=" * 80)
    print("BAR EVENTS")
    print("=" * 80)
    print(f"Found {len(bar_heartbeats)} bar-related events")
    
    if bar_heartbeats:
        print("\nFirst 3 bar events:")
        for i, (ts, entry) in enumerate(bar_heartbeats[:3]):
            print(f"\n[{i+1}] [{ts.strftime('%H:%M:%S')}] {entry.get('event', '')}")
            data = entry.get('data', {})
            if 'bar' in data or 'chicago_time' in data:
                print(f"  Data: {json.dumps(data, indent=2)[:200]}")

if __name__ == '__main__':
    main()
