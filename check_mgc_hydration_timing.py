#!/usr/bin/env python3
"""
Detailed MGC hydration timing analysis.
Checks for slow loading, stuck states, and timing issues.
"""
import json
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict
from typing import Dict, List, Any, Optional

def parse_timestamp(ts_str: str) -> Optional[datetime]:
    """Parse ISO timestamp string to datetime"""
    if not ts_str:
        return None
    try:
        if 'T' in ts_str:
            if ts_str.endswith('Z'):
                ts_str = ts_str[:-1] + '+00:00'
            elif '+' not in ts_str and '-' in ts_str[10:]:
                # Check if it's a timezone offset
                if ts_str.count('-') >= 3:  # Date has dashes, timezone might have dash
                    parts = ts_str.rsplit('-', 1)
                    if len(parts) == 2 and ':' in parts[1]:
                        ts_str = parts[0] + '+' + parts[1]
            elif '+' not in ts_str:
                ts_str = ts_str + '+00:00'
            # Handle microseconds
            if '.' in ts_str:
                base, rest = ts_str.split('.', 1)
                if '+' in rest:
                    micro, tz = rest.split('+', 1)
                    micro = micro[:6]  # Limit to 6 digits
                    ts_str = f"{base}.{micro}+{tz}"
                elif '-' in rest[4:]:
                    parts = rest.split('-', 1)
                    if len(parts) == 2 and ':' in parts[1]:
                        micro, tz = parts
                        micro = micro[:6]
                        ts_str = f"{base}.{micro}-{tz}"
            dt = datetime.fromisoformat(ts_str)
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            return dt
        return datetime.now(timezone.utc)
    except Exception as e:
        return None

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
            pass
    return events

