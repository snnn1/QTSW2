#!/usr/bin/env python3
"""
Detailed check for ES1 range building and order placement status.
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

def main():
    log_dir = Path("logs/robot")
    today_date = datetime.now().strftime('%Y-%m-%d')
    cutoff = datetime.now(timezone.utc) - timedelta(hours=24)
    
    print("="*80)
    print("ES1 RANGE BUILDING & ORDER PLACEMENT ANALYSIS")
    print("="*80)
    
    # Load ES1 events
    es1_events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            if e.get('stream') == 'ES1':
                                ts = parse_timestamp(e.get('ts_utc', ''))
                                if ts and ts >= cutoff:
                                    es1_events.append(e)
                        except:
                            pass
        except:
            pass
    
    es1_events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    print(f"\nLoaded {len(es1_events):,} ES1 events from last 24 hours\n")
    
    if not es1_events:
        print("[WARN] No ES1 events found")
        return
    
    # Group events by type
    by_event_type = defaultdict(list)
    for e in es1_events:
        by_event_type[e.get('event', '')].append(e)
    
    # Check range building status
    print("="*80)
    print("RANGE BUILDING STATUS:")
    print("="*80)
    
    range_build_start = [e for e in es1_events if e.get('event') in ['RANGE_BUILD_START', 'RANGE_BUILDING_START']]
    range_locked = [e for e in es1_events if e.get('event') == 'RANGE_LOCKED']
    range_start_initialized = [e for e in es1_events if e.get('event') == 'RANGE_START_INITIALIZED']
    breakout_levels_computed = [e for e in es1_events if e.get('event') == 'BREAKOUT_LEVELS_COMPUTED']
    
    print(f"\n  RANGE_BUILD_START: {len(range_build_start)}")
    if range_build_start:
        latest = range_build_start[-1]
        ts = latest.get('ts_utc', '')[:19] if len(latest.get('ts_utc', '')) > 19 else latest.get('ts_utc', '')
        print(f"    Latest: {ts}")
        data = latest.get('data', {})
        if isinstance(data, dict):
            print(f"    Data keys: {list(data.keys())}")
            if 'range_start' in data:
                print(f"    Range Start: {data.get('range_start')}")
            if 'range_end' in data:
                print(f"    Range End: {data.get('range_end')}")
    
    print(f"\n  RANGE_LOCKED: {len(range_locked)}")
    if range_locked:
        latest = range_locked[-1]
        ts = latest.get('ts_utc', '')[:19] if len(latest.get('ts_utc', '')) > 19 else latest.get('ts_utc', '')
        print(f"    Latest: {ts}")
        data = latest.get('data', {})
        if isinstance(data, dict):
            print(f"    Data keys: {list(data.keys())}")
    else:
        print(f"    [WARN] No RANGE_LOCKED events found")
    
    print(f"\n  RANGE_START_INITIALIZED: {len(range_start_initialized)}")
    print(f"  BREAKOUT_LEVELS_COMPUTED: {len(breakout_levels_computed)}")
    
    # Check bar buffer status
    print("\n" + "="*80)
    print("BAR BUFFER STATUS:")
    print("="*80)
    
    bar_buffer_added = [e for e in es1_events if e.get('event') == 'BAR_BUFFER_ADD_COMMITTED']
    bar_buffer_rejected = [e for e in es1_events if e.get('event') == 'BAR_BUFFER_REJECTED']
    bar_received = [e for e in es1_events if 'BAR_RECEIVED' in e.get('event', '')]
    
    print(f"\n  BAR_BUFFER_ADD_COMMITTED: {len(bar_buffer_added)}")
    print(f"  BAR_BUFFER_REJECTED: {len(bar_buffer_rejected)}")
    print(f"  BAR_RECEIVED events: {len(bar_received)}")
    
    if bar_buffer_added:
        latest = bar_buffer_added[-1]
        ts = latest.get('ts_utc', '')[:19] if len(latest.get('ts_utc', '')) > 19 else latest.get('ts_utc', '')
        print(f"\n  Latest bar added: {ts}")
    
    # Check for range validation issues
    print("\n" + "="*80)
    print("RANGE VALIDATION & ISSUES:")
    print("="*80)
    
    range_invalid = [e for e in es1_events if 'RANGE_INVALID' in e.get('event', '')]
    range_validation = [e for e in es1_events if 'RANGE_VALIDATION' in e.get('event', '')]
    data_insufficient = [e for e in es1_events if 'DATA_INSUFFICIENT' in e.get('event', '') or 'SUSPENDED_DATA_INSUFFICIENT' in e.get('event', '')]
    
    print(f"\n  RANGE_INVALID events: {len(range_invalid)}")
    if range_invalid:
        for e in range_invalid[-5:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            print(f"    {ts}: {e.get('event')} - {e.get('msg', '')[:80]}")
    
    print(f"\n  RANGE_VALIDATION events: {len(range_validation)}")
    print(f"  DATA_INSUFFICIENT events: {len(data_insufficient)}")
    
    # Check execution status
    print("\n" + "="*80)
    print("EXECUTION STATUS:")
    print("="*80)
    
    order_submit_blocked = [e for e in es1_events if e.get('event') == 'ORDER_SUBMIT_BLOCKED']
    order_created = [e for e in es1_events if 'ORDER_CREATED' in e.get('event', '')]
    execution_gate_eval = [e for e in es1_events if e.get('event') == 'EXECUTION_GATE_EVAL']
    intent_policy_registered = [e for e in es1_events if e.get('event') == 'INTENT_POLICY_REGISTERED']
    
    print(f"\n  ORDER_SUBMIT_BLOCKED: {len(order_submit_blocked)}")
    if order_submit_blocked:
        # Group by reason
        reasons = defaultdict(int)
        for e in order_submit_blocked:
            data = e.get('data', {})
            if isinstance(data, dict):
                reason = data.get('reason', 'UNKNOWN')
                reasons[reason] += 1
        
        print(f"    Blocked by reason:")
        for reason, count in sorted(reasons.items(), key=lambda x: x[1], reverse=True):
            print(f"      {reason}: {count}")
    
    print(f"\n  ORDER_CREATED events: {len(order_created)}")
    print(f"  EXECUTION_GATE_EVAL: {len(execution_gate_eval)}")
    print(f"  INTENT_POLICY_REGISTERED: {len(intent_policy_registered)}")
    
    # Check latest execution gate evaluations
    if execution_gate_eval:
        print(f"\n  Latest EXECUTION_GATE_EVAL events:")
        for e in execution_gate_eval[-5:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            data = e.get('data', {})
            if isinstance(data, dict):
                allowed = data.get('allowed', 'N/A')
                reason = data.get('reason', 'N/A')
                print(f"    {ts}: allowed={allowed}, reason={reason}")
    
    # Check state transitions
    print("\n" + "="*80)
    print("STATE TRANSITIONS:")
    print("="*80)
    
    state_transitions = [e for e in es1_events if e.get('event') == 'STREAM_STATE_TRANSITION']
    print(f"\n  Total transitions: {len(state_transitions)}")
    
    if state_transitions:
        print(f"\n  Recent transitions:")
        for e in state_transitions[-10:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            data = e.get('data', {})
            if isinstance(data, dict):
                old_state = data.get('old_state', 'N/A')
                new_state = data.get('new_state', 'N/A')
                print(f"    {ts}: {old_state} -> {new_state}")
    
    # Check for issues preventing range lock
    print("\n" + "="*80)
    print("POTENTIAL ISSUES:")
    print("="*80)
    
    issues = []
    
    if not range_locked:
        issues.append("Range not locked - still building")
    
    if len(bar_buffer_added) < 10:
        issues.append(f"Very few bars in buffer ({len(bar_buffer_added)}) - may need more bars for range")
    
    if range_invalid:
        issues.append(f"Range validation failures detected ({len(range_invalid)})")
    
    if data_insufficient:
        issues.append(f"Data insufficient events detected ({len(data_insufficient)})")
    
    if order_submit_blocked and not order_created:
        issues.append(f"Orders blocked ({len(order_submit_blocked)}) but none created")
    
    if issues:
        print("\n  [WARN] Issues detected:")
        for issue in issues:
            print(f"    - {issue}")
    else:
        print("\n  [OK] No obvious issues detected")
    
    # Show recent key events
    print("\n" + "="*80)
    print("RECENT KEY EVENTS (Last 20):")
    print("="*80)
    
    key_events = [e for e in es1_events if any(x in e.get('event', '') for x in [
        'RANGE', 'BREAKOUT', 'BAR_BUFFER', 'ORDER', 'EXECUTION', 'STATE_TRANSITION', 
        'ARMED', 'HYDRATION', 'VALIDATION'
    ])]
    
    for e in key_events[-20:]:
        ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
        event_type = e.get('event', 'N/A')
        print(f"  {ts} | {event_type}")

if __name__ == "__main__":
    main()
