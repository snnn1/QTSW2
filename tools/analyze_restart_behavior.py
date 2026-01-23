#!/usr/bin/env python3
"""Analyze restart behavior from logs and journals."""

import json
import re
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

def parse_payload(payload_str):
    """Parse payload string format: '{ key = value, key2 = value2 }'"""
    result = {}
    if not payload_str or not isinstance(payload_str, str):
        return result
    
    # Extract key-value pairs
    pattern = r'(\w+)\s*=\s*([^,}]+)'
    matches = re.findall(pattern, payload_str)
    for key, value in matches:
        result[key.strip()] = value.strip()
    return result

def main():
    log_dir = Path("logs/robot")
    journal_dir = log_dir / "journal"
    
    print("=" * 80)
    print("MID-SESSION RESTART BEHAVIOR ANALYSIS")
    print("=" * 80)
    print()
    
    # Find recent MID_SESSION_RESTART_DETECTED events
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
    
    print(f"Total MID_SESSION_RESTART_DETECTED events: {len(restart_events)}")
    
    if restart_events:
        # Get recent restarts (last 10)
        recent = restart_events[-10:]
        print(f"\nRecent mid-session restarts (last 10):")
        
        for e in recent:
            ts = e.get('ts_utc', '')[:19]
            inst = e.get('instrument', '')
            payload_str = str(e.get('data', {}).get('payload', ''))
            payload = parse_payload(payload_str)
            
            prev_state = payload.get('previous_state', 'N/A')
            restart_time = payload.get('restart_time_chicago', 'N/A')
            range_start = payload.get('range_start_chicago', 'N/A')
            slot_time = payload.get('slot_time_chicago', 'N/A')
            policy = payload.get('policy', 'N/A')
            
            print(f"\n  {ts} | {inst}")
            print(f"    Previous State: {prev_state}")
            print(f"    Restart Time: {restart_time[:19] if len(restart_time) > 19 else restart_time}")
            print(f"    Range Start: {range_start[:19] if len(range_start) > 19 else range_start}")
            print(f"    Slot Time: {slot_time[:19] if len(slot_time) > 19 else slot_time}")
            print(f"    Policy: {policy}")
        
        # Analyze by previous state
        prev_states = []
        for e in restart_events:
            payload_str = str(e.get('data', {}).get('payload', ''))
            payload = parse_payload(payload_str)
            prev_states.append(payload.get('previous_state', 'N/A'))
        
        state_counter = Counter(prev_states)
        print(f"\n\nRestart by previous state:")
        for state, count in sorted(state_counter.items()):
            print(f"  {state}: {count}")
    
    print()
    
    # Check journals for today
    today = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    today_journals = list(journal_dir.glob(f"{today}_*.json")) if journal_dir.exists() else []
    
    print(f"Today's journals ({today}): {len(today_journals)}")
    
    if today_journals:
        committed_count = 0
        active_count = 0
        states = []
        
        for journal_file in today_journals[:10]:  # Sample first 10
            try:
                with open(journal_file, 'r', encoding='utf-8') as f:
                    journal = json.load(f)
                    if journal.get('Committed', False):
                        committed_count += 1
                    else:
                        active_count += 1
                    states.append(journal.get('LastState', 'N/A'))
            except:
                pass
        
        print(f"  Committed: {committed_count}")
        print(f"  Active (not committed): {active_count}")
        
        state_counter = Counter(states)
        print(f"\n  States:")
        for state, count in sorted(state_counter.items()):
            print(f"    {state}: {count}")
    
    print()
    
    # Check for RANGE_INITIALIZED_FROM_HISTORY (indicates restart recovery)
    range_init_events = []
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        if event.get('event') == 'RANGE_INITIALIZED_FROM_HISTORY':
                            range_init_events.append(event)
                    except:
                        continue
        except:
            continue
    
    print(f"RANGE_INITIALIZED_FROM_HISTORY events: {len(range_init_events)}")
    if range_init_events:
        print("  (These indicate ranges were reconstructed from historical bars after restart)")
        recent_init = range_init_events[-5:]
        print("\n  Recent range initializations:")
        for e in recent_init:
            ts = e.get('ts_utc', '')[:19]
            inst = e.get('instrument', '')
            data = e.get('data', {})
            bar_count = data.get('bar_count', 'N/A')
            print(f"    {ts} | {inst} | bars={bar_count}")
    
    print()
    print("=" * 80)
    print("KEY FINDINGS")
    print("=" * 80)
    print("""
1. RESTART DETECTION:
   - Robot detects mid-session restarts automatically
   - Logs MID_SESSION_RESTART_DETECTED event with full context
   - Uses journal state to determine if restart is mid-session

2. RANGE RECONSTRUCTION:
   - Ranges are recomputed from historical + live bars
   - Uses BarsRequest to load historical bars from range_start
   - May differ from uninterrupted operation if restart after slot_time

3. STATE RECOVERY:
   - Journal preserves stream state (PRE_HYDRATION, RANGE_BUILDING, etc.)
   - Committed streams stay committed (cannot re-arm)
   - Active streams resume from previous state

4. POSITION PROTECTION:
   - If position exists, robot reconciles and restores protective orders
   - Unmatched positions cause stream stand-down (fail-closed)
   - Prevents duplicate entries via journal tracking
""")

if __name__ == "__main__":
    main()
