#!/usr/bin/env python3
"""
Check current MGC status - focus on recent events and potential stuck states.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

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

def load_recent_events(log_dir: Path, hours: int = 2) -> list:
    """Load events from last N hours"""
    cutoff = datetime.now(timezone.utc) - timedelta(hours=hours)
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
    return sorted(events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))

def main():
    log_dir = Path("logs/robot")
    
    print("="*80)
    print("MGC CURRENT STATUS CHECK (Last 2 Hours)")
    print("="*80)
    
    events = load_recent_events(log_dir, hours=2)
    print(f"\nLoaded {len(events)} events from last 2 hours")
    
    # Filter MGC/GC events
    mgc_events = []
    for e in events:
        stream = e.get('stream', '')
        data = e.get('data', {})
        if isinstance(data, dict):
            instrument = data.get('instrument', '')
            canonical = data.get('canonical_instrument', '')
            execution = data.get('execution_instrument', '')
        else:
            instrument = canonical = execution = ''
        
        if (stream in ['GC1', 'GC2', 'MGC1', 'MGC2'] or
            instrument in ['MGC', 'GC'] or
            canonical == 'GC' or execution == 'MGC'):
            mgc_events.append(e)
    
    print(f"Found {len(mgc_events)} MGC-related events\n")
    
    # Group by stream
    by_stream = defaultdict(list)
    for e in mgc_events:
        stream = e.get('stream', '')
        if stream:
            by_stream[stream].append(e)
    
    # Check each stream
    for stream in ['GC1', 'GC2']:
        stream_events = by_stream.get(stream, [])
        if not stream_events:
            print(f"{stream}: No events found")
            continue
        
        # Get latest event
        latest = stream_events[-1]
        latest_ts = parse_timestamp(latest.get('ts_utc', ''))
        now = datetime.now(timezone.utc)
        
        if latest_ts:
            if latest_ts.tzinfo is None:
                latest_ts = latest_ts.replace(tzinfo=timezone.utc)
            age_seconds = (now - latest_ts).total_seconds()
            age_minutes = age_seconds / 60
        else:
            age_minutes = 999
        
        print(f"{stream}:")
        print(f"  Latest event: {latest.get('event', 'N/A')}")
        print(f"  Time: {latest.get('ts_utc', '')[:19]}")
        print(f"  Age: {age_minutes:.1f} minutes ago")
        print(f"  Total events: {len(stream_events)}")
        
        # Check for key states
        has_pre_hydration_waiting = any('PRE_HYDRATION_WAITING_FOR_BARSREQUEST' in e.get('event', '') for e in stream_events)
        has_pre_hydration_complete = any('PRE_HYDRATION_COMPLETE' in e.get('event', '') for e in stream_events)
        has_armed = any(e.get('event') in ['ARMED', 'STREAM_ARMED'] for e in stream_events)
        has_range_locked = any(e.get('event') == 'RANGE_LOCKED' for e in stream_events)
        
        # Check BarsRequest status
        barsrequest_pending = [e for e in stream_events if e.get('event') == 'BARSREQUEST_PENDING_MARKED']
        barsrequest_completed = [e for e in stream_events if e.get('event') == 'BARSREQUEST_COMPLETED_MARKED']
        
        print(f"\n  State Indicators:")
        print(f"    PRE_HYDRATION_WAITING_FOR_BARSREQUEST: {has_pre_hydration_waiting}")
        print(f"    PRE_HYDRATION_COMPLETE: {has_pre_hydration_complete}")
        print(f"    ARMED: {has_armed}")
        print(f"    RANGE_LOCKED: {has_range_locked}")
        
        print(f"\n  BarsRequest Status:")
        print(f"    Pending events: {len(barsrequest_pending)}")
        print(f"    Completed events: {len(barsrequest_completed)}")
        
        # Check for issues
        issues = []
        
        if has_pre_hydration_waiting and age_minutes > 5:
            issues.append(f"Waiting for BarsRequest for {age_minutes:.1f} minutes")
        
        if len(barsrequest_pending) > len(barsrequest_completed):
            latest_pending = barsrequest_pending[-1] if barsrequest_pending else None
            if latest_pending:
                pending_ts = parse_timestamp(latest_pending.get('ts_utc', ''))
                if pending_ts:
                    if pending_ts.tzinfo is None:
                        pending_ts = pending_ts.replace(tzinfo=timezone.utc)
                    pending_age = (now - pending_ts).total_seconds() / 60
                    if pending_age > 5:
                        issues.append(f"BarsRequest pending for {pending_age:.1f} minutes")
        
        if has_pre_hydration_complete and not has_armed and not has_range_locked:
            issues.append("Pre-hydration complete but not ARMED or RANGE_BUILDING")
        
        if age_minutes > 10 and latest.get('event') in ['PRE_HYDRATION', 'PRE_HYDRATION_COMPLETE']:
            issues.append(f"Stuck in PRE_HYDRATION for {age_minutes:.1f} minutes")
        
        if issues:
            print(f"\n  [WARN] Issues:")
            for issue in issues:
                print(f"    - {issue}")
        else:
            print(f"\n  [OK] No issues detected")
        
        # Show recent key events
        key_events = [e for e in stream_events if any(x in e.get('event', '') for x in [
            'PRE_HYDRATION', 'ARMED', 'RANGE_BUILDING', 'RANGE_LOCKED', 
            'BARSREQUEST', 'HYDRATION', 'STREAM_STATE_TRANSITION'
        ])]
        
        if key_events:
            print(f"\n  Recent Key Events (last 10):")
            for e in key_events[-10:]:
                ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
                event_type = e.get('event', 'N/A')
                print(f"    {ts} | {event_type}")
        
        print()
    
    # Check for engine-level MGC events
    print("="*80)
    print("ENGINE-LEVEL MGC EVENTS:")
    print("="*80)
    
    engine_events = [e for e in mgc_events if e.get('stream') is None or e.get('stream') == '']
    engine_events = [e for e in engine_events if any(x in e.get('event', '') for x in [
        'ENGINE_START', 'DATALOADED', 'REALTIME', 'BARSREQUEST', 'SIM_ACCOUNT'
    ])]
    
    if engine_events:
        print(f"\nFound {len(engine_events)} engine-level events:")
        for e in engine_events[-10:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            event_type = e.get('event', 'N/A')
            data = e.get('data', {})
            instrument = data.get('instrument', 'N/A') if isinstance(data, dict) else 'N/A'
            print(f"  {ts} | {event_type} | Instrument: {instrument}")
    else:
        print("\nNo engine-level MGC events found")

if __name__ == "__main__":
    main()
