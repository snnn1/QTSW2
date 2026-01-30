#!/usr/bin/env python3
"""
Check ALL Tick-related events to understand the full picture.
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
    print("ALL TICK-RELATED EVENTS ANALYSIS")
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
    
    # Find ALL events with "TICK" in the name
    all_tick_events = [e for e in events if 'TICK' in e.get('event', '').upper()]
    
    print("="*80)
    print("ALL TICK-RELATED EVENTS:")
    print("="*80)
    
    by_type = defaultdict(list)
    for e in all_tick_events:
        by_type[e.get('event')].append(e)
    
    print(f"\n  Total Tick-related events: {len(all_tick_events)}")
    print(f"\n  By event type:")
    for event_type in sorted(by_type.keys()):
        count = len(by_type[event_type])
        latest = max(by_type[event_type], key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age_minutes = (datetime.now(timezone.utc) - ts).total_seconds() / 60
            print(f"    {event_type}: {count:4} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
    
    # Check for ENGINE_TICK events
    engine_tick = [e for e in events if 'ENGINE_TICK' in e.get('event', '').upper()]
    print(f"\n  ENGINE_TICK events: {len(engine_tick)}")
    
    # Check for any events from StreamStateMachine Tick() method
    stream_tick_events = [e for e in events if e.get('stream') and 'TICK' in e.get('event', '').upper()]
    print(f"\n  Stream-level Tick events: {len(stream_tick_events)}")
    
    if stream_tick_events:
        by_stream = defaultdict(list)
        for e in stream_tick_events:
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
                event_types = set(e.get('event') for e in stream_events)
                print(f"    {stream:6}: {len(stream_events)} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago) | Types: {', '.join(event_types)}")
    
    # Check recent events
    print("\n" + "="*80)
    print("RECENT TICK-RELATED EVENTS (last 10):")
    print("="*80)
    
    recent_tick = sorted(all_tick_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-10:]
    
    for e in recent_tick:
        ts = parse_timestamp(e.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            event_type = e.get('event', 'N/A')
            stream = str(e.get('stream', 'N/A'))
            print(f"    {ts_chicago.strftime('%H:%M:%S')} CT | {stream:6} | {event_type}")

if __name__ == "__main__":
    main()
