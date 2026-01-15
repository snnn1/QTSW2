#!/usr/bin/env python3
"""Check which code path is generating rollovers"""
import json
import glob
from datetime import datetime, timezone, timedelta

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("ROLLOVER SOURCE ANALYSIS")
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
    
    # Get recent rollover events
    rollovers = []
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= two_minutes_ago and entry.get('event') == 'TRADING_DAY_ROLLOVER':
                    rollovers.append((ts, entry))
        except:
            continue
    
    rollovers.sort(key=lambda x: x[0])
    
    print(f"\nFound {len(rollovers)} rollover events in last 2 minutes")
    
    if rollovers:
        # Categorize by reason/note
        committed_count = 0
        reset_count = 0
        other_count = 0
        
        for ts, entry in rollovers:
            data = entry.get('data', {})
            payload = data.get('payload', {})
            reason = payload.get('reason', '')
            note = payload.get('note', '')
            
            if 'COMMITTED' in note or 'COMMITTED' in reason:
                committed_count += 1
            elif 'Bar buffer cleared' in reason or 'PRE_HYDRATION' in reason:
                reset_count += 1
            else:
                other_count += 1
        
        print(f"\nRollover breakdown:")
        print(f"  Committed journal path: {committed_count}")
        print(f"  Reset path: {reset_count}")
        print(f"  Other: {other_count}")
        
        # Show examples
        print("\nFirst 5 rollover events:")
        for i, (ts, entry) in enumerate(rollovers[:5]):
            data = entry.get('data', {})
            payload = data.get('payload', {})
            prev = payload.get('previous_trading_date', '')
            new_date = payload.get('new_trading_date', '')
            reason = payload.get('reason', '')
            note = payload.get('note', '')
            
            print(f"\n[{i+1}] [{ts.strftime('%H:%M:%S')}]")
            print(f"  Previous: '{prev}'")
            print(f"  New: '{new_date}'")
            print(f"  Reason: '{reason}'")
            print(f"  Note: '{note}'")
            print(f"  State Reset: {payload.get('state_reset_to', 'NONE')}")
            
            # Check if this should be caught by our fix
            if not prev or prev == '':
                print(f"  [SHOULD BE INITIALIZATION] Previous date is empty!")
            elif prev and new_date:
                try:
                    prev_parts = prev.split('-')
                    new_parts = new_date.split('-')
                    if len(prev_parts) == 3 and len(new_parts) == 3:
                        prev_date = datetime(int(prev_parts[0]), int(prev_parts[1]), int(prev_parts[2]))
                        new_date_obj = datetime(int(new_parts[0]), int(new_parts[1]), int(new_parts[2]))
                        if new_date_obj < prev_date:
                            print(f"  [SHOULD BE BACKWARD DATE] New date is before previous!")
                except:
                    pass

if __name__ == '__main__':
    main()
