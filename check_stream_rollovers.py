#!/usr/bin/env python3
"""Check stream-level rollover events"""
import json
import glob
from datetime import datetime, timezone, timedelta
from collections import Counter

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("STREAM-LEVEL ROLLOVER CHECK")
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
    
    # Get rollover events (not ENGINE state)
    rollovers = []
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
                    if event == 'TRADING_DAY_ROLLOVER' and state != 'ENGINE':
                        rollovers.append((ts, entry))
        except:
            continue
    
    rollovers.sort(key=lambda x: x[0])
    
    print(f"\nFound {len(rollovers)} stream-level rollover events")
    
    if rollovers:
        print("\nFirst 5 stream rollover events:")
        for i, (ts, entry) in enumerate(rollovers[:5]):
            data = entry.get('data', {})
            print(f"\n[{i+1}] [{ts.strftime('%H:%M:%S')}]")
            print(f"  State: {entry.get('state', 'NONE')}")
            print(f"  Stream: {data.get('stream', 'NONE')}")
            print(f"  Trading Date: {entry.get('trading_date', 'NONE')}")
            print(f"  Previous: '{data.get('previous_trading_date', 'NONE')}'")
            print(f"  New: '{data.get('new_trading_date', 'NONE')}'")
            print(f"  State Reset: {data.get('state_reset_to', 'NONE')}")
            print(f"  Reason: {data.get('reason', 'NONE')}")
        
        # Count by stream
        streams = Counter()
        for ts, entry in rollovers:
            stream = entry.get('data', {}).get('stream', 'UNKNOWN')
            streams[stream] += 1
        
        print(f"\nRollovers by stream:")
        for stream, count in streams.most_common():
            print(f"  {stream}: {count}")
    
    # Check for TRADING_DATE_INITIALIZED
    initialized = []
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= two_minutes_ago and entry.get('event') == 'TRADING_DATE_INITIALIZED':
                    initialized.append((ts, entry))
        except:
            continue
    
    print(f"\n" + "=" * 80)
    print("TRADING_DATE_INITIALIZED")
    print("=" * 80)
    print(f"Found {len(initialized)} events")
    
    if initialized:
        print("\nFirst 3:")
        for ts, entry in initialized[:3]:
            data = entry.get('data', {})
            print(f"  [{ts.strftime('%H:%M:%S')}] Stream: {data.get('stream', 'NONE')}")

if __name__ == '__main__':
    main()
