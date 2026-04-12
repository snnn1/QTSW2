#!/usr/bin/env python3
"""
Check connection events in robot logs.
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
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("CONNECTION EVENTS CHECK")
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
    
    # Connection event types
    connection_event_types = [
        'CONNECTION_LOST',
        'CONNECTION_LOST_SUSTAINED',
        'CONNECTION_RECOVERED',
        'CONNECTION_RECOVERED_NOTIFICATION',
        'DISCONNECT_FAIL_CLOSED_ENTERED',
        'DISCONNECT_RECOVERY_STARTED',
        'DISCONNECT_RECOVERY_COMPLETE',
        'DISCONNECT_RECOVERY_ABORTED',
    ]
    
    print("="*80)
    print("CONNECTION EVENT TYPES:")
    print("="*80)
    
    for event_type in connection_event_types:
        matching = [e for e in events if e.get('event') == event_type]
        count = len(matching)
        
        if matching:
            latest = max(matching, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                age_seconds = (datetime.now(timezone.utc) - ts).total_seconds()
                print(f"    {event_type:35}: {count:6} events | Latest: {ts_chicago.strftime('%Y-%m-%d %H:%M:%S')} CT ({age_seconds:.1f} sec ago)")
            else:
                print(f"    {event_type:35}: {count:6} events")
        else:
            print(f"    {event_type:35}: {count:6} events | NOT FOUND")
    
    # Show recent connection events
    all_conn_events = [e for e in events if any(et in e.get('event', '') for et in ['CONNECTION', 'DISCONNECT'])]
    print(f"\n" + "="*80)
    print(f"RECENT CONNECTION EVENTS (last 10):")
    print("="*80)
    
    for e in all_conn_events[-10:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            event_type = e.get('event', 'UNKNOWN')
            data = e.get('data', {})
            conn_name = data.get('connection_name', 'N/A')
            print(f"  {ts_chicago.strftime('%H:%M:%S')} CT | {event_type:35} | Connection: {conn_name}")

if __name__ == "__main__":
    main()
