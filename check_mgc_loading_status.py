#!/usr/bin/env python3
"""
Check MGC loading status and hydration progress.
Analyzes robot logs to diagnose why MGC might be taking a while to load.
"""
import json
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict
from typing import Dict, List, Any

def parse_timestamp(ts_str: str) -> datetime:
    """Parse ISO timestamp string to datetime"""
    try:
        if not ts_str:
            return datetime.now(timezone.utc)
        if 'T' in ts_str:
            # ISO format: 2026-01-29T17:06:38.7286693Z or 2026-01-29T17:06:38+00:00
            if ts_str.endswith('Z'):
                ts_str = ts_str[:-1] + '+00:00'
            elif '+' not in ts_str and '-' in ts_str[10:]:
                # Has timezone offset
                pass
            elif '+' not in ts_str:
                # No timezone, assume UTC
                ts_str = ts_str + '+00:00'
            # Parse ISO format
            if '.' in ts_str:
                # Has microseconds
                base, rest = ts_str.split('.', 1)
                if '+' in rest:
                    micro, tz = rest.split('+', 1)
                    micro = micro[:6]  # Limit to 6 digits
                    ts_str = f"{base}.{micro}+{tz}"
                elif '-' in rest[4:]:  # Check if it's a timezone offset (not date)
                    parts = rest.split('-', 1)
                    if len(parts) == 2 and ':' in parts[1]:
                        micro, tz = parts
                        micro = micro[:6]
                        ts_str = f"{base}.{micro}-{tz}"
            return datetime.fromisoformat(ts_str)
        return datetime.now(timezone.utc)
    except Exception as e:
        # Fallback: try simple parse
        try:
            return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
        except:
            return datetime.now(timezone.utc)

def load_robot_logs(log_dir: Path) -> List[Dict[str, Any]]:
    """Load all robot log files"""
    events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            events.append(json.loads(line))
                        except:
                            pass
        except Exception as e:
            print(f"  Warning: Could not read {log_file.name}: {e}")
    return events

def analyze_mgc_loading(events: List[Dict[str, Any]]) -> Dict[str, Any]:
    """Analyze MGC loading status"""
    mgc_events = []
    mgc_streams = ['MGC1', 'MGC2', 'GC1', 'GC2']  # GC is canonical, MGC is execution
    
    # Filter MGC-related events
    for e in events:
        stream = e.get('stream', '')
        data = e.get('data', {})
        if isinstance(data, dict):
            instrument = data.get('instrument', '')
            canonical_instrument = data.get('canonical_instrument', '')
            execution_instrument = data.get('execution_instrument', '')
        else:
            instrument = ''
            canonical_instrument = ''
            execution_instrument = ''
        
        # Check if event is MGC-related
        if (stream in mgc_streams or 
            instrument == 'MGC' or instrument == 'GC' or
            canonical_instrument == 'GC' or
            execution_instrument == 'MGC' or
            'MGC' in str(e.get('data', {})) or
            ('GC' in str(e.get('data', {})) and 'MGC' in str(e.get('data', {})))):
            mgc_events.append(e)
    
    # Sort by timestamp
    mgc_events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')))
    
    # Analyze by event type
    analysis = {
        'total_events': len(mgc_events),
        'recent_events': mgc_events[-50:] if len(mgc_events) > 50 else mgc_events,
        'by_event_type': defaultdict(list),
        'by_stream': defaultdict(list),
        'timeline': [],
        'issues': []
    }
    
    for e in mgc_events:
        event_type = e.get('event', '')
        stream = e.get('stream', '')
        analysis['by_event_type'][event_type].append(e)
        if stream:
            analysis['by_stream'][stream].append(e)
        
        # Build timeline
        analysis['timeline'].append({
            'timestamp': e.get('ts_utc', ''),
            'event': event_type,
            'stream': stream,
            'data': e.get('data', {})
        })
    
    return analysis

