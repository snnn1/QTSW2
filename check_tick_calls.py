#!/usr/bin/env python3
"""Check if Tick() is being called after PRE_HYDRATION_COMPLETE"""
import json
import glob
from datetime import datetime, timezone

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    print("=" * 80)
    print("TICK() CALL ANALYSIS")
    print("=" * 80)
    
    all_events = []
    for log_file in log_files:
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        entry = json.loads(line)
                        all_events.append(entry)
                    except:
                        continue
        except:
            continue
    
    # Get today's events
    today_start = datetime.now(timezone.utc).replace(hour=0, minute=0, second=0, microsecond=0)
    today_events = []
    
    for entry in all_events:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= today_start:
                    today_events.append((ts, entry))
        except:
            continue
    
    today_events.sort(key=lambda x: x[0])
    
    # Find last PRE_HYDRATION_COMPLETE
    last_pre_hydration_ts = None
    for ts, entry in reversed(today_events):
        if entry.get('event') == 'PRE_HYDRATION_COMPLETE':
            last_pre_hydration_ts = ts
            break
    
    if not last_pre_hydration_ts:
        print("\n[ERROR] No PRE_HYDRATION_COMPLETE found")
        return
    
    print(f"\nLast PRE_HYDRATION_COMPLETE: {last_pre_hydration_ts.strftime('%Y-%m-%d %H:%M:%S')} UTC")
    print(f"Current time: {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S')} UTC")
    
    # Check for events after PRE_HYDRATION_COMPLETE
    events_after = [(ts, e) for ts, e in today_events if ts > last_pre_hydration_ts]
    
    print(f"\nEvents after PRE_HYDRATION_COMPLETE: {len(events_after)}")
    
    if len(events_after) == 0:
        print("\n[CRITICAL] No events after PRE_HYDRATION_COMPLETE!")
        print("           This means Tick() is NOT being called after the transition")
        print("           Possible causes:")
        print("           1. Robot stopped/crashed")
        print("           2. Timer not started (State.Realtime never reached)")
        print("           3. Timer callback throwing exceptions")
        return
    
    # Group events by minute to see if Tick() is being called regularly
    events_by_minute = {}
    for ts, entry in events_after:
        minute_key = ts.strftime('%Y-%m-%d %H:%M')
        if minute_key not in events_by_minute:
            events_by_minute[minute_key] = []
        events_by_minute[minute_key].append((ts, entry))
    
    print(f"\nEvents by minute (showing activity pattern):")
    for minute_key in sorted(events_by_minute.keys())[:10]:
        events = events_by_minute[minute_key]
        unique_events = set(e.get('event', '') for _, e in events)
        print(f"  [{minute_key}] {len(events)} events - {', '.join(sorted(unique_events)[:5])}")
    
    # Check for ENGINE_TICK_HEARTBEAT (if diagnostic logs enabled)
    tick_heartbeats = [(ts, e) for ts, e in events_after if e.get('event') == 'ENGINE_TICK_HEARTBEAT']
    if tick_heartbeats:
        print(f"\n[OK] Found {len(tick_heartbeats)} ENGINE_TICK_HEARTBEAT events")
        print("     This confirms Tick() is being called")
    else:
        print(f"\n[INFO] No ENGINE_TICK_HEARTBEAT events found")
        print("       (Diagnostic logs may be disabled)")
    
    # Check for ARMED_STATE_DIAGNOSTIC
    armed_diagnostics = [(ts, e) for ts, e in events_after if e.get('event') == 'ARMED_STATE_DIAGNOSTIC']
    if armed_diagnostics:
        print(f"\n[OK] Found {len(armed_diagnostics)} ARMED_STATE_DIAGNOSTIC events")
        print("     This confirms streams are in ARMED state and Tick() is processing them")
    else:
        print(f"\n[INFO] No ARMED_STATE_DIAGNOSTIC events found")
        print("       (Diagnostic logs may be disabled or streams not in ARMED state)")
    
    # Show latest events
    print("\n" + "=" * 80)
    print("LATEST 20 EVENTS AFTER PRE_HYDRATION_COMPLETE")
    print("=" * 80)
    for ts, entry in events_after[-20:]:
        event = entry.get('event', '')
        state = entry.get('state', '')
        data = entry.get('data', {})
        print(f"[{ts.strftime('%H:%M:%S')}] {event} | State: {state} | {data.get('instrument', '')} {data.get('session', '')}")

if __name__ == '__main__':
    main()
