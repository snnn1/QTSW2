#!/usr/bin/env python3
"""
Check ES1 slot time and why range isn't locking.
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
    
    print("="*80)
    print("ES1 SLOT TIME & RANGE LOCK ANALYSIS")
    print("="*80)
    
    # Load ES1 events
    es1_events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            if e.get('stream') == 'ES1':
                                ts = parse_timestamp(e.get('ts_utc', ''))
                                if ts and ts >= cutoff:
                                    es1_events.append(e)
                        except:
                            pass
        except:
            pass
    
    es1_events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    # Load timetable to get slot time
    timetable_path = Path("data/timetable/timetable_current.json")
    slot_time_chicago = None
    session = None
    
    if timetable_path.exists():
        try:
            with open(timetable_path, 'r', encoding='utf-8') as f:
                timetable = json.load(f)
                if 'streams' in timetable:
                    for stream_entry in timetable['streams']:
                        if isinstance(stream_entry, dict) and stream_entry.get('stream') == 'ES1':
                            slot_time_chicago = stream_entry.get('slot_time')
                            session = stream_entry.get('session')
                            break
        except:
            pass
    
    print(f"\nTimetable Info:")
    print(f"  Slot Time (Chicago): {slot_time_chicago}")
    print(f"  Session: {session}")
    
    # Get current Chicago time
    chicago_tz = pytz.timezone('America/Chicago')
    now_chicago = datetime.now(chicago_tz)
    now_utc = datetime.now(timezone.utc)
    
    print(f"\nCurrent Time:")
    print(f"  UTC: {now_utc.strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"  Chicago: {now_chicago.strftime('%Y-%m-%d %H:%M:%S')}")
    
    # Parse slot time
    if slot_time_chicago:
        try:
            # Parse slot time (e.g., "07:30")
            slot_hour, slot_minute = map(int, slot_time_chicago.split(':'))
            slot_time_today = chicago_tz.localize(datetime.now().replace(hour=slot_hour, minute=slot_minute, second=0, microsecond=0))
            
            print(f"\nSlot Time Analysis:")
            print(f"  Slot Time Today: {slot_time_today.strftime('%Y-%m-%d %H:%M:%S %Z')}")
            
            if now_chicago >= slot_time_today:
                minutes_past_slot = (now_chicago - slot_time_today).total_seconds() / 60
                print(f"  [WARN] Current time is {minutes_past_slot:.1f} minutes PAST slot time")
                print(f"  Range should have locked by now!")
            else:
                minutes_to_slot = (slot_time_today - now_chicago).total_seconds() / 60
                print(f"  [INFO] Current time is {minutes_to_slot:.1f} minutes BEFORE slot time")
                print(f"  Range will lock at slot time")
        except Exception as e:
            print(f"  [ERROR] Could not parse slot time: {e}")
    
    # Check range building window
    print("\n" + "="*80)
    print("RANGE BUILDING WINDOW:")
    print("="*80)
    
    range_build_start = [e for e in es1_events if e.get('event') in ['RANGE_BUILD_START', 'RANGE_BUILDING_START']]
    
    if range_build_start:
        latest = range_build_start[-1]
        data = latest.get('data', {})
        if isinstance(data, dict):
            range_start_chicago_str = data.get('range_start_chicago', '')
            bar_count = data.get('bar_count', 'N/A')
            
            print(f"\n  Range Start: {range_start_chicago_str}")
            print(f"  Bar Count at start: {bar_count}")
            
            # Parse range start
            try:
                if 'T' in range_start_chicago_str:
                    range_start_chicago = parse_timestamp(range_start_chicago_str.replace('-06:00', '+00:00'))
                    if range_start_chicago:
                        range_start_chicago = range_start_chicago.astimezone(chicago_tz)
                        print(f"  Range Start (parsed): {range_start_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
                        
                        if slot_time_chicago:
                            slot_hour, slot_minute = map(int, slot_time_chicago.split(':'))
                            slot_time_today = chicago_tz.localize(datetime.now().replace(hour=slot_hour, minute=slot_minute, second=0, microsecond=0))
                            
                            window_duration = (slot_time_today - range_start_chicago).total_seconds() / 60
                            print(f"  Range Building Window: {window_duration:.1f} minutes")
                            
                            if now_chicago >= slot_time_today:
                                print(f"  [WARN] Slot time has passed - range should be locked!")
            except Exception as e:
                print(f"  [ERROR] Could not parse range start: {e}")
    
    # Check for range lock conditions
    print("\n" + "="*80)
    print("RANGE LOCK CONDITIONS:")
    print("="*80)
    
    # Check if we're past slot time
    if slot_time_chicago:
        try:
            slot_hour, slot_minute = map(int, slot_time_chicago.split(':'))
            slot_time_today = chicago_tz.localize(datetime.now().replace(hour=slot_hour, minute=slot_minute, second=0, microsecond=0))
            
            if now_chicago >= slot_time_today:
                print(f"\n  [WARN] Current time ({now_chicago.strftime('%H:%M')}) is PAST slot time ({slot_time_chicago})")
                print(f"  Range should lock when:")
                print(f"    1. Slot time passes")
                print(f"    2. Minimum bars accumulated")
                print(f"    3. Range validation passes")
            else:
                print(f"\n  [INFO] Current time ({now_chicago.strftime('%H:%M')}) is BEFORE slot time ({slot_time_chicago})")
                print(f"  Range will lock at slot time if conditions are met")
        except:
            pass
    
    # Check bar count
    bar_buffer_added = [e for e in es1_events if e.get('event') == 'BAR_BUFFER_ADD_COMMITTED']
    print(f"\n  Bars in buffer: {len(bar_buffer_added)}")
    
    if len(bar_buffer_added) < 30:
        print(f"  [WARN] May need more bars (currently {len(bar_buffer_added)}, typically need 30-60)")
    else:
        print(f"  [OK] Sufficient bars for range lock")
    
    # Check for range validation
    range_invalid = [e for e in es1_events if 'RANGE_INVALID' in e.get('event', '')]
    print(f"\n  Range validation failures: {len(range_invalid)}")
    
    if range_invalid:
        print(f"  [WARN] Range validation is failing - this prevents range lock")
        for e in range_invalid[-3:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            print(f"    {ts}: {e.get('msg', '')[:80]}")
    
    # Summary
    print("\n" + "="*80)
    print("DIAGNOSIS:")
    print("="*80)
    
    print("\n  ES1 Range Status:")
    print("    - State: RANGE_BUILDING")
    print("    - Bars in buffer: 660")
    print("    - Range not locked yet")
    
    if slot_time_chicago:
        try:
            slot_hour, slot_minute = map(int, slot_time_chicago.split(':'))
            slot_time_today = chicago_tz.localize(datetime.now().replace(hour=slot_hour, minute=slot_minute, second=0, microsecond=0))
            
            if now_chicago >= slot_time_today:
                print(f"\n  [ISSUE] Slot time ({slot_time_chicago}) has PASSED")
                print(f"    Current time: {now_chicago.strftime('%H:%M')}")
                print(f"    Range should have locked but hasn't")
                print(f"\n  Possible reasons:")
                print(f"    1. Range validation failing")
                print(f"    2. Minimum bar count not met (though 660 should be enough)")
                print(f"    3. Range building logic waiting for something")
                print(f"    4. System may be checking range on next bar")
        except:
            pass

if __name__ == "__main__":
    main()