def check_hydration_status(events: List[Dict[str, Any]], stream: str) -> Dict[str, Any]:
    """Check hydration status for a specific stream"""
    stream_events = [e for e in events if e.get('stream') == stream]
    stream_events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')))
    
    status = {
        'stream': stream,
        'last_event': None,
        'last_event_time': None,
        'state': 'UNKNOWN',
        'hydration_events': [],
        'barsrequest_events': [],
        'pre_hydration_complete': False,
        'armed': False,
        'range_building': False,
        'range_locked': False,
        'issues': []
    }
    
    # Find latest state
    for e in reversed(stream_events):
        event_type = e.get('event', '')
        if not status['last_event']:
            status['last_event'] = event_type
            status['last_event_time'] = e.get('ts_utc', '')
        
        # Check for state transitions
        if 'PRE_HYDRATION_COMPLETE' in event_type:
            status['pre_hydration_complete'] = True
            status['hydration_events'].append(e)
        elif event_type == 'ARMED':
            status['armed'] = True
        elif event_type == 'RANGE_BUILDING_START':
            status['range_building'] = True
        elif event_type == 'RANGE_LOCKED':
            status['range_locked'] = True
        elif 'BARSREQUEST' in event_type:
            status['barsrequest_events'].append(e)
    
    # Determine current state
    if status['range_locked']:
        status['state'] = 'RANGE_LOCKED'
    elif status['range_building']:
        status['state'] = 'RANGE_BUILDING'
    elif status['armed']:
        status['state'] = 'ARMED'
    elif status['pre_hydration_complete']:
        status['state'] = 'PRE_HYDRATION_COMPLETE'
    else:
        status['state'] = 'PRE_HYDRATION'
    
    # Check for issues
    if not status['pre_hydration_complete'] and len(status['barsrequest_events']) == 0:
        status['issues'].append('No BarsRequest events found - BarsRequest may not have been called')
    
    if status['pre_hydration_complete'] and not status['armed']:
        status['issues'].append('Pre-hydration complete but not ARMED - may be stuck')
    
    # Check for stuck in PRE_HYDRATION
    if status['state'] == 'PRE_HYDRATION':
        if status['last_event_time']:
            last_time = parse_timestamp(status['last_event_time'])
            if last_time.tzinfo is None:
                last_time = last_time.replace(tzinfo=timezone.utc)
            now = datetime.now(timezone.utc)
            if (now - last_time).total_seconds() > 300:  # 5 minutes
                status['issues'].append(f'Stuck in PRE_HYDRATION for {(now - last_time).total_seconds()/60:.1f} minutes')
    
    return status

