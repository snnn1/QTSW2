"""Check YM1 execution gates and why it's not executing"""
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
    print("YM1 EXECUTION GATE CHECK")
    print("="*80)
    
    # Check journal
    today = datetime.utcnow().strftime('%Y-%m-%d')
    ym1_journal = journal_dir / f"{today}_YM1.json"
    
    if ym1_journal.exists():
        with open(ym1_journal, 'r') as f:
            ym1_data = json.load(f)
        print(f"\n[YM1 JOURNAL STATUS]")
        print(f"  State: {ym1_data.get('LastState', 'N/A')}")
        print(f"  Committed: {ym1_data.get('Committed', 'N/A')}")
        last_update = parse_timestamp(ym1_data.get('LastUpdateUtc', ''))
        if last_update:
            age_seconds = (datetime.utcnow() - last_update.replace(tzinfo=None)).total_seconds()
            print(f"  Last Update: {last_update.strftime('%Y-%m-%d %H:%M:%S UTC')} ({age_seconds:.0f} seconds ago)")
    
    # Check timetable for YM1 slot time
    timetable_file = qtsw2_root / "data" / "timetable" / "timetable_current.json"
    if timetable_file.exists():
        with open(timetable_file, 'r') as f:
            timetable = json.load(f)
        ym1_stream = next((s for s in timetable.get('streams', []) if s.get('stream') == 'YM1'), None)
        if ym1_stream:
            print(f"\n[YM1 TIMETABLE]")
            print(f"  Slot Time: {ym1_stream.get('slot_time', 'N/A')} Chicago")
            print(f"  Instrument: {ym1_stream.get('instrument', 'N/A')}")
            print(f"  Session: {ym1_stream.get('session', 'N/A')}")
            print(f"  Enabled: {ym1_stream.get('enabled', 'N/A')}")
    
    # Check for execution gate eval events
    if not log_file.exists():
        print("\n  No log file found")
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
    
    # Find EXECUTION_GATE_EVAL events for YM1
    print(f"\n[EXECUTION GATE EVALUATIONS]")
    gate_evals = []
    for e in events:
        event_str = json.dumps(e).upper()
        if 'EXECUTION_GATE_EVAL' in e.get('event', '').upper() and 'YM1' in event_str:
            gate_evals.append(e)
    
    if gate_evals:
        print(f"\n  Found {len(gate_evals)} gate evaluations for YM1:")
        for e in gate_evals[-3:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            print(f"\n    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}:")
            print(f"      Stream: {e.get('stream', 'N/A')}")
            print(f"      State: {data.get('state', 'N/A')}")
            print(f"      Stream Armed: {data.get('stream_armed', 'N/A')}")
            print(f"      State OK: {data.get('state_ok', 'N/A')}")
            print(f"      Slot Reached: {data.get('slot_reached', 'N/A')}")
            print(f"      Can Detect Entries: {data.get('can_detect_entries', 'N/A')}")
            print(f"      Breakout Levels Computed: {data.get('breakout_levels_computed', 'N/A')}")
            print(f"      Final Allowed: {data.get('final_allowed', 'N/A')}")
    else:
        print("  No EXECUTION_GATE_EVAL events found for YM1")
    
    # Check for any YM1 events around 15:00 UTC
    print(f"\n[EVENTS AROUND 15:00 UTC (Slot Time)]")
    slot_time_events = []
    for e in events:
        ts_str = e.get('ts_utc', '')
        if ts_str.startswith('2026-01-26T15:00') or ts_str.startswith('2026-01-26T14:59'):
            event_str = json.dumps(e).upper()
            if 'YM' in event_str or 'MYM' in event_str:
                slot_time_events.append(e)
    
    if slot_time_events:
        print(f"\n  Found {len(slot_time_events)} YM-related events around slot time:")
        for e in slot_time_events[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {e.get('event', 'N/A')} | Stream: {e.get('stream', 'N/A')}")
    else:
        print("  No YM events found around slot time")
    
    # Check current time vs slot time
    print(f"\n[TIME CHECK]")
    now_utc = datetime.utcnow()
    now_chicago_str = now_utc.strftime('%H:%M')
    print(f"  Current UTC: {now_utc.strftime('%H:%M:%S UTC')}")
    print(f"  YM1 Slot Time: 09:00 Chicago (15:00 UTC)")
    if now_utc.hour >= 15:
        print(f"  [OK] Slot time has passed")
    else:
        print(f"  [WAITING] Slot time not yet reached")

if __name__ == '__main__':
    main()
