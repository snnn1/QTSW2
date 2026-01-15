#!/usr/bin/env python3
"""Check rollover event details to understand why fix isn't working"""
import json
import glob
from datetime import datetime, timezone, timedelta

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("ROLLOVER EVENT DETAILS")
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
    rollover_events = []
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= two_minutes_ago and entry.get('event') == 'TRADING_DAY_ROLLOVER':
                    rollover_events.append((ts, entry))
        except:
            continue
    
    rollover_events.sort(key=lambda x: x[0])
    
    if rollover_events:
        print(f"\nFound {len(rollover_events)} rollover events in last 2 minutes")
        print("\nFirst 5 rollover events:")
        for i, (ts, entry) in enumerate(rollover_events[:5]):
            data = entry.get('data', {})
            prev_date = data.get('previous_trading_date', '')
            new_date = data.get('new_trading_date', '')
            state_reset = data.get('state_reset_to', '')
            reason = data.get('reason', '')
            
            print(f"\n[{i+1}] [{ts.strftime('%H:%M:%S')}]")
            print(f"  Previous: '{prev_date}' (empty: {not prev_date})")
            print(f"  New: '{new_date}' (empty: {not new_date})")
            print(f"  State Reset: {state_reset}")
            print(f"  Reason: {reason}")
            print(f"  Stream: {entry.get('data', {}).get('stream', 'NONE')}")
            print(f"  Trading Date: {entry.get('trading_date', 'NONE')}")
    
    # Check for TRADING_DATE_INITIALIZED
    initialized = [(ts, e) for ts, e in [(datetime.fromisoformat(e.get('ts_utc', '').replace('Z', '+00:00')), e) for e in all_events if 'ts_utc' in e] if ts >= two_minutes_ago and e.get('event') == 'TRADING_DATE_INITIALIZED']
    
    print(f"\n" + "=" * 80)
    print("TRADING_DATE_INITIALIZED CHECK")
    print("=" * 80)
    print(f"Found {len(initialized)} TRADING_DATE_INITIALIZED events")
    
    if len(initialized) == 0:
        print("\n[ISSUE] No TRADING_DATE_INITIALIZED events found")
        print("        This suggests the fix code may not be running")
        print("        Possible causes:")
        print("        1. Code not recompiled in NinjaTrader")
        print("        2. Journal TradingDate is not empty (has a value)")
        print("        3. Different code path is being used")

if __name__ == '__main__':
    main()
