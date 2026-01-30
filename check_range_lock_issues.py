#!/usr/bin/env python3
"""
Check why ranges are stuck building and not locking.
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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=24)
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("RANGE LOCK ISSUES ANALYSIS")
    print("="*80)
    
    # Load all events
    events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True):
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
    
    events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    # Check ES1 and YM1 specifically
    stuck_streams = ['ES1', 'YM1']
    
    for stream in stuck_streams:
        print(f"\n{'='*80}")
        print(f"{stream} ANALYSIS:")
        print(f"{'='*80}")
        
        stream_events = [e for e in events if e.get('stream') == stream]
        
        # Check range build start
        range_build_start = [e for e in stream_events if e.get('event') == 'RANGE_BUILD_START']
        range_locked = [e for e in stream_events if e.get('event') == 'RANGE_LOCKED']
        
        print(f"\n  RANGE_BUILD_START: {len(range_build_start)}")
        print(f"  RANGE_LOCKED: {len(range_locked)}")
        
        if range_build_start and not range_locked:
            latest_build = max(range_build_start, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts_build = parse_timestamp(latest_build.get('ts_utc', ''))
            if ts_build:
                ts_chicago = ts_build.astimezone(chicago_tz)
                age_minutes = (datetime.now(timezone.utc) - ts_build).total_seconds() / 60
                print(f"\n  Stuck building since: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
                
                # Check slot time
                data = latest_build.get('data', {})
                if isinstance(data, dict):
                    slot_time = data.get('slot_time_chicago', 'N/A')
                    print(f"  Slot time: {slot_time}")
                
                # Check if slot time has passed
                if isinstance(data, dict) and 'slot_time_chicago' in data:
                    slot_time_str = data.get('slot_time_chicago', '')
                    # Parse slot time (format: "HH:MM")
                    try:
                        slot_hour, slot_minute = map(int, slot_time_str.split(':'))
                        slot_dt_chicago = ts_build.astimezone(chicago_tz).replace(hour=slot_hour, minute=slot_minute, second=0, microsecond=0)
                        now_chicago = datetime.now(timezone.utc).astimezone(chicago_tz)
                        
                        if now_chicago >= slot_dt_chicago:
                            print(f"  [WARN] Slot time has passed! Current: {now_chicago.strftime('%H:%M:%S')} CT, Slot: {slot_time_str}")
                        else:
                            print(f"  [INFO] Slot time not yet reached. Current: {now_chicago.strftime('%H:%M:%S')} CT, Slot: {slot_time_str}")
                    except:
                        pass
        
        # Check for bars received
        bars_received = [e for e in stream_events if 'BAR' in e.get('event', '') and 'RECEIVED' in e.get('event', '')]
        bars_added = [e for e in stream_events if e.get('event') == 'BAR_BUFFER_ADD_ATTEMPT']
        bars_rejected = [e for e in stream_events if e.get('event') == 'BAR_BUFFER_REJECTED']
        
        print(f"\n  Bars received: {len(bars_received)}")
        print(f"  Bars added: {len(bars_added)}")
        print(f"  Bars rejected: {len(bars_rejected)}")
        
        # Check recent bar activity
        if bars_added:
            latest_bar = max(bars_added, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts_bar = parse_timestamp(latest_bar.get('ts_utc', ''))
            if ts_bar:
                ts_chicago = ts_bar.astimezone(chicago_tz)
                age_minutes = (datetime.now(timezone.utc) - ts_bar).total_seconds() / 60
                print(f"  Last bar added: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
        
        # Check Tick() calls
        tick_calls = [e for e in stream_events if 'TICK' in e.get('event', '').upper()]
        print(f"\n  Tick-related events: {len(tick_calls)}")
        
        # Check OnBarUpdate calls
        onbarupdate = [e for e in stream_events if 'ONBARUPDATE' in e.get('event', '').upper()]
        print(f"  OnBarUpdate events: {len(onbarupdate)}")
        
        if onbarupdate:
            latest_obu = max(onbarupdate, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts_obu = parse_timestamp(latest_obu.get('ts_utc', ''))
            if ts_obu:
                ts_chicago = ts_obu.astimezone(chicago_tz)
                age_minutes = (datetime.now(timezone.utc) - ts_obu).total_seconds() / 60
                print(f"  Last OnBarUpdate: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")

if __name__ == "__main__":
    main()
