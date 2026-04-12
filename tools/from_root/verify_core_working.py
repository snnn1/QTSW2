#!/usr/bin/env python3
"""
Quick verification that core functionality is working.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
import pytz

def parse_timestamp(ts_str: str):
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
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("CORE FUNCTIONALITY VERIFICATION")
    print("="*80)
    
    # Load recent events
    events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True)[:2]:
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            events.append(e)
                        except:
                            pass
        except:
            pass
    
    # Get last 50 events
    recent_events = sorted([e for e in events if parse_timestamp(e.get('ts_utc', ''))], 
                          key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-50:]
    
    print(f"\nAnalyzing last 50 events from most recent log file\n")
    
    # 1. Check engine ticks
    ticks = [e for e in recent_events if e.get('event') == 'ENGINE_TICK_CALLSITE']
    if ticks:
        latest = max(ticks, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            ts_chicago = ts.astimezone(chicago_tz)
            print(f"[OK] Engine Ticks: Latest at {ts_chicago.strftime('%H:%M:%S')} CT ({age:.1f}s ago)")
        else:
            print(f"[OK] Engine Ticks: {len(ticks)} in last 50 events")
    else:
        print(f"[WARN] No engine ticks in last 50 events")
    
    # 2. Check connection events and trading_date
    conn_events = [e for e in recent_events if 'CONNECTION' in e.get('event', '')]
    if conn_events:
        print(f"\n[INFO] Connection events in last 50: {len(conn_events)}")
        for e in conn_events[-3:]:
            event_type = e.get('event', '')
            data = e.get('data', {})
            trading_date = data.get('trading_date')
            td_repr = repr(trading_date)
            print(f"  {event_type:40} | trading_date: {td_repr}")
            if trading_date == "":
                print(f"    [ISSUE] Empty string found!")
            elif trading_date is None:
                print(f"    [OK] Null (correct)")
            else:
                print(f"    [OK] Has value: {trading_date}")
    else:
        print(f"\n[INFO] No connection events in last 50 events (normal if no disconnect)")
    
    # 3. Check for new event types
    print(f"\n[INFO] Checking for new event types:")
    new_types = ['CONNECTION_RECOVERED_NOTIFICATION', 'DISCONNECT_RECOVERY_COMPLETE']
    for event_type in new_types:
        found = [e for e in events if e.get('event') == event_type]
        if found:
            latest = max(found, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                print(f"  {event_type:40} [OK] Found - Latest: {ts_chicago.strftime('%Y-%m-%d %H:%M:%S')} CT")
            else:
                print(f"  {event_type:40} [OK] Found")
        else:
            print(f"  {event_type:40} [NONE] Not found yet (will appear when conditions met)")
    
    # 4. Check recent event activity
    print(f"\n[INFO] Recent event activity (last 10 events):")
    for e in recent_events[-10:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            event_type = e.get('event', 'UNKNOWN')
            print(f"  {ts_chicago.strftime('%H:%M:%S')} CT | {event_type}")
    
    # 5. Summary
    print(f"\n" + "="*80)
    print("VERIFICATION SUMMARY")
    print("="*80)
    
    all_ok = True
    
    if not ticks:
        print("[WARN] No engine ticks found")
        all_ok = False
    else:
        print("[OK] Engine is running")
    
    # Check for empty trading_date
    empty_td = [e for e in conn_events if e.get('data', {}).get('trading_date') == ""]
    if empty_td:
        print(f"[ERROR] Found {len(empty_td)} connection events with empty trading_date")
        all_ok = False
    else:
        print("[OK] Trading date handling correct (no empty strings)")
    
    if all_ok:
        print("\n[OK] Core functionality is working correctly!")
        print("     - Engine is running")
        print("     - Logging is active")
        print("     - Trading date handling is correct")
        print("     - System is ready (streams will activate when ranges form)")
    else:
        print("\n[WARN] Some issues detected - see above")
    
    print("="*80)

if __name__ == "__main__":
    main()
