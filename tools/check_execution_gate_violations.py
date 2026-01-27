"""Check execution gate violations and see what's blocking ES1/NQ1"""
import json
from pathlib import Path
from datetime import datetime

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    qtsw2_root = Path(__file__).parent.parent
    log_file = qtsw2_root / "logs" / "robot" / "robot_ENGINE.jsonl"
    
    print("="*80)
    print("EXECUTION GATE VIOLATION ANALYSIS")
    print("="*80)
    
    if not log_file.exists():
        print("  No log file found")
        return
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass
    
    # Find EXECUTION_GATE_INVARIANT_VIOLATION events (both direct and via CRITICAL_EVENT_REPORTED)
    violations = []
    for e in events:
        if e.get('event') == 'EXECUTION_GATE_INVARIANT_VIOLATION':
            violations.append(e)
        elif e.get('event') == 'CRITICAL_EVENT_REPORTED':
            data = e.get('data', {})
            if data.get('event_type') == 'EXECUTION_GATE_INVARIANT_VIOLATION':
                # Extract the payload from the critical event
                # The payload might be nested in the data field
                violation_data = {**e.get('data', {})}
                violation_data['ts_utc'] = e.get('ts_utc', '')
                violations.append({'event': 'EXECUTION_GATE_INVARIANT_VIOLATION', 'data': violation_data, 'ts_utc': e.get('ts_utc', '')})
    
    if not violations:
        print("\nNo EXECUTION_GATE_INVARIANT_VIOLATION events found")
        return
    
    print(f"\nFound {len(violations)} execution gate violations\n")
    
    # Analyze the most recent violations
    for i, v in enumerate(violations[-5:], 1):
        ts = parse_timestamp(v.get('ts_utc', ''))
        data = v.get('data', {})
        
        print(f"[Violation #{i}]")
        print(f"  Time: {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'}")
        print(f"  Stream: {data.get('stream', 'N/A')}")
        print(f"  Instrument: {data.get('instrument', 'N/A')}")
        print(f"  Trading Date: {data.get('trading_date', 'N/A')}")
        print(f"  Execution Mode: {data.get('execution_mode', 'N/A')}")
        print(f"  State: {data.get('state', 'N/A')}")
        print(f"\n  Gate States:")
        print(f"    Stream Armed: {data.get('stream_armed', 'N/A')}")
        print(f"    Can Detect Entries: {data.get('can_detect_entries', 'N/A')}")
        print(f"    Entry Detected: {data.get('entry_detected', 'N/A')}")
        print(f"    Breakout Levels Computed: {data.get('breakout_levels_computed', 'N/A')}")
        print(f"    Timetable Validated: {data.get('timetable_validated', 'N/A')}")
        print(f"    Kill Switch Active: {data.get('kill_switch_active', 'N/A')}")
        print(f"    Recovery State: {data.get('recovery_state', 'N/A')}")
        print(f"    Slot Time Allowed: {data.get('slot_time_allowed', 'N/A')}")
        print(f"    Trading Date Set: {data.get('trading_date_set', 'N/A')}")
        print()
    
    # Check for MES/MNQ vs ES/NQ mismatch
    print("[CHECKING FOR MES/MNQ vs ES/NQ MISMATCH]")
    
    # Look for execution-related events with instrument info
    execution_events = [e for e in events if 'EXECUTION' in e.get('event', '').upper() or 
                       'INTENT' in e.get('event', '').upper() or
                       'ENTRY' in e.get('event', '').upper()]
    
    mes_mnq_events = []
    for e in execution_events:
        event_str = json.dumps(e).upper()
        if 'MES' in event_str or 'MNQ' in event_str:
            mes_mnq_events.append(e)
    
    if mes_mnq_events:
        print(f"\nFound {len(mes_mnq_events)} events mentioning MES/MNQ:")
        for e in mes_mnq_events[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            print(f"  {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {e.get('event', 'N/A')}")
            data = e.get('data', {})
            if 'instrument' in data or 'execution_instrument' in data or 'canonical_instrument' in data:
                print(f"    Instrument info: {json.dumps({k: v for k, v in data.items() if 'instrument' in k.lower()}, indent=4)}")
    else:
        print("\n  No MES/MNQ events found in execution logs")
    
    # Check recent stream state transitions for ES1/NQ1
    print(f"\n[ES1/NQ1 STREAM STATE TRANSITIONS]")
    es1_states = [e for e in events if 'ES1' in json.dumps(e).upper() and 'STATE' in e.get('event', '').upper()]
    nq1_states = [e for e in events if 'NQ1' in json.dumps(e).upper() and 'STATE' in e.get('event', '').upper()]
    
    if es1_states:
        print(f"\nES1 State Events (last 5):")
        for e in es1_states[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            print(f"  {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {e.get('event', 'N/A')}")
    
    if nq1_states:
        print(f"\nNQ1 State Events (last 5):")
        for e in nq1_states[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            print(f"  {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {e.get('event', 'N/A')}")

if __name__ == '__main__':
    main()
