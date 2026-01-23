#!/usr/bin/env python3
"""Check for mid-session restart events in logs."""

import json
from pathlib import Path
from datetime import datetime, timezone
from collections import Counter

def parse_timestamp(ts_str):
    """Parse ISO8601 timestamp."""
    try:
        ts_str = ts_str.replace('Z', '+00:00')
        if '.' in ts_str:
            ts_str = ts_str.split('.')[0] + '+00:00'
        return datetime.fromisoformat(ts_str)
    except:
        return None

def main():
    log_dir = Path("logs/robot")
    if not log_dir.exists():
        print("[ERROR] Log directory not found")
        return
    
    print("=" * 80)
    print("MID-SESSION RESTART ASSESSMENT")
    print("=" * 80)
    print()
    
    # Collect all MID_SESSION_RESTART_DETECTED events
    restart_events = []
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        if event.get('event') == 'MID_SESSION_RESTART_DETECTED':
                            restart_events.append(event)
                    except:
                        continue
        except:
            continue
    
    print(f"MID_SESSION_RESTART_DETECTED events found: {len(restart_events)}")
    print()
    
    if restart_events:
        print("Recent mid-session restarts:")
        for e in restart_events[-10:]:
            ts = e.get('ts_utc', '')[:19]
            inst = e.get('instrument', '')
            data = e.get('data', {})
            prev_state = data.get('previous_state', 'N/A')
            policy = data.get('policy', 'N/A')
            print(f"  {ts} | {inst:4} | previous_state={prev_state:20} | policy={policy}")
        print()
        
        # Analyze restart patterns
        prev_states = Counter([e.get('data', {}).get('previous_state', 'N/A') for e in restart_events])
        print("Restart by previous state:")
        for state, count in sorted(prev_states.items()):
            print(f"  {state}: {count}")
        print()
    else:
        print("No mid-session restarts detected in logs.")
        print("(This is normal - restarts are only detected when journal exists and stream was active)")
        print()
    
    # Check for recovery events
    recovery_events = []
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        event_type = event.get('event', '')
                        if 'RECOVERY' in event_type or 'RECONCILED' in event_type:
                            recovery_events.append(event)
                    except:
                        continue
        except:
            continue
    
    if recovery_events:
        print(f"Recovery-related events found: {len(recovery_events)}")
        recovery_types = Counter([e.get('event', 'N/A') for e in recovery_events])
        print("\nRecovery event types:")
        for event_type, count in sorted(recovery_types.items()):
            print(f"  {event_type}: {count}")
        print()
        
        # Check for unmatched positions
        unmatched = [e for e in recovery_events if e.get('event') == 'RECOVERY_POSITION_UNMATCHED']
        if unmatched:
            print(f"[WARN] Found {len(unmatched)} RECOVERY_POSITION_UNMATCHED events:")
            for e in unmatched:
                ts = e.get('ts_utc', '')[:19]
                data = e.get('data', {})
                count = data.get('unmatched_count', 0)
                print(f"  {ts} | {count} unmatched positions")
            print()
    else:
        print("No recovery events found (normal if no positions existed during restarts)")
        print()
    
    # Check journal files
    journal_dir = log_dir / "journal"
    if journal_dir.exists():
        journals = list(journal_dir.glob("*.json"))
        print(f"Journal files found: {len(journals)}")
        
        if journals:
            # Sample a few journals to see state
            print("\nSample journal states:")
            for journal_file in sorted(journals)[:5]:
                try:
                    with open(journal_file, 'r', encoding='utf-8') as f:
                        journal = json.load(f)
                        trading_date = journal.get('TradingDate', 'N/A')
                        stream = journal.get('Stream', 'N/A')
                        committed = journal.get('Committed', False)
                        last_state = journal.get('LastState', 'N/A')
                        print(f"  {journal_file.name}: committed={committed}, state={last_state}")
                except:
                    print(f"  {journal_file.name}: [ERROR reading]")
            print()
    else:
        print("Journal directory not found")
        print()
    
    # Summary
    print("=" * 80)
    print("SUMMARY")
    print("=" * 80)
    print(f"Mid-session restarts detected: {len(restart_events)}")
    print(f"Recovery events: {len(recovery_events)}")
    print(f"Journal files: {len(journals) if journal_dir.exists() else 0}")
    
    if len(restart_events) == 0:
        print("\n[INFO] No mid-session restarts found in logs.")
        print("This means either:")
        print("  1. No restarts occurred during active trading sessions")
        print("  2. Restarts occurred before range_start (not mid-session)")
        print("  3. Streams were already committed when restart occurred")

if __name__ == "__main__":
    main()
