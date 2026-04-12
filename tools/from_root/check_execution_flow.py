#!/usr/bin/env python3
"""Check execution flow - why orders aren't being executed"""

import json
import glob

def check_stream_execution(stream_name):
    """Check execution flow for a specific stream"""
    log_file = f'logs/robot/robot_{stream_name}.jsonl'
    
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            events = [json.loads(l.strip()) for l in f.readlines()]
    except:
        return None
    
    # Find key events
    gate_evals = [e for e in events if e.get('event') == 'EXECUTION_GATE_EVAL']
    breakout_detected = [e for e in events if 'BREAKOUT' in e.get('event', '')]
    entry_detected = [e for e in events if 'ENTRY_DETECTED' in e.get('event', '')]
    order_submitted = [e for e in events if 'ORDER_SUBMITTED' in e.get('event', '')]
    intent_registered = [e for e in events if 'INTENT' in e.get('event', '')]
    state_transitions = [e for e in events if 'STATE_TRANSITION' in e.get('event', '') or 'STREAM_STATE_TRANSITION' in e.get('event', '')]
    pre_hydration_complete = [e for e in events if 'PRE_HYDRATION_COMPLETE' in e.get('event', '')]
    range_locked = [e for e in events if 'RANGE_LOCKED' in e.get('event', '')]
    
    print(f"\n{'='*80}")
    print(f"EXECUTION FLOW ANALYSIS: {stream_name}")
    print(f"{'='*80}")
    
    print(f"\nState Transitions: {len(state_transitions)}")
    if state_transitions:
        print("  Recent transitions:")
        for e in state_transitions[-5:]:
            data = e.get('data', {})
            print(f"    {e.get('ts_utc', 'N/A')} | {data.get('from_state', 'N/A')} -> {data.get('to_state', 'N/A')}")
    
    print(f"\nPre-Hydration Complete: {len(pre_hydration_complete)}")
    if pre_hydration_complete:
        print(f"  Last: {pre_hydration_complete[-1].get('ts_utc', 'N/A')}")
    
    print(f"\nRange Locked Events: {len(range_locked)}")
    if range_locked:
        print(f"  Last: {range_locked[-1].get('ts_utc', 'N/A')}")
    
    print(f"\nBreakout Detected: {len(breakout_detected)}")
    if breakout_detected:
        print("  Recent breakouts:")
        for e in breakout_detected[-5:]:
            data = e.get('data', {})
            print(f"    {e.get('ts_utc', 'N/A')} | Direction: {data.get('direction', 'N/A')} | Price: {data.get('entry_price', 'N/A')}")
    
    print(f"\nEntry Detected: {len(entry_detected)}")
    if entry_detected:
        print("  Recent entries:")
        for e in entry_detected[-5:]:
            data = e.get('data', {})
            print(f"    {e.get('ts_utc', 'N/A')} | Direction: {data.get('direction', 'N/A')} | Price: {data.get('entry_price', 'N/A')}")
    
    print(f"\nIntent Registered: {len(intent_registered)}")
    if intent_registered:
        print("  Recent intents:")
        for e in intent_registered[-5:]:
            data = e.get('data', {})
            print(f"    {e.get('ts_utc', 'N/A')} | Intent ID: {data.get('intent_id', 'N/A')[:20]}...")
    
    print(f"\nOrders Submitted: {len(order_submitted)}")
    if order_submitted:
        print("  Recent orders:")
        for e in order_submitted[-5:]:
            data = e.get('data', {})
            print(f"    {e.get('ts_utc', 'N/A')} | Instrument: {data.get('instrument', 'N/A')} | Direction: {data.get('direction', 'N/A')}")
    
    # Analyze latest gate eval
    if gate_evals:
        latest_gate = gate_evals[-1]
        data = latest_gate.get('data', {})
        print(f"\nLatest Gate Evaluation:")
        print(f"  Time: {latest_gate.get('ts_utc', 'N/A')}")
        print(f"  Final Allowed: {data.get('final_allowed')}")
        print(f"  State: {data.get('state', 'N/A')}")
        print(f"  Stream Armed: {data.get('stream_armed')}")
        print(f"  Slot Reached: {data.get('slot_reached')}")
        print(f"  Can Detect Entries: {data.get('can_detect_entries')}")
        print(f"  Entry Detected: {data.get('entry_detected')}")
        print(f"  Breakout Levels Computed: {data.get('breakout_levels_computed')}")
        
        # Show which gates are failing
        if not data.get('final_allowed'):
            print(f"\n  Blocking Gates:")
            if not data.get('realtime_ok'): print(f"    - REALTIME_OK")
            if not data.get('trading_day'): print(f"    - TRADING_DAY")
            if not data.get('session_active'): print(f"    - SESSION_ACTIVE")
            if not data.get('slot_reached'): print(f"    - SLOT_REACHED")
            if not data.get('timetable_enabled'): print(f"    - TIMETABLE_ENABLED")
            if not data.get('stream_armed'): print(f"    - STREAM_ARMED")
            if not data.get('state_ok'): print(f"    - STATE_OK")
            if not data.get('can_detect_entries'): print(f"    - CAN_DETECT_ENTRIES")

# Check active streams
streams = ['NG', 'ES', 'NQ', 'GC', 'RTY', 'YM']

for stream in streams:
    check_stream_execution(stream)
