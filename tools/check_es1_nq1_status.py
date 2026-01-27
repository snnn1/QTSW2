"""Check status of ES1 and NQ1 streams"""
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
    print("ES1 AND NQ1 STATUS CHECK")
    print("="*80)
    
    # Check journal files
    today = datetime.utcnow().strftime('%Y-%m-%d')
    es1_journal = journal_dir / f"{today}_ES1.json"
    nq1_journal = journal_dir / f"{today}_NQ1.json"
    
    print(f"\n[JOURNAL STATUS - {today}]")
    
    if es1_journal.exists():
        with open(es1_journal, 'r') as f:
            es1_data = json.load(f)
        print(f"\nES1:")
        print(f"  State: {es1_data.get('LastState', 'N/A')}")
        print(f"  Committed: {es1_data.get('Committed', 'N/A')}")
        print(f"  Commit Reason: {es1_data.get('CommitReason', 'N/A')}")
        last_update = parse_timestamp(es1_data.get('LastUpdateUtc', ''))
        if last_update:
            print(f"  Last Update: {last_update.strftime('%Y-%m-%d %H:%M:%S UTC')}")
    else:
        print(f"\nES1: No journal file found")
    
    if nq1_journal.exists():
        with open(nq1_journal, 'r') as f:
            nq1_data = json.load(f)
        print(f"\nNQ1:")
        print(f"  State: {nq1_data.get('LastState', 'N/A')}")
        print(f"  Committed: {nq1_data.get('Committed', 'N/A')}")
        print(f"  Commit Reason: {nq1_data.get('CommitReason', 'N/A')}")
        last_update = parse_timestamp(nq1_data.get('LastUpdateUtc', ''))
        if last_update:
            print(f"  Last Update: {last_update.strftime('%Y-%m-%d %H:%M:%S UTC')}")
    else:
        print(f"\nNQ1: No journal file found")
    
    # Check recent log events for ES1 and NQ1
    print(f"\n[RECENT LOG EVENTS]")
    
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
    
    # Filter for ES1 and NQ1 events
    es1_events = []
    nq1_events = []
    
    for e in events:
        event_str = json.dumps(e).upper()
        if 'ES1' in event_str and ('STREAM' in event_str or 'BAR' in event_str or 'RANGE' in event_str or 'EXECUTION' in event_str):
            es1_events.append(e)
        if 'NQ1' in event_str and ('STREAM' in event_str or 'BAR' in event_str or 'RANGE' in event_str or 'EXECUTION' in event_str):
            nq1_events.append(e)
    
    print(f"\nES1 Events (last 10):")
    if es1_events:
        for e in es1_events[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            ts_str = ts.strftime('%H:%M:%S UTC') if ts else 'N/A'
            event_type = e.get('event', 'N/A')
            inst = e.get('instrument', 'N/A')
            print(f"  {ts_str} | {event_type} | Inst: {inst}")
    else:
        print("  No ES1 events found")
    
    print(f"\nNQ1 Events (last 10):")
    if nq1_events:
        for e in nq1_events[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            ts_str = ts.strftime('%H:%M:%S UTC') if ts else 'N/A'
            event_type = e.get('event', 'N/A')
            inst = e.get('instrument', 'N/A')
            print(f"  {ts_str} | {event_type} | Inst: {inst}")
    else:
        print("  No NQ1 events found")
    
    # Check for critical events
    print(f"\n[CRITICAL EVENTS]")
    critical_es1 = [e for e in events if 'ES1' in json.dumps(e).upper() and 'CRITICAL' in e.get('event', '').upper()]
    critical_nq1 = [e for e in events if 'NQ1' in json.dumps(e).upper() and 'CRITICAL' in e.get('event', '').upper()]
    
    if critical_es1:
        print(f"\nES1 Critical Events: {len(critical_es1)}")
        for e in critical_es1[-3:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            print(f"  {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {e.get('event', 'N/A')}")
    else:
        print(f"\nES1: No critical events")
    
    if critical_nq1:
        print(f"\nNQ1 Critical Events: {len(critical_nq1)}")
        for e in critical_nq1[-3:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            print(f"  {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {e.get('event', 'N/A')}")
    else:
        print(f"\nNQ1: No critical events")

if __name__ == '__main__':
    main()