def print_analysis(analysis: Dict[str, Any]):
    """Print analysis results"""
    print("="*80)
    print("MGC LOADING STATUS ANALYSIS")
    print("="*80)
    print(f"\nTotal MGC-related events found: {analysis['total_events']}")
    
    # Show recent events
    print(f"\n{'='*80}")
    print("RECENT EVENTS (Last 20):")
    print(f"{'='*80}")
    for e in analysis['recent_events'][-20:]:
        ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
        event_type = e.get('event', 'N/A')
        stream = e.get('stream') or 'N/A'
        print(f"  {ts} | {str(stream):6} | {event_type}")
    
    # Show events by type
    print(f"\n{'='*80}")
    print("EVENTS BY TYPE:")
    print(f"{'='*80}")
    for event_type, events_list in sorted(analysis['by_event_type'].items(), key=lambda x: len(x[1]), reverse=True):
        print(f"  {event_type}: {len(events_list)} events")
        if event_type in ['PRE_HYDRATION_COMPLETE', 'ARMED', 'RANGE_BUILDING_START', 'RANGE_LOCKED', 
                          'BARSREQUEST_PENDING_MARKED', 'BARSREQUEST_COMPLETED_MARKED', 
                          'BARSREQUEST_TIMEOUT', 'PRE_HYDRATION_WAITING_FOR_BARSREQUEST']:
            # Show details for key events
            for e in events_list[-3:]:  # Last 3
                ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
                stream = e.get('stream', 'N/A')
                data = e.get('data', {})
                print(f"    {ts} | {stream} | {json.dumps(data, default=str)[:100]}")
    
    # Check hydration status for each MGC stream
    print(f"\n{'='*80}")
    print("HYDRATION STATUS BY STREAM:")
    print(f"{'='*80}")
    
    mgc_streams = ['MGC1', 'MGC2', 'GC1', 'GC2']  # GC is canonical, MGC is execution
    for stream in mgc_streams:
        stream_events = analysis['by_stream'].get(stream, [])
        if not stream_events:
            print(f"\n  {stream}: No events found")
            continue
        
        # Get latest event
        latest = stream_events[-1]
        latest_time = parse_timestamp(latest.get('ts_utc', ''))
        now = datetime.now(timezone.utc)
        # Ensure both are timezone-aware
        if latest_time and latest_time.tzinfo is None:
            latest_time = latest_time.replace(tzinfo=timezone.utc)
        age_minutes = (now - latest_time).total_seconds() / 60 if latest_time and latest_time.tzinfo else 0
        
        print(f"\n  {stream}:")
        print(f"    Latest event: {latest.get('event', 'N/A')} at {latest.get('ts_utc', '')[:19]}")
        print(f"    Age: {age_minutes:.1f} minutes ago")
        print(f"    Total events: {len(stream_events)}")
        
        # Count key events
        pre_hydration_complete = len([e for e in stream_events if 'PRE_HYDRATION_COMPLETE' in e.get('event', '')])
        stream_armed = len([e for e in stream_events if e.get('event') == 'STREAM_ARMED' or e.get('event') == 'ARMED'])
        range_building = len([e for e in stream_events if e.get('event') == 'RANGE_BUILDING_START' or 'RANGE_BUILD_START' in e.get('event', '')])
        range_locked = len([e for e in stream_events if e.get('event') == 'RANGE_LOCKED'])
        barsrequest = len([e for e in stream_events if 'BARSREQUEST' in e.get('event', '')])
        state_transitions = [e for e in stream_events if e.get('event') == 'STREAM_STATE_TRANSITION']
        
        print(f"    Key events:")
        print(f"      PRE_HYDRATION_COMPLETE: {pre_hydration_complete}")
        print(f"      STREAM_ARMED/ARMED: {stream_armed}")
        print(f"      RANGE_BUILDING_START: {range_building}")
        print(f"      RANGE_LOCKED: {range_locked}")
        print(f"      BARSREQUEST events: {barsrequest}")
        print(f"      STATE_TRANSITIONS: {len(state_transitions)}")
        
        # Show state transitions
        if state_transitions:
            print(f"    State transitions:")
            for e in state_transitions[-5:]:
                ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
                data = e.get('data', {})
                old_state = data.get('old_state', 'N/A') if isinstance(data, dict) else 'N/A'
                new_state = data.get('new_state', 'N/A') if isinstance(data, dict) else 'N/A'
                print(f"      {ts} | {old_state} -> {new_state}")
        
        # Determine current state from latest events
        current_state = 'UNKNOWN'
        if range_locked > 0:
            current_state = 'RANGE_LOCKED'
        elif range_building > 0:
            current_state = 'RANGE_BUILDING'
        elif stream_armed > 0 or pre_hydration_complete > 0:
            current_state = 'ARMED'
        elif pre_hydration_complete > 0:
            current_state = 'PRE_HYDRATION_COMPLETE'
        else:
            current_state = 'PRE_HYDRATION'
        
        print(f"    Current state: {current_state}")
        
        # Check for issues
        if age_minutes > 5 and latest.get('event') in ['PRE_HYDRATION', 'PRE_HYDRATION_COMPLETE']:
            print(f"    [WARN] ISSUE: Stuck in PRE_HYDRATION for {age_minutes:.1f} minutes")
        if barsrequest == 0:
            print(f"    [WARN] ISSUE: No BarsRequest events found")
        if pre_hydration_complete > 0 and stream_armed == 0 and range_building == 0:
            print(f"    [WARN] ISSUE: Pre-hydration complete but not ARMED or RANGE_BUILDING")
        
        # Show latest hydration events
        hydration_events = [e for e in stream_events if 'HYDRATION' in e.get('event', '') or 'PRE_HYDRATION' in e.get('event', '')]
        if hydration_events:
            print(f"    Latest hydration events:")
            for e in hydration_events[-3:]:
                ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
                event_type = e.get('event', 'N/A')
                data = e.get('data', {})
                if isinstance(data, dict):
                    bar_count = data.get('bar_count') or data.get('bars_received') or data.get('loaded_bars')
                    if bar_count is not None:
                        print(f"      {ts} | {event_type} | {bar_count} bars")
                    else:
                        print(f"      {ts} | {event_type}")
                else:
                    print(f"      {ts} | {event_type}")
    
    # Check for BarsRequest issues
    print(f"\n{'='*80}")
    print("BARSREQUEST ANALYSIS:")
    print(f"{'='*80}")
    
    barsrequest_events = analysis['by_event_type'].get('BARSREQUEST_PENDING_MARKED', []) + \
                        analysis['by_event_type'].get('BARSREQUEST_COMPLETED_MARKED', []) + \
                        analysis['by_event_type'].get('BARSREQUEST_TIMEOUT', []) + \
                        analysis['by_event_type'].get('PRE_HYDRATION_WAITING_FOR_BARSREQUEST', [])
    
    if barsrequest_events:
        barsrequest_events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')))
        print(f"\n  Found {len(barsrequest_events)} BarsRequest-related events:")
        for e in barsrequest_events[-10:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            event_type = e.get('event', 'N/A')
            stream = e.get('stream', 'N/A')
            data = e.get('data', {})
            print(f"    {ts} | {stream} | {event_type}")
            if 'instrument' in data:
                print(f"      Instrument: {data.get('instrument')}")
            if 'canonical_instrument' in data:
                print(f"      Canonical: {data.get('canonical_instrument')}")
    else:
        print(f"\n  [WARN] No BarsRequest events found for MGC")
    
    # Check for initialization issues
    print(f"\n{'='*80}")
    print("INITIALIZATION CHECK:")
    print(f"{'='*80}")
    
    init_events = analysis['by_event_type'].get('DATALOADED_INITIALIZATION_COMPLETE', []) + \
                 analysis['by_event_type'].get('REALTIME_STATE_REACHED', []) + \
                 analysis['by_event_type'].get('SIM_ACCOUNT_VERIFIED', [])
    
    if init_events:
        init_events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')))
        print(f"\n  Found {len(init_events)} initialization events:")
        for e in init_events[-5:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            event_type = e.get('event', 'N/A')
            data = e.get('data', {})
            print(f"    {ts} | {event_type}")
            if 'instrument' in data:
                print(f"      Instrument: {data.get('instrument')}")
    else:
        print(f"\n  [WARN] No initialization events found for MGC")

def main():
    log_dir = Path("logs/robot")
    
    if not log_dir.exists():
        print(f"Error: Log directory not found: {log_dir}")
        return
    
    print("Loading robot logs...")
    events = load_robot_logs(log_dir)
    print(f"Loaded {len(events)} total events")
    
    print("\nAnalyzing MGC loading status...")
    analysis = analyze_mgc_loading(events)
    
    print_analysis(analysis)
    
    print(f"\n{'='*80}")
    print("SUMMARY:")
    print(f"{'='*80}")
    
    if analysis['total_events'] == 0:
        print("\n  [WARN] No MGC-related events found in logs")
        print("     This could mean:")
        print("     1. MGC strategies haven't started yet")
        print("     2. Logs are from a different date")
        print("     3. MGC strategies failed to initialize")
    else:
        print(f"\n  [OK] Found {analysis['total_events']} MGC-related events")
        
        # Check for common issues
        issues_found = []
        
        if len(analysis['by_event_type'].get('BARSREQUEST_PENDING_MARKED', [])) > 0:
            pending = analysis['by_event_type']['BARSREQUEST_PENDING_MARKED']
            completed = analysis['by_event_type'].get('BARSREQUEST_COMPLETED_MARKED', [])
            if len(pending) > len(completed):
                issues_found.append("BarsRequest pending but not completed")
        
        if len(analysis['by_event_type'].get('PRE_HYDRATION_WAITING_FOR_BARSREQUEST', [])) > 10:
            issues_found.append("Waiting for BarsRequest for extended period")
        
        pre_hydration_events = analysis['by_event_type'].get('PRE_HYDRATION_COMPLETE_SIM', []) + \
                               analysis['by_event_type'].get('PRE_HYDRATION_COMPLETE_DRYRUN', [])
        if len(pre_hydration_events) == 0:
            issues_found.append("Pre-hydration never completed")
        
        if issues_found:
            print("\n  [WARN] Potential issues detected:")
            for issue in issues_found:
                print(f"     - {issue}")
        else:
            print("\n  [OK] No obvious issues detected in logs")

if __name__ == "__main__":
    main()
