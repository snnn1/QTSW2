#!/usr/bin/env python3
"""Check rollover pattern to understand why it's happening repeatedly"""
import json
import glob
from datetime import datetime, timezone, timedelta
from collections import Counter

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("ROLLOVER PATTERN ANALYSIS")
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
    
    # Get rollover events with actual data
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
                    if event == 'TRADING_DAY_ROLLOVER':
                        data = entry.get('data', {})
                        payload = data.get('payload', {})
                        prev = payload.get('previous_trading_date', '')
                        new_date = payload.get('new_trading_date', '')
                        if prev and new_date:
                            rollovers.append((ts, prev, new_date, entry.get('data', {}).get('stream', 'UNKNOWN')))
        except:
            continue
    
    rollovers.sort(key=lambda x: x[0])
    
    print(f"\nFound {len(rollovers)} rollover events with dates")
    
    if rollovers:
        # Count date transitions
        transitions = Counter()
        for ts, prev, new_date, stream in rollovers:
            transitions[(prev, new_date)] += 1
        
        print("\nDate transitions:")
        for (prev, new_date), count in transitions.most_common():
            print(f"  {prev} -> {new_date}: {count} times")
        
        # Check if same transition happening repeatedly
        if len(transitions) == 1 and len(rollovers) > 10:
            print(f"\n[ISSUE] Same date transition ({prev} -> {new_date}) happening {len(rollovers)} times!")
            print("        This suggests UpdateTradingDate is being called repeatedly")
            print("        with the same date change, or streams are being recreated")
        
        # Show timeline
        print("\nFirst 10 rollovers by time:")
        for i, (ts, prev, new_date, stream) in enumerate(rollovers[:10]):
            print(f"  [{ts.strftime('%H:%M:%S.%f')[:12]}] {prev} -> {new_date} | Stream: {stream}")
        
        # Check if streams are being recreated
        streams = Counter()
        for ts, prev, new_date, stream in rollovers:
            streams[stream] += 1
        
        print(f"\nRollovers by stream:")
        for stream, count in streams.most_common():
            print(f"  {stream}: {count} rollovers")

if __name__ == '__main__':
    main()