def analyze_mgc_hydration_timing(events: List[Dict[str, Any]]) -> Dict[str, Any]:
    """Analyze MGC hydration timing"""
    # Filter MGC/GC events
    mgc_events = []
    for e in events:
        stream = e.get('stream', '')
        data = e.get('data', {})
        if isinstance(data, dict):
            instrument = data.get('instrument', '')
            canonical_instrument = data.get('canonical_instrument', '')
            execution_instrument = data.get('execution_instrument', '')
        else:
            instrument = canonical_instrument = execution_instrument = ''
        
        if (stream in ['GC1', 'GC2', 'MGC1', 'MGC2'] or
            instrument in ['MGC', 'GC'] or
            canonical_instrument == 'GC' or
            execution_instrument == 'MGC'):
            mgc_events.append(e)
    
    mgc_events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    # Group by stream
    by_stream = defaultdict(list)
    for e in mgc_events:
        stream = e.get('stream', '')
        if stream:
            by_stream[stream].append(e)
    
    # Analyze each stream
    analysis = {}
    for stream in ['GC1', 'GC2']:
        stream_events = by_stream.get(stream, [])
        if not stream_events:
            continue
        
        # Find key timing events
        timing = {
            'stream': stream,
            'engine_start': None,
            'dataloaded_init': None,
            'realtime_reached': None,
            'barsrequest_pending': None,
            'barsrequest_completed': None,
            'pre_hydration_complete': None,
            'armed': None,
            'range_building_start': None,
            'range_locked': None,
            'current_state': 'UNKNOWN',
            'issues': []
        }
        
        for e in stream_events:
            event_type = e.get('event', '')
            ts_str = e.get('ts_utc', '')
            ts = parse_timestamp(ts_str)
            
            if event_type == 'ENGINE_START' and not timing['engine_start']:
                timing['engine_start'] = ts
            elif event_type == 'DATALOADED_INITIALIZATION_COMPLETE' and not timing['dataloaded_init']:
                timing['dataloaded_init'] = ts
            elif event_type == 'REALTIME_STATE_REACHED' and not timing['realtime_reached']:
                timing['realtime_reached'] = ts
            elif event_type == 'BARSREQUEST_PENDING_MARKED':
                # Get latest
                if not timing['barsrequest_pending'] or (ts and timing['barsrequest_pending'] and ts > timing['barsrequest_pending']):
                    timing['barsrequest_pending'] = ts
            elif event_type == 'BARSREQUEST_COMPLETED_MARKED':
                # Get latest
                if not timing['barsrequest_completed'] or (ts and timing['barsrequest_completed'] and ts > timing['barsrequest_completed']):
                    timing['barsrequest_completed'] = ts
            elif 'PRE_HYDRATION_COMPLETE' in event_type:
                # Get latest
                if not timing['pre_hydration_complete'] or (ts and timing['pre_hydration_complete'] and ts > timing['pre_hydration_complete']):
                    timing['pre_hydration_complete'] = ts
            elif event_type == 'STREAM_ARMED' or event_type == 'ARMED':
                if not timing['armed'] or (ts and timing['armed'] and ts > timing['armed']):
                    timing['armed'] = ts
            elif event_type == 'RANGE_BUILD_START' or event_type == 'RANGE_BUILDING_START':
                if not timing['range_building_start'] or (ts and timing['range_building_start'] and ts > timing['range_building_start']):
                    timing['range_building_start'] = ts
            elif event_type == 'RANGE_LOCKED':
                if not timing['range_locked'] or (ts and timing['range_locked'] and ts > timing['range_locked']):
                    timing['range_locked'] = ts
        
        # Determine current state
        if timing['range_locked']:
            timing['current_state'] = 'RANGE_LOCKED'
        elif timing['range_building_start']:
            timing['current_state'] = 'RANGE_BUILDING'
        elif timing['armed']:
            timing['current_state'] = 'ARMED'
        elif timing['pre_hydration_complete']:
            timing['current_state'] = 'PRE_HYDRATION_COMPLETE'
        else:
            timing['current_state'] = 'PRE_HYDRATION'
        
        # Calculate timing gaps
        gaps = {}
        if timing['dataloaded_init'] and timing['realtime_reached']:
            gaps['init_to_realtime'] = (timing['realtime_reached'] - timing['dataloaded_init']).total_seconds()
        if timing['barsrequest_pending'] and timing['barsrequest_completed']:
            gaps['barsrequest_duration'] = (timing['barsrequest_completed'] - timing['barsrequest_pending']).total_seconds()
        if timing['realtime_reached'] and timing['pre_hydration_complete']:
            gaps['realtime_to_hydration'] = (timing['pre_hydration_complete'] - timing['realtime_reached']).total_seconds()
        if timing['pre_hydration_complete'] and timing['armed']:
            gaps['hydration_to_armed'] = (timing['armed'] - timing['pre_hydration_complete']).total_seconds()
        if timing['armed'] and timing['range_building_start']:
            gaps['armed_to_range_building'] = (timing['range_building_start'] - timing['armed']).total_seconds()
        if timing['range_building_start'] and timing['range_locked']:
            gaps['range_building_duration'] = (timing['range_locked'] - timing['range_building_start']).total_seconds()
        
        timing['gaps'] = gaps
        
        # Check for issues
        if timing['barsrequest_pending'] and not timing['barsrequest_completed']:
            now = datetime.now(timezone.utc)
            if timing['barsrequest_pending'].tzinfo is None:
                timing['barsrequest_pending'] = timing['barsrequest_pending'].replace(tzinfo=timezone.utc)
            elapsed = (now - timing['barsrequest_pending']).total_seconds()
            if elapsed > 300:  # 5 minutes
                timing['issues'].append(f'BarsRequest pending for {elapsed/60:.1f} minutes')
        
        if timing['pre_hydration_complete'] and not timing['armed'] and not timing['range_building_start']:
            now = datetime.now(timezone.utc)
            if timing['pre_hydration_complete'].tzinfo is None:
                timing['pre_hydration_complete'] = timing['pre_hydration_complete'].replace(tzinfo=timezone.utc)
            elapsed = (now - timing['pre_hydration_complete']).total_seconds()
            if elapsed > 60:  # 1 minute
                timing['issues'].append(f'Pre-hydration complete but not ARMED for {elapsed/60:.1f} minutes')
        
        if gaps.get('barsrequest_duration', 0) > 300:  # 5 minutes
            timing['issues'].append(f'BarsRequest took {gaps["barsrequest_duration"]/60:.1f} minutes (slow)')
        
        if gaps.get('realtime_to_hydration', 0) > 300:  # 5 minutes
            timing['issues'].append(f'Realtime to hydration took {gaps["realtime_to_hydration"]/60:.1f} minutes (slow)')
        
        analysis[stream] = timing
    
    return analysis

