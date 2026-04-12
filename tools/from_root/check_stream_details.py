#!/usr/bin/env python3
"""
Detailed stream analysis - check why streams aren't active and verify stream state transitions.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict
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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=4)  # Last 4 hours for stream history
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("DETAILED STREAM ANALYSIS")
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
    
    print(f"Loaded {len(events):,} events from last 4 hours\n")
    
    # Find all stream state transitions
    stream_transitions = [e for e in events if e.get('event') == 'STREAM_STATE_TRANSITION']
    
    print("="*80)
    print("STREAM STATE TRANSITIONS")
    print("="*80)
    
    streams = defaultdict(lambda: {'transitions': [], 'current_state': None, 'trading_date': None, 'instrument': None})
    
    for e in stream_transitions:
        stream = e.get('stream', '')
        data = e.get('data', {})
        old_state = data.get('old_state', '')
        new_state = data.get('new_state', '')
        ts = parse_timestamp(e.get('ts_utc', ''))
        trading_date = e.get('trading_date') or data.get('trading_date')
        instrument = e.get('instrument') or data.get('instrument')
        
        if stream:
            streams[stream]['transitions'].append({
                'timestamp': ts,
                'old_state': old_state,
                'new_state': new_state,
                'event': e
            })
            streams[stream]['current_state'] = new_state
            if trading_date:
                streams[stream]['trading_date'] = trading_date
            if instrument:
                streams[stream]['instrument'] = instrument
    
    print(f"\nFound {len(streams)} unique streams\n")
    
    if streams:
        for stream in sorted(streams.keys()):
            info = streams[stream]
            current_state = info['current_state'] or 'UNKNOWN'
            trading_date = info['trading_date'] or 'N/A'
            instrument = info['instrument'] or 'N/A'
            
            print(f"Stream: {stream}")
            print(f"  Current State: {current_state}")
            print(f"  Trading Date: {trading_date}")
            print(f"  Instrument: {instrument}")
            print(f"  Transitions: {len(info['transitions'])}")
            
            # Show recent transitions
            if info['transitions']:
                recent = sorted(info['transitions'], key=lambda x: x['timestamp'] or datetime.min.replace(tzinfo=timezone.utc))[-5:]
                print(f"  Recent transitions:")
                for trans in recent:
                    ts = trans['timestamp']
                    if ts:
                        ts_chicago = ts.astimezone(chicago_tz)
                        print(f"    {ts_chicago.strftime('%H:%M:%S')} CT | {trans['old_state']:20} → {trans['new_state']:20}")
            
            # Check if stream is in a terminal state
            terminal_states = ['COMMITTED', 'STOOD_DOWN', 'FAILED']
            if current_state in terminal_states:
                print(f"  [INFO] Stream is in terminal state: {current_state}")
            
            print()
    else:
        print("  [INFO] No stream state transitions found in last 4 hours")
        print("         This could mean:")
        print("         - No streams have been created yet")
        print("         - Streams were created earlier (>4 hours ago)")
        print("         - Market is closed and no new streams")
    
    # Check for stream creation events
    print("="*80)
    print("STREAM CREATION EVENTS")
    print("="*80)
    
    stream_created = [e for e in events if 'STREAM' in e.get('event', '') and ('CREATED' in e.get('event', '') or 'ARMED' in e.get('event', ''))]
    if stream_created:
        print(f"Found {len(stream_created)} stream creation/arming events")
        for e in stream_created[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                stream = e.get('stream', 'N/A')
                event_type = e.get('event', '')
                print(f"  {ts_chicago.strftime('%H:%M:%S')} CT | {event_type:40} | Stream: {stream}")
    else:
        print("  No stream creation events found")
    
    # Check for range locked events
    print("\n" + "="*80)
    print("RANGE LOCKED EVENTS")
    print("="*80)
    
    range_locked = [e for e in events if e.get('event') == 'RANGE_LOCKED']
    if range_locked:
        print(f"Found {len(range_locked)} RANGE_LOCKED events")
        for e in range_locked[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                stream = e.get('stream', 'N/A')
                data = e.get('data', {})
                bars_count = data.get('bars_count', 'N/A')
                print(f"  {ts_chicago.strftime('%H:%M:%S')} CT | Stream: {stream:20} | Bars: {bars_count}")
    else:
        print("  No RANGE_LOCKED events found")
    
    # Check for timetable validation
    print("\n" + "="*80)
    print("TIMETABLE VALIDATION")
    print("="*80)
    
    timetable_validated = [e for e in events if e.get('event') == 'TIMETABLE_VALIDATED']
    if timetable_validated:
        print(f"Found {len(timetable_validated)} TIMETABLE_VALIDATED events")
        latest = max(timetable_validated, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            data = latest.get('data', {})
            trading_date = latest.get('trading_date') or data.get('trading_date', 'N/A')
            print(f"  Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            print(f"  Trading Date: {trading_date}")
    else:
        print("  No TIMETABLE_VALIDATED events found")
    
    # Check BAR_RECEIVED_NO_STREAMS - why are bars arriving but no streams?
    print("\n" + "="*80)
    print("BAR_RECEIVED_NO_STREAMS ANALYSIS")
    print("="*80)
    
    no_streams = [e for e in events if e.get('event') == 'BAR_RECEIVED_NO_STREAMS']
    if no_streams:
        print(f"Found {len(no_streams)} BAR_RECEIVED_NO_STREAMS events")
        print("  This means bars are arriving but no streams are in a state that accepts them")
        print("  Possible reasons:")
        print("    - No streams created yet")
        print("    - Streams are in COMMITTED/STOOD_DOWN state")
        print("    - Streams are waiting for range window")
        print("    - Market is closed")
        
        # Check instruments
        instruments = defaultdict(int)
        for e in no_streams[-100:]:  # Last 100
            inst = e.get('instrument', 'UNKNOWN')
            instruments[inst] += 1
        
        print(f"\n  Instruments receiving bars (last 100 events):")
        for inst, count in sorted(instruments.items(), key=lambda x: x[1], reverse=True):
            print(f"    {inst:10} {count:4} events")
    
    # Check recovery state
    print("\n" + "="*80)
    print("RECOVERY STATE DETAILS")
    print("="*80)
    
    recovery_waiting = [e for e in events if e.get('event') == 'DISCONNECT_RECOVERY_WAITING_FOR_SYNC']
    if recovery_waiting:
        latest = max(recovery_waiting, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            print(f"  Latest DISCONNECT_RECOVERY_WAITING_FOR_SYNC: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            print(f"  Total events: {len(recovery_waiting)}")
            print(f"  [INFO] Robot is waiting for broker synchronization")
            print(f"         This is normal after a reconnect - waiting for order/execution updates")
    
    recovery_complete = [e for e in events if e.get('event') == 'DISCONNECT_RECOVERY_COMPLETE']
    if recovery_complete:
        latest = max(recovery_complete, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            print(f"  Latest DISCONNECT_RECOVERY_COMPLETE: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            print(f"  [OK] Recovery completed successfully")
    else:
        print(f"  [INFO] No DISCONNECT_RECOVERY_COMPLETE events found")
        print(f"         Recovery may still be in progress")
    
    print("\n" + "="*80)
    print("ANALYSIS COMPLETE")
    print("="*80)

if __name__ == "__main__":
    main()
