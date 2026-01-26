"""Check if streams are within their range windows"""
import json
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    log_dir = Path("logs/robot")
    
    # Read ENGINE log
    engine_log = log_dir / "robot_ENGINE.jsonl"
    if not engine_log.exists():
        print("[ERROR] ENGINE log not found")
        return
    
    events = []
    with open(engine_log, 'r', encoding='utf-8-sig') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass
    
    # Get recent events
    recent = events[-500:] if len(events) > 500 else events
    
    # Find latest ENGINE_START
    starts = [e for e in recent if e.get('event') == 'ENGINE_START']
    if not starts:
        print("[ERROR] No ENGINE_START found")
        return
    
    latest_start = starts[-1]
    start_time = parse_timestamp(latest_start.get('ts_utc', ''))
    
    print("="*80)
    print("STREAM RANGE WINDOW ANALYSIS")
    print("="*80)
    print(f"\nLatest ENGINE_START: {start_time.strftime('%Y-%m-%d %H:%M:%S UTC') if start_time else 'N/A'}")
    
    # Get current time
    now_utc = datetime.now(timezone.utc)
    print(f"Current time (UTC): {now_utc.strftime('%Y-%m-%d %H:%M:%S UTC')}")
    
    # Find STREAM_ARMED events
    armed = [e for e in recent 
             if e.get('event') == 'STREAM_ARMED' and 
             parse_timestamp(e.get('ts_utc', '')) and
             parse_timestamp(e.get('ts_utc', '')) >= start_time]
    
    print(f"\n[STREAM_ARMED EVENTS]")
    print(f"  Total: {len(armed)}")
    
    if not armed:
        print("  No streams armed since latest start")
        return
    
    # Extract stream info
    streams_info = []
    for e in armed:
        inst = e.get('instrument', 'UNKNOWN')
        stream = e.get('stream', 'UNKNOWN')
        data = e.get('data', {})
        payload = str(data.get('payload', ''))
        
        # Try to extract slot_time_chicago from payload
        slot_time = 'UNKNOWN'
        session = 'UNKNOWN'
        range_start = 'UNKNOWN'
        
        if 'slot_time_chicago =' in payload:
            try:
                slot_time = payload.split('slot_time_chicago =')[1].split(',')[0].strip()
            except:
                pass
        
        if 'session =' in payload:
            try:
                session = payload.split('session =')[1].split(',')[0].strip()
            except:
                pass
        
        if 'range_start_chicago =' in payload:
            try:
                range_start = payload.split('range_start_chicago =')[1].split(',')[0].strip()
            except:
                pass
        
        streams_info.append({
            'instrument': inst,
            'stream': stream,
            'slot_time': slot_time,
            'session': session,
            'range_start': range_start,
            'armed_time': parse_timestamp(e.get('ts_utc', ''))
        })
    
    print(f"\n[STREAM DETAILS]")
    print(f"{'Inst':<6} {'Stream':<20} {'Session':<10} {'Slot Time':<12} {'Range Start':<12} {'Status'}")
    print("-" * 80)
    
    for info in streams_info:
        inst = info['instrument']
        stream = info['stream'][:20]
        session = info['session'][:10]
        slot_time = info['slot_time'][:12]
        range_start = info['range_start'][:12]
        
        # Determine if stream is in range window
        # This is simplified - actual logic checks if current time is >= range_start
        status = "WAITING"
        if range_start != 'UNKNOWN' and slot_time != 'UNKNOWN':
            try:
                # Parse range_start (format: HH:MM)
                range_hour, range_min = map(int, range_start.split(':'))
                # Parse slot_time (format: HH:MM)
                slot_hour, slot_min = map(int, slot_time.split(':'))
                
                # Convert to minutes for comparison
                range_minutes = range_hour * 60 + range_min
                slot_minutes = slot_hour * 60 + slot_min
                now_chicago = now_utc.astimezone(timezone(timedelta(hours=-6)))  # CST
                now_minutes = now_chicago.hour * 60 + now_chicago.minute
                
                # Check if we're past range_start but before slot_time
                if range_minutes <= now_minutes < slot_minutes:
                    status = "IN_RANGE"
                elif now_minutes >= slot_minutes:
                    status = "PAST_SLOT"
                else:
                    status = "BEFORE_RANGE"
            except:
                pass
        
        print(f"{inst:<6} {stream:<20} {session:<10} {slot_time:<12} {range_start:<12} {status}")
    
    # Check for RANGE_START_INITIALIZED events
    range_start_init = [e for e in recent 
                       if e.get('event') == 'RANGE_START_INITIALIZED' and
                       parse_timestamp(e.get('ts_utc', '')) and
                       parse_timestamp(e.get('ts_utc', '')) >= start_time]
    
    print(f"\n[RANGE_START_INITIALIZED EVENTS]")
    print(f"  Total: {len(range_start_init)}")
    
    if range_start_init:
        print("  [OK] Range start times have been initialized")
        for e in range_start_init[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            inst = e.get('instrument', 'N/A')
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {inst}")
    else:
        print("  [WARN] No range start times initialized")
    
    # Summary
    print("\n" + "="*80)
    print("SUMMARY")
    print("="*80)
    
    in_range_count = sum(1 for info in streams_info if info.get('status') == 'IN_RANGE')
    waiting_count = len(streams_info) - in_range_count
    
    print(f"\nStreams in range window: {in_range_count}/{len(streams_info)}")
    print(f"Streams waiting: {waiting_count}/{len(streams_info)}")
    
    if in_range_count == 0:
        print("\n[EXPECTED BEHAVIOR]")
        print("  No streams are currently in their range windows")
        print("  BarsRequest will be called when streams enter their range windows")
        print("  This is normal if current time is before range_start times")
    else:
        print(f"\n[ACTION NEEDED]")
        print(f"  {in_range_count} streams are in range but BarsRequest not called")
        print("  Check why BarsRequest is not being triggered")

if __name__ == '__main__':
    from datetime import timedelta
    main()
