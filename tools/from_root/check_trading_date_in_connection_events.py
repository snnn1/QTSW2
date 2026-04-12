#!/usr/bin/env python3
"""
Check trading_date in connection events - verify SetTradingDate is working.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
import pytz

def parse_timestamp(ts_str: str):
    """Parse ISO timestamp"""
    if not ts_str:
        return None
    try:
        if 'T' in ts_str:
            if ts_str.endswith('Z'):
                ts_str = ts_str[:-1] + '+00:00'
            elif '+' not in ts_str:
                ts_str = ts_str + '+00:00'
            dt = datetime.fromisoformat(ts_str)
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            return dt
    except:
        pass
    return None

def main():
    log_dir = Path("logs/robot")
    cutoff = datetime.now(timezone.utc) - timedelta(hours=6)
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("TRADING DATE IN CONNECTION EVENTS CHECK")
    print("="*80)
    
    # Load events
    events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True)[:5]:
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            ts = parse_timestamp(e.get('ts_utc', ''))
                            if ts and ts >= cutoff:
                                events.append(e)
                        except:
                            pass
        except:
            pass
    
    # Find connection events
    conn_events = [e for e in events if 'CONNECTION' in e.get('event', '')]
    
    print(f"\nFound {len(conn_events)} connection events in last 6 hours\n")
    
    print("Connection Events with Trading Date:")
    print("-" * 80)
    
    empty_count = 0
    null_count = 0
    has_td_count = 0
    
    for e in conn_events:
        ts = parse_timestamp(e.get('ts_utc', ''))
        event_type = e.get('event', '')
        data = e.get('data', {})
        trading_date = data.get('trading_date')
        
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            
            if trading_date == "":
                empty_count += 1
                print(f"  {ts_chicago.strftime('%H:%M:%S')} CT | {event_type:35} | trading_date: \"\" [ISSUE: Empty string]")
            elif trading_date is None:
                null_count += 1
                print(f"  {ts_chicago.strftime('%H:%M:%S')} CT | {event_type:35} | trading_date: null [OK: Null is correct]")
            else:
                has_td_count += 1
                print(f"  {ts_chicago.strftime('%H:%M:%S')} CT | {event_type:35} | trading_date: {trading_date} [OK]")
    
    print("\n" + "-" * 80)
    print("Summary:")
    print(f"  Has trading_date: {has_td_count}")
    print(f"  Empty string: {empty_count} [ISSUE if > 0]")
    print(f"  Null/missing: {null_count} [OK]")
    
    if empty_count > 0:
        print(f"\n  [WARN] Found {empty_count} connection events with empty trading_date string")
        print(f"         SetTradingDate() should prevent this - may need to verify DLL is updated")
    else:
        print(f"\n  [OK] No empty trading_date strings found - SetTradingDate() is working correctly")
    
    print("\n" + "="*80)

if __name__ == "__main__":
    main()
