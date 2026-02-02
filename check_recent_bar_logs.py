#!/usr/bin/env python3
"""
Check if bars are being updated in robot logs - look at most recent log file.
"""
import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

def parse_timestamp(ts_str: str):
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
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("CHECKING RECENT BAR EVENTS IN ROBOT LOGS")
    print("="*80)
    
    # Get most recent log file
    log_files = sorted(log_dir.glob("robot_*.jsonl"), reverse=True)
    
    if not log_files:
        print("\n[ERROR] No robot log files found")
        return
    
    print(f"\nChecking most recent log file: {log_files[0].name}\n")
    
    # Read last 1000 lines (to get recent events)
    events = []
    try:
        with open(log_files[0], 'r', encoding='utf-8') as f:
            lines = f.readlines()
            # Get last 1000 lines
            recent_lines = lines[-1000:] if len(lines) > 1000 else lines
            for line in recent_lines:
                line = line.strip()
                if line:
                    try:
                        e = json.loads(line)
                        events.append(e)
                    except:
                        pass
    except Exception as e:
        print(f"[ERROR] Failed to read log file: {e}")
        return
    
    print(f"Loaded {len(events)} events from end of log file\n")
    
    # Filter bar-related events
    bar_events = [e for e in events if 'BAR' in e.get('event', '')]
    
    print("="*80)
    print("BAR EVENTS IN RECENT LOGS")
    print("="*80)
    
    if bar_events:
        print(f"\nFound {len(bar_events)} bar-related events\n")
        
        # Group by event type
        by_type = {}
        for e in bar_events:
            event_type = e.get('event', 'UNKNOWN')
            by_type[event_type] = by_type.get(event_type, 0) + 1
        
        print("Event Types:")
        for event_type in sorted(by_type.keys()):
            print(f"  {event_type:40} {by_type[event_type]:6} events")
        
        # Show most recent bar events
        print("\n" + "="*80)
        print("MOST RECENT BAR EVENTS (Last 20)")
        print("="*80)
        
        # Sort by timestamp
        bar_events_sorted = sorted(bar_events, 
                                 key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc),
                                 reverse=True)
        
        for e in bar_events_sorted[:20]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                now = datetime.now(timezone.utc)
                age = (now - ts).total_seconds()
                
                event_type = e.get('event', 'UNKNOWN')
                instrument = e.get('instrument', 'N/A')
                stream = e.get('stream', 'N/A')
                
                print(f"  {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago) | {event_type:35} | Inst: {instrument:10} | Stream: {stream}")
            else:
                print(f"  [NO TIMESTAMP] | {e.get('event', 'UNKNOWN')}")
        
        # Check latest bar received
        print("\n" + "="*80)
        print("LATEST BAR RECEIVED BY INSTRUMENT")
        print("="*80)
        
        instruments = {}
        for e in bar_events_sorted:
            if e.get('event') in ['BAR_RECEIVED_NO_STREAMS', 'BAR_ACCEPTED', 'ONBARUPDATE_CALLED']:
                inst = e.get('instrument', 'UNKNOWN')
                ts = parse_timestamp(e.get('ts_utc', ''))
                if inst and ts:
                    if inst not in instruments:
                        instruments[inst] = ts
        
        if instruments:
            now = datetime.now(timezone.utc)
            for inst in sorted(instruments.keys()):
                latest_ts = instruments[inst]
                age = (now - latest_ts).total_seconds()
                ts_chicago = latest_ts.astimezone(chicago_tz)
                print(f"  {inst:15} Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
        else:
            print("  No bar received events found")
        
        # Check if bars are still arriving
        print("\n" + "="*80)
        print("BAR ARRIVAL STATUS")
        print("="*80)
        
        if bar_events_sorted:
            latest = bar_events_sorted[0]
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                now = datetime.now(timezone.utc)
                age = (now - ts).total_seconds()
                ts_chicago = ts.astimezone(chicago_tz)
                
                print(f"  Latest bar event: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
                print(f"  Event type: {latest.get('event')}")
                print(f"  Instrument: {latest.get('instrument', 'N/A')}")
                
                if age < 60:
                    print(f"\n  [OK] Bars are arriving - latest event {age:.0f}s ago")
                elif age < 300:
                    print(f"\n  [WARN] Bars may have stopped - latest event {age:.0f}s ago (5 minutes)")
                else:
                    print(f"\n  [ERROR] Bars appear to have stopped - latest event {age:.0f}s ago ({age/60:.1f} minutes)")
        
    else:
        print("\n[WARN] No bar-related events found in recent logs")
        print("       This could mean:")
        print("       - Market is closed")
        print("       - No bars are being received")
        print("       - Bar events aren't being logged")
    
    # Check log file modification time
    print("\n" + "="*80)
    print("LOG FILE STATUS")
    print("="*80)
    
    log_file = log_files[0]
    mtime = datetime.fromtimestamp(log_file.stat().st_mtime, tz=timezone.utc)
    mtime_chicago = mtime.astimezone(chicago_tz)
    now = datetime.now(timezone.utc)
    age = (now - mtime).total_seconds()
    
    print(f"  Log file: {log_file.name}")
    print(f"  Last modified: {mtime_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
    
    if age < 60:
        print(f"  [OK] Log file is being updated")
    else:
        print(f"  [WARN] Log file hasn't been updated in {age:.0f}s")
    
    print("="*80)

if __name__ == "__main__":
    main()
