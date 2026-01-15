#!/usr/bin/env python3
"""Check detailed rollover event data"""
import json
import glob
from datetime import datetime, timezone, timedelta

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("DETAILED ROLLOVER ANALYSIS")
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
        print("\nFirst 3 rollover events (full JSON):")
        for i, (ts, entry) in enumerate(rollover_events[:3]):
            print(f"\n[{i+1}] [{ts.strftime('%H:%M:%S')}]")
            print(json.dumps(entry, indent=2))
    
    # Check what the journal TradingDate is
    print("\n" + "=" * 80)
    print("CHECKING JOURNAL_WRITTEN EVENTS")
    print("=" * 80)
    
    journal_events = []
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= two_minutes_ago and entry.get('event') == 'JOURNAL_WRITTEN':
                    journal_events.append((ts, entry))
        except:
            continue
    
    journal_events.sort(key=lambda x: x[0])
    
    if journal_events:
        print(f"\nFound {len(journal_events)} JOURNAL_WRITTEN events")
        print("\nFirst 3 journal events:")
        for i, (ts, entry) in enumerate(journal_events[:3]):
            print(f"\n[{i+1}] [{ts.strftime('%H:%M:%S')}]")
            data = entry.get('data', {})
            print(f"  Trading Date: {entry.get('trading_date', 'NONE')}")
            print(f"  Committed: {data.get('committed', 'NONE')}")
            print(f"  Commit Reason: {data.get('commit_reason', 'NONE')}")

if __name__ == '__main__':
    main()
