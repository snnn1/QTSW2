#!/usr/bin/env python3
"""
Check RTY2 startup timing to understand why bars were missed
"""
import json
import datetime
from pathlib import Path

def check_startup_timing():
    """Check when system started and what bars were requested"""
    log_file = Path("logs/robot/robot_RTY.jsonl")
    
    today_start = datetime.datetime.now(datetime.timezone.utc).replace(
        hour=0, minute=0, second=0, microsecond=0
    )
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig', errors='ignore') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                event = json.loads(line)
                if 'ts_utc' in event:
                    ts = datetime.datetime.fromisoformat(
                        event['ts_utc'].replace('Z', '+00:00')
                    )
                    if ts >= today_start:
                        events.append(event)
            except:
                continue
    
    print("="*80)
    print("RTY2 STARTUP TIMING ANALYSIS")
    print("="*80)
    
    # Find first event
    if events:
        first_event = events[0]
        first_ts = datetime.datetime.fromisoformat(
            first_event['ts_utc'].replace('Z', '+00:00')
        )
        first_chicago = first_ts.astimezone(
            datetime.timezone(datetime.timedelta(hours=-6))
        )
        print(f"\nFIRST EVENT:")
        print(f"  UTC: {first_ts.strftime('%Y-%m-%d %H:%M:%S')}")
        print(f"  Chicago: {first_chicago.strftime('%Y-%m-%d %H:%M:%S')}")
        print(f"  Event: {first_event.get('event', 'UNKNOWN')}")
    
    # Find BarsRequest events
    barsrequest_events = [e for e in events if 'BARSREQUEST' in e.get('event', '')]
    print(f"\nBARSREQUEST EVENTS: {len(barsrequest_events)}")
    for event in barsrequest_events[:5]:
        ts = event.get('ts_utc', '')[:19]
        event_type = event.get('event', 'UNKNOWN')
        data = event.get('data', {})
        print(f"\n  [{ts}] {event_type}")
        if 'start_time' in data:
            print(f"    Start Time: {data.get('start_time')}")
        if 'end_time' in data:
            print(f"    End Time: {data.get('end_time')}")
        if 'bars_requested' in data:
            print(f"    Bars Requested: {data.get('bars_requested')}")
        if 'bars_received' in data:
            print(f"    Bars Received: {data.get('bars_received')}")
    
    # Find STREAM_ARMED or PRE_HYDRATION events
    startup_events = [e for e in events if any(x in e.get('event', '') for x in ['STREAM_ARMED', 'PRE_HYDRATION', 'STARTUP'])]
    print(f"\nSTARTUP EVENTS: {len(startup_events)}")
    for event in startup_events[:5]:
        ts = event.get('ts_utc', '')[:19]
        event_type = event.get('event', 'UNKNOWN')
        print(f"  [{ts}] {event_type}")
    
    # Check when bars from 08:57-09:12 should have been received
    print("\n" + "="*80)
    print("MISSING BAR WINDOW ANALYSIS")
    print("="*80)
    
    missing_start = datetime.datetime(2026, 2, 4, 8, 57, 0, tzinfo=datetime.timezone(datetime.timedelta(hours=-6)))
    missing_end = datetime.datetime(2026, 2, 4, 9, 13, 0, tzinfo=datetime.timezone(datetime.timedelta(hours=-6)))
    
    print(f"\nMissing Window: {missing_start.strftime('%H:%M')} to {missing_end.strftime('%H:%M')} CT")
    
    # Check if system was running during this time
    if events:
        first_chicago_time = first_chicago
        print(f"\nSystem Started: {first_chicago_time.strftime('%H:%M:%S')} CT")
        
        if first_chicago_time > missing_end:
            print(f"\nWARNING: SYSTEM STARTED AFTER MISSING WINDOW")
            print(f"   System started at {first_chicago_time.strftime('%H:%M:%S')} CT")
            print(f"   Missing window was {missing_start.strftime('%H:%M')} to {missing_end.strftime('%H:%M')} CT")
            print(f"   System wasn't running during missing window!")
        elif first_chicago_time > missing_start:
            print(f"\nWARNING: SYSTEM STARTED DURING MISSING WINDOW")
            print(f"   System started at {first_chicago_time.strftime('%H:%M:%S')} CT")
            print(f"   Missing window was {missing_start.strftime('%H:%M')} to {missing_end.strftime('%H:%M')} CT")
            print(f"   System started partway through missing window")
        else:
            print(f"\nOK: SYSTEM STARTED BEFORE MISSING WINDOW")
            print(f"   System should have received bars, but didn't")

if __name__ == "__main__":
    check_startup_timing()
