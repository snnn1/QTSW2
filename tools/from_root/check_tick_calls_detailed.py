#!/usr/bin/env python3
"""
Detailed check of Tick() calls and why safety assertion might not be showing events.
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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=2)
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("DETAILED TICK() CALLS AND SAFETY ASSERTION CHECK")
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
    
    print(f"\nLoaded {len(events):,} events from last 2 hours\n")
    
    # Check all Tick-related events
    print("="*80)
    print("TICK-RELATED EVENTS:")
    print("="*80)
    
    tick_events = [e for e in events if 'TICK' in e.get('event', '').upper()]
    
    tick_event_types = defaultdict(int)
    for e in tick_events:
        tick_event_types[e.get('event')] += 1
    
    print(f"\n  Total Tick-related events: {len(tick_events)}")
    print(f"\n  By event type:")
    for event_type, count in sorted(tick_event_types.items()):
        print(f"    {event_type}: {count}")
    
    # Check TICK_CALLED events specifically
    tick_called = [e for e in events if e.get('event') == 'TICK_CALLED']
    print(f"\n  TICK_CALLED events: {len(tick_called)}")
    
    if tick_called:
        # Group by stream
        by_stream = defaultdict(list)
        for e in tick_called:
            stream = e.get('stream', 'UNKNOWN')
            by_stream[stream].append(e)
        
        print(f"\n  By stream:")
        for stream in sorted(by_stream.keys()):
            stream_events = by_stream[stream]
            latest = max(stream_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                age_minutes = (datetime.now(timezone.utc) - ts).total_seconds() / 60
                print(f"    {stream:6}: {len(stream_events)} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
    else:
        print(f"    [WARN] No TICK_CALLED events found")
        print(f"         This means Tick() may not be running for streams")
    
    # Check TICK_TRACE events
    tick_trace = [e for e in events if e.get('event') == 'TICK_TRACE']
    print(f"\n  TICK_TRACE events: {len(tick_trace)}")
    
    if tick_trace:
        # Group by stream
        by_stream = defaultdict(list)
        for e in tick_trace:
            stream = e.get('stream', 'UNKNOWN')
            by_stream[stream].append(e)
        
        print(f"\n  By stream:")
        for stream in sorted(by_stream.keys()):
            stream_events = by_stream[stream]
            latest = max(stream_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                age_minutes = (datetime.now(timezone.utc) - ts).total_seconds() / 60
                print(f"    {stream:6}: {len(stream_events)} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
    
    # Check for RANGE_BUILDING streams and their slot times
    print("\n" + "="*80)
    print("RANGE_BUILDING STREAMS AND SLOT TIMES:")
    print("="*80)
    
    range_build_start = [e for e in events if e.get('event') == 'RANGE_BUILD_START']
    range_locked = [e for e in events if e.get('event') == 'RANGE_LOCKED']
    
    # Get latest state transitions
    state_transitions = [e for e in events if e.get('event') == 'STREAM_STATE_TRANSITION']
    stream_states = {}
    for e in state_transitions:
        stream = e.get('stream', '')
        data = e.get('data', {})
        if isinstance(data, dict):
            new_state = data.get('new_state', 'UNKNOWN')
            ts = parse_timestamp(e.get('ts_utc', ''))
            if stream and ts:
                if stream not in stream_states or ts > stream_states[stream]['ts']:
                    stream_states[stream] = {'state': new_state, 'ts': ts}
    
    # Find RANGE_BUILDING streams
    range_building_streams = {s: info for s, info in stream_states.items() if info['state'] == 'RANGE_BUILDING'}
    
    print(f"\n  Streams in RANGE_BUILDING: {len(range_building_streams)}")
    
    now_utc = datetime.now(timezone.utc)
    now_chicago = now_utc.astimezone(chicago_tz)
    
    for stream in sorted(range_building_streams.keys()):
        state_info = range_building_streams[stream]
        entered_at = state_info['ts']
        age_minutes = (now_utc - entered_at).total_seconds() / 60
        
        # Find slot time for this stream
        stream_range_build = [e for e in range_build_start if e.get('stream') == stream]
        slot_time_str = 'N/A'
        if stream_range_build:
            latest_build = max(stream_range_build, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            data = latest_build.get('data', {})
            if isinstance(data, dict):
                slot_time_str = data.get('slot_time_chicago', 'N/A')
        
        print(f"\n    {stream}:")
        print(f"      Entered RANGE_BUILDING: {entered_at.astimezone(chicago_tz).strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
        print(f"      Slot time: {slot_time_str}")
        print(f"      Current time: {now_chicago.strftime('%H:%M:%S')} CT")
        
        # Check if Tick() has been called for this stream
        stream_ticks = [e for e in tick_called if e.get('stream') == stream]
        if stream_ticks:
            latest_tick = max(stream_ticks, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            tick_ts = parse_timestamp(latest_tick.get('ts_utc', ''))
            if tick_ts:
                tick_age_minutes = (now_utc - tick_ts).total_seconds() / 60
                print(f"      Tick() called: {tick_ts.astimezone(chicago_tz).strftime('%H:%M:%S')} CT ({tick_age_minutes:.1f} min ago)")
            else:
                print(f"      Tick() called: YES (but timestamp parsing failed)")
        else:
            print(f"      Tick() called: NO (no TICK_CALLED events found)")
    
    # Summary
    print("\n" + "="*80)
    print("SUMMARY:")
    print("="*80)
    
    if tick_called:
        print(f"\n  [OK] Tick() is being called for streams")
    else:
        print(f"\n  [WARN] Tick() may not be called for streams")
        print(f"         Check if Tick() is running from OnMarketData()")
    
    if range_building_streams:
        print(f"\n  [INFO] {len(range_building_streams)} streams in RANGE_BUILDING")
        print(f"         Safety assertion should check these streams")
        print(f"         Assertion checks are rate-limited to once per 15 minutes")
        print(f"         Threshold is 10 minutes past slot time")
        print(f"         If no events yet, streams may not have exceeded threshold")

if __name__ == "__main__":
    main()
