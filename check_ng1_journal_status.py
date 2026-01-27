#!/usr/bin/env python3
"""Check NG1 journal status and why it might not be rechecking"""
import json
from pathlib import Path
from datetime import datetime

# Check journal file
journal_path = Path("logs/robot/journal")
if journal_path.exists():
    # Find NG1 journal for today
    today = datetime.utcnow().strftime("%Y-%m-%d")
    ng1_journal_file = journal_path / f"{today}_NG1.json"
    
    if ng1_journal_file.exists():
        print("="*80)
        print("NG1 JOURNAL STATUS")
        print("="*80)
        try:
            journal_data = json.loads(ng1_journal_file.read_text())
            print(f"\nJournal file: {ng1_journal_file}")
            print(f"TradingDate: {journal_data.get('TradingDate', 'N/A')}")
            print(f"Stream: {journal_data.get('Stream', 'N/A')}")
            print(f"Committed: {journal_data.get('Committed', 'N/A')}")
            print(f"CommitReason: {journal_data.get('CommitReason', 'N/A')}")
            print(f"LastState: {journal_data.get('LastState', 'N/A')}")
            print(f"LastUpdateUtc: {journal_data.get('LastUpdateUtc', 'N/A')}")
            print(f"TimetableHashAtCommit: {journal_data.get('TimetableHashAtCommit', 'N/A')}")
            
            if journal_data.get('Committed'):
                print(f"\nWARNING: NG1 IS COMMITTED - This prevents re-arming/reconstruction")
                print(f"   Commit reason: {journal_data.get('CommitReason', 'N/A')}")
                print(f"   Last state: {journal_data.get('LastState', 'N/A')}")
            else:
                print(f"\nOK: NG1 is NOT committed - Should allow reconstruction")
        except Exception as e:
            print(f"Error reading journal: {e}")
    else:
        print(f"NG1 journal file not found: {ng1_journal_file}")
        print("This means NG1 hasn't been initialized today, or journal was deleted")
else:
    print(f"Journal directory not found: {journal_path}")

# Check recent events for NG1
print(f"\n{'='*80}")
print("NG1 RECENT EVENTS")
print("="*80)

log_dir = Path("logs/robot")
events = []
for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except:
        pass

today_events = [e for e in events if e.get('ts_utc', '').startswith('2026-01-26')]
ng1_events = [e for e in today_events if e.get('stream') == 'NG1']
ng1_events.sort(key=lambda x: x.get('ts_utc', ''))

# Check for key events
print(f"\nTotal NG1 events today: {len(ng1_events)}")

# Check for STREAM_SKIPPED
skipped = [e for e in ng1_events if 'STREAM_SKIPPED' in e.get('event', '')]
if skipped:
    print(f"\nWARNING: STREAM_SKIPPED events: {len(skipped)}")
    for s in skipped[-3:]:
        print(f"  {s.get('ts_utc', '')[:19]} - {s.get('event', 'N/A')}")
        data = s.get('data', {})
        if isinstance(data, dict):
            print(f"    Reason: {data.get('reason', 'N/A')}")

# Check for MID_SESSION_RESTART_DETECTED
restarts = [e for e in ng1_events if 'MID_SESSION_RESTART_DETECTED' in e.get('event', '')]
if restarts:
    print(f"\nOK: MID_SESSION_RESTART_DETECTED events: {len(restarts)}")
    for r in restarts[-3:]:
        print(f"  {r.get('ts_utc', '')[:19]} - {r.get('event', 'N/A')}")

# Check for STREAM_ARMED
armed = [e for e in ng1_events if 'STREAM_ARMED' in e.get('event', '')]
if armed:
    print(f"\nOK: STREAM_ARMED events: {len(armed)}")
    for a in armed[-3:]:
        print(f"  {a.get('ts_utc', '')[:19]} - {a.get('event', 'N/A')}")

# Check latest state
state_events = [e for e in ng1_events if e.get('state')]
if state_events:
    latest_state = max(state_events, key=lambda x: x.get('ts_utc', ''))
    print(f"\nLatest state: {latest_state.get('state', 'N/A')} (at {latest_state.get('ts_utc', '')[:19]})")

# Check for commits
commits = [e for e in ng1_events if 'COMMIT' in e.get('event', '') or 'JOURNAL_WRITTEN' in e.get('event', '')]
if commits:
    print(f"\nWARNING: Commit events: {len(commits)}")
    for c in commits[-3:]:
        print(f"  {c.get('ts_utc', '')[:19]} - {c.get('event', 'N/A')}")
        data = c.get('data', {})
        if isinstance(data, dict):
            print(f"    Committed: {data.get('committed', 'N/A')}")
            print(f"    Reason: {data.get('commit_reason', 'N/A')}")

print(f"\n{'='*80}")
