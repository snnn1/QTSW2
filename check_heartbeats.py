#!/usr/bin/env python3
"""
Check all heartbeat events in the system.
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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=6)
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("HEARTBEAT EVENTS ANALYSIS")
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
    
    print(f"\nLoaded {len(events):,} events from last 6 hours\n")
    
    # Find all heartbeat events
    heartbeat_events = [e for e in events if 'HEARTBEAT' in e.get('event', '').upper()]
    
    print("="*80)
    print("HEARTBEAT EVENTS SUMMARY:")
    print("="*80)
    
    print(f"\n  Total heartbeat events: {len(heartbeat_events)}")
    
    if heartbeat_events:
        # Group by event type
        by_type = defaultdict(list)
        for e in heartbeat_events:
            by_type[e.get('event')].append(e)
        
        print(f"\n  By event type:")
        for event_type in sorted(by_type.keys()):
            count = len(by_type[event_type])
            latest = max(by_type[event_type], key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                age_minutes = (datetime.now(timezone.utc) - ts).total_seconds() / 60
                print(f"    {event_type}: {count:6} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
        
        # Group by stream/instrument
        by_stream = defaultdict(list)
        by_instrument = defaultdict(list)
        
        for e in heartbeat_events:
            stream = e.get('stream', '')
            data = e.get('data', {})
            if isinstance(data, dict):
                instrument = data.get('instrument', '')
            else:
                instrument = ''
            
            if stream:
                by_stream[stream].append(e)
            if instrument:
                by_instrument[instrument].append(e)
        
        if by_stream:
            print(f"\n  By stream:")
            for stream in sorted(by_stream.keys()):
                stream_events = by_stream[stream]
                latest = max(stream_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
                ts = parse_timestamp(latest.get('ts_utc', ''))
                if ts:
                    ts_chicago = ts.astimezone(chicago_tz)
                    age_minutes = (datetime.now(timezone.utc) - ts).total_seconds() / 60
                    print(f"    {stream:6}: {len(stream_events):4} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
        
        if by_instrument:
            print(f"\n  By instrument:")
            for instrument in sorted(by_instrument.keys()):
                inst_events = by_instrument[instrument]
                latest = max(inst_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
                ts = parse_timestamp(latest.get('ts_utc', ''))
                if ts:
                    ts_chicago = ts.astimezone(chicago_tz)
                    age_minutes = (datetime.now(timezone.utc) - ts).total_seconds() / 60
                    print(f"    {instrument:6}: {len(inst_events):4} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
        
        # Show recent heartbeats
        print("\n" + "="*80)
        print("RECENT HEARTBEAT EVENTS (last 10):")
        print("="*80)
        
        recent = sorted(heartbeat_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-10:]
        
        for e in recent:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                event_type = e.get('event', 'N/A')
                stream = str(e.get('stream', 'N/A'))
                data = e.get('data', {})
                if isinstance(data, dict):
                    instrument = data.get('instrument', 'N/A')
                    bars_since = data.get('bars_since_last_heartbeat', 'N/A')
                    print(f"    {ts_chicago.strftime('%H:%M:%S')} CT | {stream:6} | {instrument:6} | {event_type} | Bars: {bars_since}")
            else:
                print(f"    {e.get('event', 'N/A')} | {e.get('stream', 'N/A')}")
    else:
        print(f"\n  [WARN] No heartbeat events found!")
        print(f"         This could indicate:")
        print(f"         - Heartbeat logging not enabled")
        print(f"         - System not running")
        print(f"         - Heartbeat events filtered out")

if __name__ == "__main__":
    main()
