#!/usr/bin/env python3
"""
Check recovery state and why it's stuck in DISCONNECT_RECOVERY_WAITING_FOR_SYNC.
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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=2)
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("RECOVERY STATE ANALYSIS")
    print("="*80)
    
    # Load events
    events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True)[:3]:
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
    
    # Find recovery events in sequence
    recovery_sequence = []
    for e in events:
        event_type = e.get('event', '')
        if any(x in event_type for x in ['DISCONNECT', 'RECOVERY', 'CONNECTION']):
            recovery_sequence.append(e)
    
    print(f"\nRecovery-related events (last 2 hours): {len(recovery_sequence)}\n")
    
    # Show sequence
    print("Recovery Event Sequence:")
    for e in recovery_sequence[-20:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            event_type = e.get('event', '')
            data = e.get('data', {})
            note = data.get('note', '')[:60] if data.get('note') else ''
            print(f"  {ts_chicago.strftime('%H:%M:%S')} CT | {event_type:40} | {note}")
    
    # Check for OrderUpdate/ExecutionUpdate events (needed for broker sync)
    print("\n" + "="*80)
    print("BROKER SYNC EVENTS")
    print("="*80)
    
    order_updates = [e for e in events if 'ORDER' in e.get('event', '') and 'UPDATE' in e.get('event', '')]
    exec_updates = [e for e in events if 'EXECUTION' in e.get('event', '') and 'UPDATE' in e.get('event', '')]
    
    print(f"  OrderUpdate events: {len(order_updates)}")
    print(f"  ExecutionUpdate events: {len(exec_updates)}")
    
    if order_updates:
        latest = max(order_updates, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            print(f"    Latest OrderUpdate: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
    
    if exec_updates:
        latest = max(exec_updates, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            print(f"    Latest ExecutionUpdate: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
    
    # Check when recovery started
    recovery_started = [e for e in events if e.get('event') == 'DISCONNECT_RECOVERY_STARTED']
    if recovery_started:
        latest_start = max(recovery_started, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts_start = parse_timestamp(latest_start.get('ts_utc', ''))
        if ts_start:
            recovery_duration = (datetime.now(timezone.utc) - ts_start).total_seconds()
            ts_chicago = ts_start.astimezone(chicago_tz)
            print(f"\n  Recovery started: {ts_chicago.strftime('%H:%M:%S')} CT")
            print(f"  Duration: {recovery_duration:.0f} seconds ({recovery_duration/60:.1f} minutes)")
            print(f"  [INFO] Recovery has been waiting for {recovery_duration:.0f}s")
            print(f"         Broker sync requires OrderUpdate or ExecutionUpdate events")
            print(f"         If no orders exist, recovery may complete automatically after timeout")
    
    print("\n" + "="*80)

if __name__ == "__main__":
    main()
