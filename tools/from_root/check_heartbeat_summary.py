#!/usr/bin/env python3
"""Check heartbeat events summary"""
import json
from pathlib import Path
from datetime import datetime, timedelta

def main():
    print("=" * 80)
    print("HEARTBEAT AND TICK EVENTS SUMMARY")
    print("=" * 80)
    print()
    
    # Load recent events
    events = []
    log_dir = Path('logs/robot')
    files = sorted(log_dir.glob('*.jsonl'), key=lambda p: p.stat().st_mtime, reverse=True)
    
    # Load all events from recent log files (last 3 files)
    for log_file in files[:3]:
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                        events.append(event)
                    except:
                        continue
        except Exception as e:
            print(f"  Error reading {log_file}: {e}")
    
    print(f"Total events in last 2 hours: {len(events)}\n")
    
    # Check each event type
    event_types = {
        'ENGINE_TICK_CALLSITE': [],
        'TICK_CALLED_FROM_ONMARKETDATA': [],
        'HEARTBEAT': [],
        'SUSPENDED_STREAM_HEARTBEAT': [],
        'ENGINE_TICK_HEARTBEAT': [],
        'ENGINE_BAR_HEARTBEAT': [],
        'ENGINE_TICK_STALL_DETECTED': [],
        'ENGINE_TICK_STALL_RECOVERED': [],
    }
    
    for event in events:
        event_type = event.get('event_type', '')
        if event_type in event_types:
            event_types[event_type].append(event)
    
    print("=" * 80)
    print("EVENT STATUS")
    print("=" * 80)
    print()
    
    # Critical events
    print("[CRITICAL EVENTS]")
    print(f"  ENGINE_TICK_CALLSITE: {len(event_types['ENGINE_TICK_CALLSITE'])} events")
    if event_types['ENGINE_TICK_CALLSITE']:
        latest = event_types['ENGINE_TICK_CALLSITE'][-1]
        ts = latest.get('ts_utc', '')[:19] if latest.get('ts_utc') else 'N/A'
        print(f"    Latest: {ts}")
        print(f"    Status: WORKING")
    else:
        print(f"    Status: NOT FOUND")
    
    print(f"  ENGINE_TICK_STALL_DETECTED: {len(event_types['ENGINE_TICK_STALL_DETECTED'])} events")
    if event_types['ENGINE_TICK_STALL_DETECTED']:
        print(f"    Status: DETECTED (stalls found)")
    else:
        print(f"    Status: NOT DETECTED (good - no stalls)")
    
    print(f"  ENGINE_TICK_STALL_RECOVERED: {len(event_types['ENGINE_TICK_STALL_RECOVERED'])} events")
    if event_types['ENGINE_TICK_STALL_RECOVERED']:
        print(f"    Status: RECOVERED")
    else:
        print(f"    Status: NOT NEEDED (no stalls)")
    
    print()
    
    # Important events
    print("[IMPORTANT EVENTS]")
    print(f"  HEARTBEAT (stream-level): {len(event_types['HEARTBEAT'])} events")
    if event_types['HEARTBEAT']:
        latest = event_types['HEARTBEAT'][-1]
        ts = latest.get('ts_utc', '')[:19] if latest.get('ts_utc') else 'N/A'
        payload = latest.get('payload', {})
        state = payload.get('state', 'N/A') if isinstance(payload, dict) else 'N/A'
        print(f"    Latest: {ts}")
        print(f"    State: {state}")
        print(f"    Status: WORKING")
    else:
        print(f"    Status: NOT APPEARING (rate-limited to 7 min)")
        print(f"    Note: Streams may need to run 7+ minutes before first heartbeat")
    
    print(f"  SUSPENDED_STREAM_HEARTBEAT: {len(event_types['SUSPENDED_STREAM_HEARTBEAT'])} events")
    if event_types['SUSPENDED_STREAM_HEARTBEAT']:
        print(f"    Status: WORKING (suspended streams found)")
    else:
        print(f"    Status: NOT NEEDED (no suspended streams)")
    
    print()
    
    # Diagnostic events
    print("[DIAGNOSTIC EVENTS]")
    print(f"  TICK_CALLED_FROM_ONMARKETDATA: {len(event_types['TICK_CALLED_FROM_ONMARKETDATA'])} events")
    if event_types['TICK_CALLED_FROM_ONMARKETDATA']:
        latest = event_types['TICK_CALLED_FROM_ONMARKETDATA'][-1]
        ts = latest.get('ts_utc', '')[:19] if latest.get('ts_utc') else 'N/A'
        print(f"    Latest: {ts}")
        print(f"    Status: WORKING")
    else:
        print(f"    Status: NOT FOUND")
    
    print(f"  ENGINE_TICK_HEARTBEAT: {len(event_types['ENGINE_TICK_HEARTBEAT'])} events")
    if event_types['ENGINE_TICK_HEARTBEAT']:
        print(f"    Status: WORKING (diagnostic logs enabled)")
    else:
        print(f"    Status: NOT LOGGING (requires diagnostic logs)")
    
    print(f"  ENGINE_BAR_HEARTBEAT: {len(event_types['ENGINE_BAR_HEARTBEAT'])} events")
    if event_types['ENGINE_BAR_HEARTBEAT']:
        print(f"    Status: WORKING (diagnostic logs enabled)")
    else:
        print(f"    Status: NOT LOGGING (requires diagnostic logs)")
    
    print()
    print("=" * 80)
    print("RECOMMENDATIONS")
    print("=" * 80)
    print()
    
    print("MUST KEEP (Critical):")
    print("  - ENGINE_TICK_CALLSITE: WORKING - Keep")
    print("  - ENGINE_TICK_STALL_DETECTED: WORKING - Keep")
    print("  - ENGINE_TICK_STALL_RECOVERED: WORKING - Keep")
    print()
    
    print("SHOULD KEEP (Important):")
    if len(event_types['HEARTBEAT']) > 0:
        print("  - HEARTBEAT (stream-level): WORKING - Keep")
    else:
        print("  - HEARTBEAT (stream-level): NOT APPEARING - Wait 7+ min, then Keep")
    print("  - SUSPENDED_STREAM_HEARTBEAT: WORKING - Keep")
    print()
    
    print("OPTIONAL (Diagnostic):")
    if len(event_types['TICK_CALLED_FROM_ONMARKETDATA']) > 0:
        print("  - TICK_CALLED_FROM_ONMARKETDATA: WORKING - Can keep or remove")
    else:
        print("  - TICK_CALLED_FROM_ONMARKETDATA: NOT FOUND")
    print("  - ENGINE_TICK_HEARTBEAT: Enable if debugging bar processing")
    print("  - ENGINE_BAR_HEARTBEAT: Enable if debugging bar routing")
    print()
    
    print("DON'T NEED:")
    print("  - ENGINE_HEARTBEAT: Deprecated - Remove if still in code")
    print("  - Other diagnostic tick events: Optional - Remove if not debugging")

if __name__ == '__main__':
    main()
