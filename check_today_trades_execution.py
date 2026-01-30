#!/usr/bin/env python3
"""
Comprehensive analysis of why trades aren't being taken today.
Checks execution gates, blocks, errors, and order submission status.
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
    today = datetime.now(timezone.utc).date()
    cutoff = datetime.now(timezone.utc) - timedelta(hours=24)
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("TRADES NOT BEING TAKEN - COMPREHENSIVE ASSESSMENT")
    print(f"Date: {today}")
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
    
    print(f"\nLoaded {len(events):,} events from last 24 hours\n")
    
    # Key execution-related events
    execution_events = [
        'ORDER_SUBMITTED',
        'ORDER_SUBMIT_BLOCKED',
        'ORDER_SUBMIT_FAILED',
        'ENTRY_ORDER_SUBMITTED',
        'ENTRY_ORDER_FAILED',
        'STOP_BRACKETS_SUBMITTED',
        'STOP_BRACKETS_SUBMIT_FAILED',
        'EXECUTION_GATE_EVAL',
        'EXECUTION_GATE_BLOCKED',
        'EXECUTION_GATE_PASSED',
        'RANGE_LOCKED',
        'RANGE_BUILD_START',
        'BREAKOUT_DETECTED',
        'ENTRY_DETECTED',
        'ENTRY_ORDER_PLACED',
        'ENTRY_ORDER_REJECTED',
        'INTENT_CREATED',
        'INTENT_SUBMITTED',
        'INTENT_FAILED',
        'PROTECTIVE_ORDER_SUBMITTED',
        'PROTECTIVE_ORDER_FAILED'
    ]
    
    # Filter execution events
    exec_events = [e for e in events if e.get('event') in execution_events]
    
    print("="*80)
    print("EXECUTION EVENTS SUMMARY:")
    print("="*80)
    
    by_event = defaultdict(list)
    for e in exec_events:
        by_event[e.get('event')].append(e)
    
    for event_type in sorted(by_event.keys()):
        count = len(by_event[event_type])
        latest = max(by_event[event_type], key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            print(f"  {event_type}: {count:4} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT")
    
    # Check for blocks
    print("\n" + "="*80)
    print("ORDER SUBMISSION BLOCKS:")
    print("="*80)
    
    blocked = [e for e in events if 'BLOCKED' in e.get('event', '') or 'BLOCK' in e.get('event', '')]
    failed = [e for e in events if 'FAILED' in e.get('event', '') or 'FAIL' in e.get('event', '')]
    
    print(f"\n  Blocked events: {len(blocked)}")
    if blocked:
        by_reason = defaultdict(list)
        for e in blocked:
            data = e.get('data', {})
            if isinstance(data, dict):
                reason = data.get('reason', data.get('block_reason', 'UNKNOWN'))
                by_reason[reason].append(e)
        
        print(f"\n  Block reasons:")
        for reason, reason_events in sorted(by_reason.items(), key=lambda x: len(x[1]), reverse=True):
            print(f"    {reason}: {len(reason_events)} events")
            latest = max(reason_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                stream = latest.get('stream', 'N/A')
                print(f"      Latest: {ts_chicago.strftime('%H:%M:%S')} CT | Stream: {stream}")
    
    print(f"\n  Failed events: {len(failed)}")
    if failed:
        by_reason = defaultdict(list)
        for e in failed:
            data = e.get('data', {})
            if isinstance(data, dict):
                reason = data.get('reason', data.get('error', data.get('error_message', 'UNKNOWN')))
                by_reason[reason].append(e)
        
        print(f"\n  Failure reasons:")
        for reason, reason_events in sorted(by_reason.items(), key=lambda x: len(x[1]), reverse=True)[:10]:
            print(f"    {reason[:80]}: {len(reason_events)} events")
    
    # Check range lock status
    print("\n" + "="*80)
    print("RANGE LOCK STATUS:")
    print("="*80)
    
    range_locked = [e for e in events if e.get('event') == 'RANGE_LOCKED']
    range_build_start = [e for e in events if e.get('event') == 'RANGE_BUILD_START']
    
    print(f"\n  RANGE_LOCKED events: {len(range_locked)}")
    print(f"  RANGE_BUILD_START events: {len(range_build_start)}")
    
    # Group by stream
    by_stream = defaultdict(lambda: {'locked': [], 'building': []})
    for e in range_locked:
        stream = e.get('stream', 'UNKNOWN')
        by_stream[stream]['locked'].append(e)
    for e in range_build_start:
        stream = e.get('stream', 'UNKNOWN')
        by_stream[stream]['building'].append(e)
    
    print(f"\n  By stream:")
    for stream in sorted(by_stream.keys()):
        locked = by_stream[stream]['locked']
        building = by_stream[stream]['building']
        
        if locked:
            latest_locked = max(locked, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts_locked = parse_timestamp(latest_locked.get('ts_utc', ''))
            if ts_locked:
                ts_chicago = ts_locked.astimezone(chicago_tz)
                print(f"    {stream:6}: LOCKED at {ts_chicago.strftime('%H:%M:%S')} CT ({len(locked)} times)")
        elif building:
            latest_building = max(building, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts_building = parse_timestamp(latest_building.get('ts_utc', ''))
            if ts_building:
                ts_chicago = ts_building.astimezone(chicago_tz)
                age_minutes = (datetime.now(timezone.utc) - ts_building).total_seconds() / 60
                print(f"    {stream:6}: BUILDING since {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
        else:
            print(f"    {stream:6}: NO RANGE ACTIVITY")
    
    # Check execution gate evaluations
    print("\n" + "="*80)
    print("EXECUTION GATE EVALUATIONS:")
    print("="*80)
    
    gate_evals = [e for e in events if e.get('event') == 'EXECUTION_GATE_EVAL']
    gate_passed = [e for e in events if e.get('event') == 'EXECUTION_GATE_PASSED']
    gate_blocked = [e for e in events if e.get('event') == 'EXECUTION_GATE_BLOCKED']
    
    print(f"\n  Total evaluations: {len(gate_evals)}")
    print(f"  Passed: {len(gate_passed)}")
    print(f"  Blocked: {len(gate_blocked)}")
    
    if gate_blocked:
        by_reason = defaultdict(list)
        for e in gate_blocked:
            data = e.get('data', {})
            if isinstance(data, dict):
                reason = data.get('reason', data.get('block_reason', 'UNKNOWN'))
                by_reason[reason].append(e)
        
        print(f"\n  Block reasons:")
        for reason, reason_events in sorted(by_reason.items(), key=lambda x: len(x[1]), reverse=True):
            print(f"    {reason}: {len(reason_events)} blocks")
    
    # Check breakout detection
    print("\n" + "="*80)
    print("BREAKOUT DETECTION:")
    print("="*80)
    
    breakouts = [e for e in events if e.get('event') == 'BREAKOUT_DETECTED']
    entries = [e for e in events if e.get('event') == 'ENTRY_DETECTED']
    
    print(f"\n  BREAKOUT_DETECTED: {len(breakouts)}")
    print(f"  ENTRY_DETECTED: {len(entries)}")
    
    if breakouts:
        print(f"\n  Recent breakouts:")
        for e in breakouts[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                stream = e.get('stream', 'N/A')
                data = e.get('data', {})
                direction = data.get('direction', 'N/A') if isinstance(data, dict) else 'N/A'
                print(f"    {ts_chicago.strftime('%H:%M:%S')} CT | {stream:6} | {direction}")
    
    # Check intent creation and submission
    print("\n" + "="*80)
    print("INTENT CREATION & SUBMISSION:")
    print("="*80)
    
    intent_created = [e for e in events if e.get('event') == 'INTENT_CREATED']
    intent_submitted = [e for e in events if e.get('event') == 'INTENT_SUBMITTED']
    intent_failed = [e for e in events if e.get('event') == 'INTENT_FAILED']
    
    print(f"\n  INTENT_CREATED: {len(intent_created)}")
    print(f"  INTENT_SUBMITTED: {len(intent_submitted)}")
    print(f"  INTENT_FAILED: {len(intent_failed)}")
    
    if intent_failed:
        print(f"\n  Intent failures:")
        for e in intent_failed[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                stream = e.get('stream', 'N/A')
                data = e.get('data', {})
                reason = data.get('reason', data.get('error', 'UNKNOWN')) if isinstance(data, dict) else 'UNKNOWN'
                print(f"    {ts_chicago.strftime('%H:%M:%S')} CT | {stream:6} | {reason[:60]}")
    
    # Check current stream states
    print("\n" + "="*80)
    print("CURRENT STREAM STATES:")
    print("="*80)
    
    state_transitions = [e for e in events if e.get('event') == 'STREAM_STATE_TRANSITION']
    
    # Get latest state for each stream
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
    
    print(f"\n  Stream states:")
    for stream in sorted(stream_states.keys()):
        state_info = stream_states[stream]
        ts_chicago = state_info['ts'].astimezone(chicago_tz)
        age_minutes = (datetime.now(timezone.utc) - state_info['ts']).total_seconds() / 60
        print(f"    {stream:6}: {state_info['state']:20} | Since {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
    
    # Summary
    print("\n" + "="*80)
    print("ROOT CAUSE ANALYSIS:")
    print("="*80)
    
    issues = []
    
    # Check if ranges are locked
    if not range_locked:
        issues.append("NO RANGES LOCKED - Cannot place orders without locked ranges")
    
    # Check if ranges are stuck building
    stuck_building = []
    for stream, info in by_stream.items():
        if info['building'] and not info['locked']:
            latest = max(info['building'], key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                age_minutes = (datetime.now(timezone.utc) - ts).total_seconds() / 60
                if age_minutes > 30:
                    stuck_building.append((stream, age_minutes))
    
    if stuck_building:
        issues.append(f"RANGES STUCK BUILDING: {', '.join([f'{s} ({m:.1f}min)' for s, m in stuck_building])}")
    
    # Check for blocks
    if blocked:
        issues.append(f"ORDER SUBMISSION BLOCKED: {len(blocked)} block events found")
    
    # Check for failures
    if failed:
        issues.append(f"ORDER SUBMISSION FAILED: {len(failed)} failure events found")
    
    # Check if breakouts detected but no entries
    if breakouts and not entries:
        issues.append(f"BREAKOUTS DETECTED BUT NO ENTRIES: {len(breakouts)} breakouts, {len(entries)} entries")
    
    # Check if entries detected but no orders
    if entries and not intent_created:
        issues.append(f"ENTRIES DETECTED BUT NO INTENTS: {len(entries)} entries, {len(intent_created)} intents")
    
    if issues:
        print("\n  [CRITICAL] Issues preventing trades:")
        for i, issue in enumerate(issues, 1):
            print(f"    {i}. {issue}")
    else:
        print("\n  [INFO] No obvious issues found - may need deeper analysis")
    
    return {
        'events': events,
        'exec_events': exec_events,
        'blocked': blocked,
        'failed': failed,
        'range_locked': range_locked,
        'range_building': range_build_start,
        'breakouts': breakouts,
        'entries': entries,
        'intent_created': intent_created,
        'intent_failed': intent_failed,
        'stream_states': stream_states,
        'issues': issues
    }

if __name__ == "__main__":
    result = main()
