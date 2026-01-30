#!/usr/bin/env python3
"""
Check current stream states to understand why safety assertion isn't showing events.
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
    print("CURRENT STREAM STATES")
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
    
    # Get latest state for each stream
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
    
    print(f"\n  Stream states (last 2 hours):")
    now_utc = datetime.now(timezone.utc)
    
    range_building_streams = []
    for stream in sorted(stream_states.keys()):
        state_info = stream_states[stream]
        ts_chicago = state_info['ts'].astimezone(chicago_tz)
        age_minutes = (now_utc - state_info['ts']).total_seconds() / 60
        state = state_info['state']
        
        print(f"    {stream:6}: {state:20} | Since {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
        
        if state == 'RANGE_BUILDING':
            range_building_streams.append(stream)
    
    if range_building_streams:
        print(f"\n  [INFO] Streams in RANGE_BUILDING state: {', '.join(range_building_streams)}")
        print(f"         Safety assertion should be checking these streams")
    else:
        print(f"\n  [INFO] No streams in RANGE_BUILDING state")
        print(f"         Safety assertion only checks RANGE_BUILDING streams")
        print(f"         This explains why no assertion check events are found")
    
    # Check for Tick() calls
    tick_calls = [e for e in events if e.get('event') == 'TICK_CALLED']
    print(f"\n  TICK_CALLED events: {len(tick_calls)}")
    if tick_calls:
        latest = max(tick_calls, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age_minutes = (now_utc - ts).total_seconds() / 60
            stream = latest.get('stream', 'N/A')
            print(f"    Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago) | Stream: {stream}")

if __name__ == "__main__":
    main()
