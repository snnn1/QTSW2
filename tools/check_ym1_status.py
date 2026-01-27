"""Check YM1 stream status and range locking"""
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
    journal_dir = qtsw2_root / "logs" / "robot" / "journal"
    
    print("="*80)
    print("YM1 STATUS CHECK")
    print("="*80)
    
    # Check journal
    today = datetime.utcnow().strftime('%Y-%m-%d')
    ym1_journal = journal_dir / f"{today}_YM1.json"
    
    print(f"\n[JOURNAL STATUS - {today}]")
    if ym1_journal.exists():
        with open(ym1_journal, 'r') as f:
            ym1_data = json.load(f)
        print(f"\nYM1:")
        print(f"  State: {ym1_data.get('LastState', 'N/A')}")
        print(f"  Committed: {ym1_data.get('Committed', 'N/A')}")
        print(f"  Commit Reason: {ym1_data.get('CommitReason', 'N/A')}")
        last_update = parse_timestamp(ym1_data.get('LastUpdateUtc', ''))
        if last_update:
            age_seconds = (datetime.utcnow() - last_update.replace(tzinfo=None)).total_seconds()
            print(f"  Last Update: {last_update.strftime('%Y-%m-%d %H:%M:%S UTC')} ({age_seconds:.0f} seconds ago)")
    else:
        print(f"\nYM1: No journal file found")
    
    # Check recent YM1 events
    print(f"\n[RECENT YM1 EVENTS]")
    
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
    
    # Filter for YM1 events
    ym1_events = []
    for e in events:
        event_str = json.dumps(e).upper()
        if 'YM1' in event_str:
            ym1_events.append(e)
    
    print(f"\n  Found {len(ym1_events)} YM1 events")
    
    # Show key events
    key_events = ['RANGE_LOCKED', 'RANGE_COMPUTED', 'RANGE_COMPUTE_FAILED', 'STATE_TRANSITION', 
                   'BAR_ACCEPTED', 'PRE_HYDRATION_COMPLETE', 'ARMED', 'EXECUTION_GATE']
    
    print(f"\n  Key Events (last 20):")
    shown = 0
    for e in reversed(ym1_events):
        event_type = e.get('event', '')
        if any(key in event_type.upper() for key in key_events) or shown < 5:
            ts = parse_timestamp(e.get('ts_utc', ''))
            ts_str = ts.strftime('%H:%M:%S UTC') if ts else 'N/A'
            inst = e.get('instrument', 'N/A')
            stream = e.get('stream', 'N/A')
            print(f"    {ts_str} | {event_type} | Stream: {stream} | Inst: {inst}")
            shown += 1
            if shown >= 20:
                break
    
    # Check for range computation issues
    print(f"\n[RANGE COMPUTATION STATUS]")
    range_computed = [e for e in ym1_events if 'RANGE_COMPUTED' in e.get('event', '').upper()]
    range_failed = [e for e in ym1_events if 'RANGE_COMPUTE_FAILED' in e.get('event', '').upper()]
    range_locked = [e for e in ym1_events if 'RANGE_LOCKED' in e.get('event', '').upper()]
    
    print(f"  Range Computed: {len(range_computed)}")
    print(f"  Range Compute Failed: {len(range_failed)}")
    print(f"  Range Locked: {len(range_locked)}")
    
    if range_failed:
        print(f"\n  Recent Range Compute Failures:")
        for e in range_failed[-3:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {data.get('reason', 'N/A')}")
    
    if range_locked:
        latest_lock = range_locked[-1]
        ts = parse_timestamp(latest_lock.get('ts_utc', ''))
        data = latest_lock.get('data', {})
        print(f"\n  Latest Range Lock:")
        print(f"    Time: {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}")
        print(f"    Range High: {data.get('range_high', 'N/A')}")
        print(f"    Range Low: {data.get('range_low', 'N/A')}")
        print(f"    Range Start: {data.get('range_start_chicago', 'N/A')}")
        print(f"    Range End: {data.get('range_end_chicago', 'N/A')}")
    
    # Check current state
    print(f"\n[CURRENT STATE]")
    state_events = [e for e in ym1_events if 'STATE' in e.get('event', '').upper()]
    if state_events:
        latest_state = state_events[-1]
        ts = parse_timestamp(latest_state.get('ts_utc', ''))
        data = latest_state.get('data', {})
        print(f"  Latest State Event: {latest_state.get('event', 'N/A')}")
        print(f"  Time: {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}")
        print(f"  State: {data.get('state', latest_state.get('state', 'N/A'))}")
        print(f"  Old State: {data.get('old_state', 'N/A')}")
        print(f"  New State: {data.get('new_state', 'N/A')}")

if __name__ == '__main__':
    main()
