#!/usr/bin/env python3
"""Check if TRADING_DATE_BACKWARD events are appearing"""
import json
import glob
from datetime import datetime, timezone, timedelta

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("BACKWARD DATE FIX CHECK")
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
    
    # Check for TRADING_DATE_BACKWARD events
    backward_events = []
    rollover_events = []
    
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= two_minutes_ago:
                    event = entry.get('event', '')
                    if event == 'TRADING_DATE_BACKWARD':
                        backward_events.append((ts, entry))
                    elif event == 'TRADING_DAY_ROLLOVER':
                        rollover_events.append((ts, entry))
        except:
            continue
    
    backward_events.sort(key=lambda x: x[0])
    rollover_events.sort(key=lambda x: x[0])
    
    print(f"\nTRADING_DATE_BACKWARD events: {len(backward_events)}")
    print(f"TRADING_DAY_ROLLOVER events: {len(rollover_events)}")
    
    if backward_events:
        print(f"\n[SUCCESS] Found {len(backward_events)} TRADING_DATE_BACKWARD events!")
        print("          The backward date fix is working")
        print("\n  First 5 backward events:")
        for i, (ts, entry) in enumerate(backward_events[:5]):
            data = entry.get('data', {})
            payload = data.get('payload', {})
            print(f"    [{ts.strftime('%H:%M:%S')}] {payload.get('previous_trading_date', '')} -> {payload.get('new_trading_date', '')}")
    else:
        print(f"\n[WARNING] No TRADING_DATE_BACKWARD events found")
        print("          The fix may not be working or backward dates aren't being detected")
    
    # Check rollover events to see if they're forward or backward
    if rollover_events:
        forward_count = 0
        backward_count = 0
        
        for ts, entry in rollover_events:
            data = entry.get('data', {})
            payload = data.get('payload', {})
            prev = payload.get('previous_trading_date', '')
            new_date = payload.get('new_trading_date', '')
            
            if prev and new_date:
                # Try to parse dates
                try:
                    prev_parts = prev.split('-')
                    new_parts = new_date.split('-')
                    if len(prev_parts) == 3 and len(new_parts) == 3:
                        prev_date = datetime(int(prev_parts[0]), int(prev_parts[1]), int(prev_parts[2]))
                        new_date_obj = datetime(int(new_parts[0]), int(new_parts[1]), int(new_parts[2]))
                        if new_date_obj < prev_date:
                            backward_count += 1
                        else:
                            forward_count += 1
                except:
                    pass
        
        print(f"\nRollover breakdown:")
        print(f"  Forward dates: {forward_count}")
        print(f"  Backward dates: {backward_count}")
        
        if backward_count > 0:
            print(f"\n[ISSUE] {backward_count} backward rollovers still being logged")
            print("        These should be TRADING_DATE_BACKWARD events instead")

if __name__ == '__main__':
    main()
