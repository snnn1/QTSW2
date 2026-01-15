#!/usr/bin/env python3
"""Check what's causing TRADING_DAY_ROLLOVER spam"""
import json
import glob
from datetime import datetime, timezone, timedelta
from collections import Counter

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("TRADING_DAY_ROLLOVER ANALYSIS")
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
    
    # Get recent rollover events
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
    
    print(f"\nFound {len(rollover_events)} TRADING_DAY_ROLLOVER events in last 5 minutes")
    
    if rollover_events:
        print("\nFirst 10 rollover events:")
        for ts, entry in rollover_events[:10]:
            data = entry.get('data', {})
            prev_date = data.get('previous_trading_date', '')
            new_date = data.get('new_trading_date', '')
            state_reset = data.get('state_reset_to', '')
            reason = data.get('reason', '')
            
            print(f"  [{ts.strftime('%H:%M:%S')}] {prev_date} -> {new_date} | Reset to: {state_reset}")
            if reason:
                print(f"    Reason: {reason}")
        
        # Count by stream
        streams = Counter()
        for ts, entry in rollover_events:
            data = entry.get('data', {})
            stream = data.get('stream', 'UNKNOWN')
            streams[stream] += 1
        
        print(f"\nRollover events by stream:")
        for stream, count in streams.most_common():
            print(f"  {stream}: {count} events")
        
        # Check if dates are changing
        date_changes = set()
        for ts, entry in rollover_events:
            data = entry.get('data', {})
            prev_date = data.get('previous_trading_date', '')
            new_date = data.get('new_trading_date', '')
            if prev_date and new_date:
                date_changes.add((prev_date, new_date))
        
        print(f"\nUnique date changes: {len(date_changes)}")
        for prev, new in sorted(date_changes)[:10]:
            print(f"  {prev} -> {new}")
        
        if len(date_changes) == 1 and len(rollover_events) > 100:
            print(f"\n[ISSUE] Same date change repeated {len(rollover_events)} times!")
            print("        This suggests UpdateTradingDate() is being called in a loop")
    
    # Check what events happen right before rollover
    print("\n" + "=" * 80)
    print("EVENTS BEFORE ROLLOVER")
    print("=" * 80)
    
    # Get all recent events
    recent_events = []
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= five_minutes_ago:
                    recent_events.append((ts, entry))
        except:
            continue
    
    recent_events.sort(key=lambda x: x[0])
    
    # Find events right before first rollover
    if rollover_events:
        first_rollover_ts = rollover_events[0][0]
        before_rollover = [(ts, e) for ts, e in recent_events if ts < first_rollover_ts and (ts - first_rollover_ts).total_seconds() > -10]
        
        print(f"\nEvents in 10 seconds before first rollover:")
        for ts, entry in before_rollover[-10:]:
            event = entry.get('event', '')
            print(f"  [{ts.strftime('%H:%M:%S')}] {event}")

if __name__ == '__main__':
    main()
