#!/usr/bin/env python3
"""
Check all enabled streams for today.
Reads execution timetable and analyzes today's activity for each enabled stream.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict
from typing import Dict, List, Any, Optional

def parse_timestamp(ts_str: str) -> Optional[datetime]:
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

def get_today_date() -> str:
    """Get today's date in YYYY-MM-DD format"""
    return datetime.now().strftime('%Y-%m-%d')

def load_execution_timetable(timetable_path: Path) -> Dict[str, Any]:
    """Load execution timetable"""
    if not timetable_path.exists():
        return {}
    
    try:
        with open(timetable_path, 'r', encoding='utf-8') as f:
            return json.load(f)
    except Exception as e:
        print(f"  Warning: Could not load timetable: {e}")
        return {}

def get_enabled_streams(timetable: Dict[str, Any]) -> List[str]:
    """Extract enabled streams from timetable"""
    enabled = []
    
    if 'streams' in timetable:
        for stream_entry in timetable['streams']:
            if isinstance(stream_entry, dict):
                if stream_entry.get('enabled', False):
                    enabled.append(stream_entry.get('stream', ''))
            elif isinstance(stream_entry, str):
                # Old format - assume all listed streams are enabled
                enabled.append(stream_entry)
    
    return [s for s in enabled if s]

def load_today_events(log_dir: Path, today_date: str) -> List[Dict[str, Any]]:
    """Load events from today"""
    events = []
    cutoff = datetime.now(timezone.utc) - timedelta(hours=24)  # Last 24 hours
    
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            ts = e.get('ts_utc', '')
                            # Check if event is from today
                            if ts.startswith(today_date) or (parse_timestamp(ts) and parse_timestamp(ts) >= cutoff):
                                events.append(e)
                        except:
                            pass
        except:
            pass
    
    return events

