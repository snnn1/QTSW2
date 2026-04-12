#!/usr/bin/env python3
"""
Comprehensive recent logging health check.
Analyzes recent logs to verify system is working correctly.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict, Counter
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

def load_recent_events(log_dir: Path, hours: int = 2) -> List[Dict[str, Any]]:
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

def analyze_system_health(events: List[Dict[str, Any]]) -> Dict[str, Any]:
    """Analyze system health from events"""
    health = {
        'total_events': len(events),
        'time_range': {},
        'critical_errors': [],
        'warnings': [],
        'stuck_states': [],
        'component_status': {},
        'event_counts': Counter(),
        'stream_status': {},
        'issues': []
    }
    
    if not events:
        return health
    
    # Time range
    first_ts = parse_timestamp(events[0].get('ts_utc', ''))
    last_ts = parse_timestamp(events[-1].get('ts_utc', ''))
    if first_ts and last_ts:
        health['time_range'] = {
            'start': first_ts,
            'end': last_ts,
            'duration_minutes': (last_ts - first_ts).total_seconds() / 60
        }
    
    # Count events by type
    for e in events:
        event_type = e.get('event', '')
        health['event_counts'][event_type] += 1
    
    # Critical error patterns
    critical_patterns = [
        'CRITICAL', 'FATAL', 'EXCEPTION', 'ERROR', 'FAILED', 'FAILURE',
        'TIMEOUT', 'STALL', 'STUCK', 'CORRUPTION', 'DATA_LOSS',
        'INTENT_INCOMPLETE', 'FLATTEN_FAILED', 'PROTECTIVE_FAILURE'
    ]
    
    # Warning patterns
    warning_patterns = [
        'WARNING', 'WARN', 'BLOCKED', 'REJECTED', 'SKIPPED',
        'PRE_HYDRATION_WAITING', 'BARSREQUEST_TIMEOUT'
    ]
    
    # Check for critical errors
    for e in events:
        event_type = e.get('event', '')
        msg = str(e.get('msg', '')).upper()
        data_str = str(e.get('data', {})).upper()
        
        # Critical errors
        if any(pattern in event_type.upper() or pattern in msg or pattern in data_str 
               for pattern in critical_patterns):
            if 'INTENT_INCOMPLETE' in event_type or 'INTENT_INCOMPLETE' in msg:
                health['critical_errors'].append({
                    'timestamp': e.get('ts_utc', ''),
                    'event': event_type,
                    'stream': e.get('stream', 'N/A'),
                    'message': e.get('msg', '')[:100]
                })
            elif 'FLATTEN_FAILED' in event_type or 'FLATTEN_FAILED' in msg:
                health['critical_errors'].append({
                    'timestamp': e.get('ts_utc', ''),
                    'event': event_type,
                    'stream': e.get('stream', 'N/A'),
                    'message': e.get('msg', '')[:100]
                })
            elif 'PROTECTIVE_FAILURE' in event_type or 'PROTECTIVE_FAILURE' in msg:
                health['critical_errors'].append({
                    'timestamp': e.get('ts_utc', ''),
                    'event': event_type,
                    'stream': e.get('stream', 'N/A'),
                    'message': e.get('msg', '')[:100]
                })
        
        # Warnings
        if any(pattern in event_type.upper() or pattern in msg or pattern in data_str 
               for pattern in warning_patterns):
            if event_type not in ['ORDER_SUBMIT_BLOCKED', 'STOP_BRACKETS_SUBMIT_FAILED']:  # These are common
                health['warnings'].append({
                    'timestamp': e.get('ts_utc', ''),
                    'event': event_type,
                    'stream': e.get('stream', 'N/A'),
                    'message': e.get('msg', '')[:100]
                })
    
    # Check for stuck states
    now = datetime.now(timezone.utc)
    stream_last_event = {}
    stream_states = {}
    
    for e in events:
        stream = e.get('stream', '')
        if stream:
            stream_last_event[stream] = parse_timestamp(e.get('ts_utc', ''))
            event_type = e.get('event', '')
            if 'STREAM_STATE_TRANSITION' == event_type:
                data = e.get('data', {})
                if isinstance(data, dict):
                    new_state = data.get('new_state', '')
                    if new_state:
                        stream_states[stream] = new_state
    
    # Check for streams stuck in PRE_HYDRATION
    for stream, last_ts in stream_last_event.items():
        if last_ts:
            if last_ts.tzinfo is None:
                last_ts = last_ts.replace(tzinfo=timezone.utc)
            age_minutes = (now - last_ts).total_seconds() / 60
            state = stream_states.get(stream, 'UNKNOWN')
            
            if state == 'PRE_HYDRATION' and age_minutes > 5:
                health['stuck_states'].append({
                    'stream': stream,
                    'state': state,
                    'age_minutes': age_minutes,
                    'last_event': last_ts.strftime('%H:%M:%S')
                })
    
    # Component status
    health['component_status'] = {
        'robot_engine': {
            'status': 'OK' if health['event_counts'].get('ONBARUPDATE_CALLED', 0) > 0 else 'UNKNOWN',
            'onbarupdate_calls': health['event_counts'].get('ONBARUPDATE_CALLED', 0)
        },
        'execution': {
            'status': 'OK' if health['event_counts'].get('ORDER_CREATED', 0) > 0 or 
                              health['event_counts'].get('ORDER_SUBMIT_BLOCKED', 0) > 0 else 'UNKNOWN',
            'orders_created': health['event_counts'].get('ORDER_CREATED', 0),
            'orders_blocked': health['event_counts'].get('ORDER_SUBMIT_BLOCKED', 0)
        },
        'hydration': {
            'status': 'OK' if health['event_counts'].get('PRE_HYDRATION_COMPLETE_SIM', 0) > 0 or
                              health['event_counts'].get('PRE_HYDRATION_COMPLETE_DRYRUN', 0) > 0 else 'UNKNOWN',
            'completions': health['event_counts'].get('PRE_HYDRATION_COMPLETE_SIM', 0) + 
                          health['event_counts'].get('PRE_HYDRATION_COMPLETE_DRYRUN', 0)
        },
        'barsrequest': {
            'status': 'OK' if health['event_counts'].get('BARSREQUEST_EXECUTED', 0) > 0 else 'UNKNOWN',
            'executed': health['event_counts'].get('BARSREQUEST_EXECUTED', 0),
            'timeouts': health['event_counts'].get('BARSREQUEST_TIMEOUT', 0)
        }
    }
    
    # Stream status
    streams_with_events = set(e.get('stream', '') for e in events if e.get('stream'))
    for stream in sorted(streams_with_events):
        stream_events = [e for e in events if e.get('stream') == stream]
        if stream_events:
            latest = stream_events[-1]
            latest_ts = parse_timestamp(latest.get('ts_utc', ''))
            if latest_ts:
                if latest_ts.tzinfo is None:
                    latest_ts = latest_ts.replace(tzinfo=timezone.utc)
                age_minutes = (now - latest_ts).total_seconds() / 60
            else:
                age_minutes = 999
            
            has_range_locked = any(e.get('event') == 'RANGE_LOCKED' for e in stream_events)
            has_armed = any(e.get('event') in ['ARMED', 'STREAM_ARMED'] for e in stream_events)
            has_pre_hydration_complete = any('PRE_HYDRATION_COMPLETE' in e.get('event', '') for e in stream_events)
            
            health['stream_status'][stream] = {
                'event_count': len(stream_events),
                'latest_event': latest.get('event', 'N/A'),
                'latest_time': latest.get('ts_utc', '')[:19] if len(latest.get('ts_utc', '')) > 19 else latest.get('ts_utc', ''),
                'age_minutes': age_minutes,
                'has_range_locked': has_range_locked,
                'has_armed': has_armed,
                'has_pre_hydration_complete': has_pre_hydration_complete,
                'status': 'ACTIVE' if age_minutes < 5 else 'STALE' if age_minutes < 30 else 'INACTIVE'
            }
    
    # Identify issues
    if health['critical_errors']:
        health['issues'].append(f"{len(health['critical_errors'])} critical errors detected")
    
    if health['stuck_states']:
        health['issues'].append(f"{len(health['stuck_states'])} streams stuck in PRE_HYDRATION")
    
    if health['component_status']['barsrequest']['timeouts'] > 0:
        health['issues'].append(f"{health['component_status']['barsrequest']['timeouts']} BarsRequest timeouts")
    
    inactive_streams = [s for s, status in health['stream_status'].items() if status['status'] == 'INACTIVE']
    if inactive_streams:
        health['issues'].append(f"{len(inactive_streams)} inactive streams: {', '.join(inactive_streams)}")
    
    return health

def print_health_report(health: Dict[str, Any]):
    """Print health report"""
    print("="*80)
    print("SYSTEM HEALTH REPORT (Last 2 Hours)")
    print("="*80)
    
    # Summary
    print(f"\nSUMMARY:")
    print(f"  Total Events: {health['total_events']:,}")
    if health['time_range']:
        print(f"  Time Range: {health['time_range']['start'].strftime('%H:%M:%S')} - {health['time_range']['end'].strftime('%H:%M:%S')}")
        print(f"  Duration: {health['time_range']['duration_minutes']:.1f} minutes")
    
    # Critical Errors
    print(f"\n{'='*80}")
    print("CRITICAL ERRORS:")
    print(f"{'='*80}")
    if health['critical_errors']:
        print(f"\n  [CRITICAL] Found {len(health['critical_errors'])} critical errors:")
        for err in health['critical_errors'][:10]:  # Show first 10
            print(f"    {err['timestamp'][:19]} | {err['stream']:6} | {err['event']}")
            if err['message']:
                print(f"      {err['message'][:80]}")
    else:
        print("\n  [OK] No critical errors detected")
    
    # Warnings
    print(f"\n{'='*80}")
    print("WARNINGS:")
    print(f"{'='*80}")
    if health['warnings']:
        print(f"\n  [WARN] Found {len(health['warnings'])} warnings:")
        # Group by event type
        warning_counts = Counter(w['event'] for w in health['warnings'])
        for event_type, count in warning_counts.most_common(10):
            print(f"    {event_type}: {count}")
    else:
        print("\n  [OK] No warnings detected")
    
    # Stuck States
    print(f"\n{'='*80}")
    print("STUCK STATES:")
    print(f"{'='*80}")
    if health['stuck_states']:
        print(f"\n  [WARN] Found {len(health['stuck_states'])} stuck streams:")
        for stuck in health['stuck_states']:
            print(f"    {stuck['stream']}: Stuck in {stuck['state']} for {stuck['age_minutes']:.1f} minutes")
    else:
        print("\n  [OK] No stuck states detected")
    
    # Component Status
    print(f"\n{'='*80}")
    print("COMPONENT STATUS:")
    print(f"{'='*80}")
    for component, status in health['component_status'].items():
        print(f"\n  {component.upper()}:")
        print(f"    Status: {status['status']}")
        for key, value in status.items():
            if key != 'status':
                print(f"    {key}: {value}")
    
    # Stream Status
    print(f"\n{'='*80}")
    print("STREAM STATUS:")
    print(f"{'='*80}")
    if health['stream_status']:
        print(f"\n  Found {len(health['stream_status'])} active streams:")
        for stream, status in sorted(health['stream_status'].items()):
            status_icon = '[OK]' if status['status'] == 'ACTIVE' else '[WARN]' if status['status'] == 'STALE' else '[INACTIVE]'
            print(f"\n  {status_icon} {stream}:")
            print(f"    Status: {status['status']}")
            print(f"    Latest Event: {status['latest_event']} at {status['latest_time']}")
            print(f"    Age: {status['age_minutes']:.1f} minutes ago")
            print(f"    Events: {status['event_count']:,}")
            print(f"    Range Locked: {status['has_range_locked']}")
            print(f"    Armed: {status['has_armed']}")
            print(f"    Pre-Hydration Complete: {status['has_pre_hydration_complete']}")
    else:
        print("\n  [WARN] No stream events found")
    
    # Top Events
    print(f"\n{'='*80}")
    print("TOP EVENTS (by count):")
    print(f"{'='*80}")
    for event_type, count in health['event_counts'].most_common(15):
        print(f"  {event_type}: {count:,}")
    
    # Issues Summary
    print(f"\n{'='*80}")
    print("ISSUES SUMMARY:")
    print(f"{'='*80}")
    if health['issues']:
        print("\n  [WARN] Issues detected:")
        for issue in health['issues']:
            print(f"    - {issue}")
    else:
        print("\n  [OK] No issues detected - system appears healthy")
    
    # Overall Health
    print(f"\n{'='*80}")
    print("OVERALL HEALTH ASSESSMENT:")
    print(f"{'='*80}")
    
    if health['critical_errors']:
        print("\n  [CRITICAL] System has critical errors - immediate attention required")
    elif health['stuck_states']:
        print("\n  [WARN] System has stuck states - investigation recommended")
    elif health['issues']:
        print("\n  [WARN] System has minor issues - monitoring recommended")
    else:
        print("\n  [OK] System appears healthy - all components functioning normally")

def main():
    log_dir = Path("logs/robot")
    
    if not log_dir.exists():
        print(f"Error: Log directory not found: {log_dir}")
        return
    
    print("Loading recent robot logs...")
    events = load_recent_events(log_dir, hours=2)
    print(f"Loaded {len(events):,} events from last 2 hours\n")
    
    print("Analyzing system health...")
    health = analyze_system_health(events)
    
    print_health_report(health)

if __name__ == "__main__":
    main()