def print_timing_analysis(analysis: Dict[str, Any]):
    """Print timing analysis"""
    print("="*80)
    print("MGC HYDRATION TIMING ANALYSIS")
    print("="*80)
    
    for stream, timing in analysis.items():
        print(f"\n{stream}:")
        print(f"  Current State: {timing['current_state']}")
        
        # Show timing events
        print(f"\n  Timing Events:")
        if timing['engine_start']:
            print(f"    ENGINE_START: {timing['engine_start'].strftime('%H:%M:%S')}")
        if timing['dataloaded_init']:
            print(f"    DATALOADED_INIT: {timing['dataloaded_init'].strftime('%H:%M:%S')}")
        if timing['realtime_reached']:
            print(f"    REALTIME_REACHED: {timing['realtime_reached'].strftime('%H:%M:%S')}")
        if timing['barsrequest_pending']:
            print(f"    BARSREQUEST_PENDING: {timing['barsrequest_pending'].strftime('%H:%M:%S')}")
        if timing['barsrequest_completed']:
            print(f"    BARSREQUEST_COMPLETED: {timing['barsrequest_completed'].strftime('%H:%M:%S')}")
        if timing['pre_hydration_complete']:
            print(f"    PRE_HYDRATION_COMPLETE: {timing['pre_hydration_complete'].strftime('%H:%M:%S')}")
        if timing['armed']:
            print(f"    ARMED: {timing['armed'].strftime('%H:%M:%S')}")
        if timing['range_building_start']:
            print(f"    RANGE_BUILDING_START: {timing['range_building_start'].strftime('%H:%M:%S')}")
        if timing['range_locked']:
            print(f"    RANGE_LOCKED: {timing['range_locked'].strftime('%H:%M:%S')}")
        
        # Show timing gaps
        if timing['gaps']:
            print(f"\n  Timing Gaps:")
            for gap_name, duration in timing['gaps'].items():
                print(f"    {gap_name}: {duration:.1f} seconds ({duration/60:.1f} minutes)")
        
        # Show issues
        if timing['issues']:
            print(f"\n  [WARN] Issues Detected:")
            for issue in timing['issues']:
                print(f"    - {issue}")
        else:
            print(f"\n  [OK] No timing issues detected")
        
        # Overall assessment
        total_time = None
        if timing['dataloaded_init'] and timing['range_locked']:
            total_time = (timing['range_locked'] - timing['dataloaded_init']).total_seconds()
            print(f"\n  Total Time (Init to Range Locked): {total_time:.1f} seconds ({total_time/60:.1f} minutes)")
            if total_time > 600:  # 10 minutes
                print(f"    [WARN] Total loading time is slow (>10 minutes)")
            elif total_time > 300:  # 5 minutes
                print(f"    [INFO] Total loading time is moderate (5-10 minutes)")
            else:
                print(f"    [OK] Total loading time is acceptable (<5 minutes)")

def main():
    log_dir = Path("logs/robot")
    
    if not log_dir.exists():
        print(f"Error: Log directory not found: {log_dir}")
        return
    
    print("Loading robot logs...")
    events = load_robot_logs(log_dir)
    print(f"Loaded {len(events)} total events")
    
    print("\nAnalyzing MGC hydration timing...")
    analysis = analyze_mgc_hydration_timing(events)
    
    if not analysis:
        print("\n[WARN] No MGC/GC stream events found")
        return
    
    print_timing_analysis(analysis)
    
    print(f"\n{'='*80}")
    print("SUMMARY:")
    print(f"{'='*80}")
    
    for stream, timing in analysis.items():
        print(f"\n{stream}:")
        print(f"  State: {timing['current_state']}")
        if timing['issues']:
            print(f"  Issues: {len(timing['issues'])}")
            for issue in timing['issues']:
                print(f"    - {issue}")
        else:
            print(f"  Status: [OK] No issues")

if __name__ == "__main__":
    main()