def analyze_stream_status(stream: str, events: List[Dict[str, Any]]) -> Dict[str, Any]:
    """Analyze status for a specific stream"""
    stream_events = [e for e in events if e.get('stream') == stream]
    
    if not stream_events:
        return {
            'stream': stream,
            'has_events': False,
            'status': 'NO_EVENTS'
        }
    
    # Sort by timestamp
    stream_events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    latest = stream_events[-1]
    latest_ts = parse_timestamp(latest.get('ts_utc', ''))
    now = datetime.now(timezone.utc)
    
    if latest_ts:
        if latest_ts.tzinfo is None:
            latest_ts = latest_ts.replace(tzinfo=timezone.utc)
        age_minutes = (now - latest_ts).total_seconds() / 60
    else:
        age_minutes = 999
    
    # Check key states
    has_pre_hydration_complete = any('PRE_HYDRATION_COMPLETE' in e.get('event', '') for e in stream_events)
    has_armed = any(e.get('event') in ['ARMED', 'STREAM_ARMED'] for e in stream_events)
    has_range_locked = any(e.get('event') == 'RANGE_LOCKED' for e in stream_events)
    has_range_building = any(e.get('event') in ['RANGE_BUILD_START', 'RANGE_BUILDING_START'] for e in stream_events)
    
    # Check for BarsRequest
    barsrequest_events = [e for e in stream_events if 'BARSREQUEST' in e.get('event', '')]
    barsrequest_pending = any(e.get('event') == 'BARSREQUEST_PENDING_MARKED' for e in barsrequest_events)
    barsrequest_completed = any(e.get('event') == 'BARSREQUEST_COMPLETED_MARKED' for e in barsrequest_events)
    barsrequest_executed = any(e.get('event') == 'BARSREQUEST_EXECUTED' for e in barsrequest_events)
    
    # Check for waiting
    waiting_for_barsrequest = any('PRE_HYDRATION_WAITING_FOR_BARSREQUEST' in e.get('event', '') for e in stream_events)
    
    # Determine current state
    current_state = 'UNKNOWN'
    if has_range_locked:
        current_state = 'RANGE_LOCKED'
    elif has_range_building:
        current_state = 'RANGE_BUILDING'
    elif has_armed:
        current_state = 'ARMED'
    elif has_pre_hydration_complete:
        current_state = 'PRE_HYDRATION_COMPLETE'
    elif waiting_for_barsrequest:
        current_state = 'PRE_HYDRATION_WAITING'
    else:
        current_state = 'PRE_HYDRATION'
    
    # Determine activity status
    if age_minutes < 5:
        activity_status = 'ACTIVE'
    elif age_minutes < 30:
        activity_status = 'STALE'
    else:
        activity_status = 'INACTIVE'
    
    # Count key events
    event_counts = {
        'total': len(stream_events),
        'bar_admission': len([e for e in stream_events if 'BAR_ADMISSION' in e.get('event', '')]),
        'bar_buffer': len([e for e in stream_events if 'BAR_BUFFER' in e.get('event', '')]),
        'execution': len([e for e in stream_events if 'ORDER' in e.get('event', '') or 'EXECUTION' in e.get('event', '')]),
        'state_transitions': len([e for e in stream_events if e.get('event') == 'STREAM_STATE_TRANSITION']),
    }
    
    # Check for issues
    issues = []
    if waiting_for_barsrequest and age_minutes > 5:
        issues.append(f"Waiting for BarsRequest for {age_minutes:.1f} minutes")
    if barsrequest_pending and not barsrequest_completed and age_minutes > 5:
        issues.append(f"BarsRequest pending for {age_minutes:.1f} minutes")
    if has_pre_hydration_complete and not has_armed and not has_range_building and age_minutes > 1:
        issues.append("Pre-hydration complete but not ARMED")
    if activity_status == 'INACTIVE':
        issues.append(f"No activity for {age_minutes:.1f} minutes")
    
    return {
        'stream': stream,
        'has_events': True,
        'status': activity_status,
        'current_state': current_state,
        'latest_event': latest.get('event', 'N/A'),
        'latest_time': latest.get('ts_utc', '')[:19] if len(latest.get('ts_utc', '')) > 19 else latest.get('ts_utc', ''),
        'age_minutes': age_minutes,
        'has_pre_hydration_complete': has_pre_hydration_complete,
        'has_armed': has_armed,
        'has_range_locked': has_range_locked,
        'has_range_building': has_range_building,
        'barsrequest_status': {
            'pending': barsrequest_pending,
            'completed': barsrequest_completed,
            'executed': barsrequest_executed,
            'waiting': waiting_for_barsrequest
        },
        'event_counts': event_counts,
        'issues': issues
    }

