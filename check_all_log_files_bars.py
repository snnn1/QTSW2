#!/usr/bin/env python3
"""
Check all robot log files for recent bar events.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
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
    now = datetime.now(timezone.utc)
    cutoff = now - timedelta(hours=2)  # Last 2 hours
    
    print("="*80)
    print("CHECKING ALL LOG FILES FOR RECENT BAR EVENTS")
    print("="*80)
    print(f"Looking for events in last 2 hours (since {cutoff.astimezone(chicago_tz).strftime('%H:%M:%S')} CT)\n")
    
    # Get all log files sorted by modification time
    log_files = sorted(log_dir.glob("robot_*.jsonl"), key=lambda x: x.stat().st_mtime, reverse=True)
    
    print(f"Found {len(log_files)} log files\n")
    print("Most recent log files:")
    for f in log_files[:10]:
        mtime = datetime.fromtimestamp(f.stat().st_mtime, tz=timezone.utc)
        mtime_chicago = mtime.astimezone(chicago_tz)
        age = (now - mtime).total_seconds()
        print(f"  {f.name:30} Modified: {mtime_chicago.strftime('%Y-%m-%d %H:%M:%S')} CT ({age/60:.0f}m ago)")
    
    print("\n" + "="*80)
    print("SEARCHING FOR BAR EVENTS IN RECENT LOGS")
    print("="*80)
    
    all_bar_events = []
    
    # Check most recent files first
    for log_file in log_files[:5]:  # Check top 5 most recent files
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            ts = parse_timestamp(e.get('ts_utc', ''))
                            if ts and ts >= cutoff:
                                if 'BAR' in e.get('event', ''):
                                    all_bar_events.append((ts, e, log_file.name))
                        except:
                            pass
        except Exception as e:
            print(f"  [WARN] Failed to read {log_file.name}: {e}")
    
    # Sort by timestamp
    all_bar_events.sort(key=lambda x: x[0], reverse=True)
    
    print(f"\nFound {len(all_bar_events)} bar events in last 2 hours\n")
    
    if all_bar_events:
        print("Most recent bar events (last 20):")
        print("-" * 80)
        
        for ts, e, log_file in all_bar_events[:20]:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (now - ts).total_seconds()
            event_type = e.get('event', 'UNKNOWN')
            instrument = e.get('instrument', 'N/A')
            stream = e.get('stream', 'N/A')
            
            print(f"  {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago) | {log_file:20} | {event_type:35} | Inst: {instrument:10}")
        
        # Group by event type
        print("\n" + "="*80)
        print("BAR EVENT TYPES")
        print("="*80)
        
        by_type = {}
        for ts, e, log_file in all_bar_events:
            event_type = e.get('event', 'UNKNOWN')
            by_type[event_type] = by_type.get(event_type, 0) + 1
        
        for event_type in sorted(by_type.keys()):
            print(f"  {event_type:40} {by_type[event_type]:6} events")
        
        # Check for actual bar received events
        print("\n" + "="*80)
        print("BAR RECEIVED EVENTS")
        print("="*80)
        
        received_events = [e for ts, e, log_file in all_bar_events 
                          if e.get('event') in ['BAR_RECEIVED_NO_STREAMS', 'BAR_ACCEPTED', 'ONBARUPDATE_CALLED']]
        
        if received_events:
            print(f"Found {len(received_events)} bar received events")
            
            # Group by instrument
            by_instrument = {}
            for e in received_events:
                inst = e.get('instrument', 'UNKNOWN')
                ts = parse_timestamp(e.get('ts_utc', ''))
                if inst and ts:
                    if inst not in by_instrument:
                        by_instrument[inst] = []
                    by_instrument[inst].append(ts)
            
            print("\nLatest bar received by instrument:")
            for inst in sorted(by_instrument.keys()):
                timestamps = by_instrument[inst]
                latest = max(timestamps)
                age = (now - latest).total_seconds()
                ts_chicago = latest.astimezone(chicago_tz)
                print(f"  {inst:15} Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago) | Total: {len(timestamps)}")
        else:
            print("  [WARN] No BAR_RECEIVED_NO_STREAMS, BAR_ACCEPTED, or ONBARUPDATE_CALLED events found")
            print("         Only bar processing events found (BAR_BUFFER_*, BAR_ADMISSION_*, etc.)")
            print("         This suggests bars are not being received from the data feed")
        
    else:
        print("\n[WARN] No bar events found in last 2 hours")
        print("       Possible reasons:")
        print("       - Market is closed")
        print("       - Robot is not running")
        print("       - Data feed is disconnected")
        print("       - Bars are not being logged")
    
    print("\n" + "="*80)
    print("SUMMARY")
    print("="*80)
    
    if all_bar_events:
        latest_ts, latest_e, latest_file = all_bar_events[0]
        age = (now - latest_ts).total_seconds()
        ts_chicago = latest_ts.astimezone(chicago_tz)
        
        print(f"\nLatest bar event: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
        print(f"Event: {latest_e.get('event')}")
        print(f"File: {latest_file}")
        
        if age < 60:
            print("\n[OK] Bars are being updated in logs")
        elif age < 300:
            print(f"\n[WARN] Bars may have stopped - latest {age:.0f}s ago")
        else:
            print(f"\n[ERROR] Bars have stopped - latest {age:.0f}s ago ({age/60:.1f} minutes)")
    else:
        print("\n[ERROR] No bar events found - bars are NOT being updated in logs")
    
    print("="*80)

if __name__ == "__main__":
    main()