def main():
    today_date = get_today_date()
    
    print("="*80)
    print(f"ENABLED STREAMS STATUS CHECK - {today_date}")
    print("="*80)
    
    # Load execution timetable
    timetable_path = Path("data/timetable/timetable_current.json")
    print(f"\nLoading execution timetable from: {timetable_path}")
    
    timetable = load_execution_timetable(timetable_path)
    
    if not timetable:
        print("  [WARN] No timetable found - checking all known streams")
        # Fallback to all known streams
        all_streams = ['ES1', 'ES2', 'GC1', 'GC2', 'CL1', 'CL2', 'NQ1', 'NQ2', 'NG1', 'NG2', 'YM1', 'YM2', 'RTY1', 'RTY2']
        enabled_streams = all_streams
    else:
        enabled_streams = get_enabled_streams(timetable)
        print(f"  Found {len(enabled_streams)} enabled streams in timetable")
        if 'trading_date' in timetable:
            print(f"  Timetable trading_date: {timetable.get('trading_date')}")
        if 'as_of' in timetable:
            print(f"  Timetable as_of: {timetable.get('as_of')}")
    
    if not enabled_streams:
        print("\n  [WARN] No enabled streams found in timetable")
        return
    
    # Load today's events
    log_dir = Path("logs/robot")
    print(f"\nLoading events from: {log_dir}")
    events = load_today_events(log_dir, today_date)
    print(f"  Loaded {len(events):,} events from today")
    
    # Analyze each enabled stream
    print(f"\n{'='*80}")
    print("STREAM STATUS:")
    print(f"{'='*80}")
    
    stream_statuses = []
    for stream in sorted(enabled_streams):
        status = analyze_stream_status(stream, events)
        stream_statuses.append(status)
    
    # Print status for each stream
    for status in stream_statuses:
        stream = status['stream']
        
        if not status['has_events']:
            print(f"\n{stream}: [NO_EVENTS] No events found today")
            continue
        
        # Status icon
        if status['status'] == 'ACTIVE':
            icon = '[OK]'
        elif status['status'] == 'STALE':
            icon = '[WARN]'
        else:
            icon = '[INACTIVE]'
        
        print(f"\n{icon} {stream}:")
        print(f"  Status: {status['status']}")
        print(f"  Current State: {status['current_state']}")
        print(f"  Latest Event: {status['latest_event']} at {status['latest_time']}")
        print(f"  Age: {status['age_minutes']:.1f} minutes ago")
        print(f"  Total Events: {status['event_counts']['total']:,}")
        
        print(f"\n  State Indicators:")
        print(f"    Pre-Hydration Complete: {status['has_pre_hydration_complete']}")
        print(f"    Armed: {status['has_armed']}")
        print(f"    Range Building: {status['has_range_building']}")
        print(f"    Range Locked: {status['has_range_locked']}")
        
        print(f"\n  BarsRequest Status:")
        br_status = status['barsrequest_status']
        print(f"    Pending: {br_status['pending']}")
        print(f"    Completed: {br_status['completed']}")
        print(f"    Executed: {br_status['executed']}")
        print(f"    Waiting: {br_status['waiting']}")
        
        print(f"\n  Event Counts:")
        counts = status['event_counts']
        print(f"    Bar Admission: {counts['bar_admission']:,}")
        print(f"    Bar Buffer: {counts['bar_buffer']:,}")
        print(f"    Execution: {counts['execution']:,}")
        print(f"    State Transitions: {counts['state_transitions']:,}")
        
        if status['issues']:
            print(f"\n  [WARN] Issues:")
            for issue in status['issues']:
                print(f"    - {issue}")
        else:
            print(f"\n  [OK] No issues detected")
    
    # Summary
    print(f"\n{'='*80}")
    print("SUMMARY:")
    print(f"{'='*80}")
    
    active_count = sum(1 for s in stream_statuses if s.get('status') == 'ACTIVE')
    stale_count = sum(1 for s in stream_statuses if s.get('status') == 'STALE')
    inactive_count = sum(1 for s in stream_statuses if s.get('status') == 'INACTIVE')
    no_events_count = sum(1 for s in stream_statuses if not s.get('has_events'))
    
    print(f"\n  Total Enabled Streams: {len(enabled_streams)}")
    print(f"  Active: {active_count}")
    print(f"  Stale: {stale_count}")
    print(f"  Inactive: {inactive_count}")
    print(f"  No Events: {no_events_count}")
    
    # Streams with issues
    streams_with_issues = [s for s in stream_statuses if s.get('issues')]
    if streams_with_issues:
        print(f"\n  Streams with Issues: {len(streams_with_issues)}")
        for s in streams_with_issues:
            print(f"    - {s['stream']}: {', '.join(s['issues'])}")
    else:
        print(f"\n  [OK] No streams with issues")
    
    # Streams by state
    print(f"\n  Streams by State:")
    state_counts = defaultdict(int)
    for s in stream_statuses:
        if s.get('has_events'):
            state_counts[s.get('current_state', 'UNKNOWN')] += 1
    
    for state, count in sorted(state_counts.items()):
        print(f"    {state}: {count}")

if __name__ == "__main__":
    main()
