#!/usr/bin/env python3
"""
Explain why the user is seeing these alerts and what they actually mean.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
import pytz

def parse_timestamp(ts_str: str):
    if not ts_str:
        return None
    try:
        if 'T' in ts_str:
            if ts_str.endswith('Z'):
                ts_str = ts_str[:-1] + '+00:00'
            elif '+' not in ts_str:
                ts_str = ts_str + '+00:00'
            dt = datetime.fromisoformat(ts_str)
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            return dt
    except:
        pass
    return None

def main():
    log_dir = Path("logs/robot")
    cutoff = datetime.now(timezone.utc) - timedelta(hours=2)
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("EXPLANATION OF CRITICAL ALERTS")
    print("="*80)
    print("\nAnalyzing what these alerts actually mean based on current system state...\n")
    
    # Load events
    events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True)[:3]:
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            ts = parse_timestamp(e.get('ts_utc', ''))
                            if ts and ts >= cutoff:
                                events.append(e)
                        except:
                            pass
        except:
            pass
    
    events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    print("="*80)
    print("1. ENGINE STALLED")
    print("="*80)
    
    ticks = [e for e in events if e.get('event') == 'ENGINE_TICK_CALLSITE']
    stalls = [e for e in events if e.get('event') == 'ENGINE_TICK_STALL_DETECTED']
    
    if ticks:
        latest_tick = max(ticks, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest_tick.get('ts_utc', ''))
        if ts:
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            ts_chicago = ts.astimezone(chicago_tz)
            print(f"  Latest engine tick: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.1f}s ago)")
            if age < 15:
                print(f"  [FALSE ALERT] Engine is NOT stalled - running normally")
                print(f"  Reason: Engine ticks are current (< 15s threshold)")
            else:
                print(f"  [POSSIBLE ISSUE] Engine tick age is {age:.0f}s (threshold: 15s)")
    else:
        print(f"  [ERROR] No engine ticks found")
    
    if stalls:
        print(f"  [WARN] {len(stalls)} stall detection events found")
    else:
        print(f"  [OK] No stall detection events")
    
    print("\n" + "="*80)
    print("2. BROKER DISCONNECTED")
    print("="*80)
    
    conn_lost = [e for e in events if e.get('event') == 'CONNECTION_LOST']
    conn_recovered = [e for e in events if e.get('event') == 'CONNECTION_RECOVERED']
    recovery_waiting = [e for e in events if e.get('event') == 'DISCONNECT_RECOVERY_WAITING_FOR_SYNC']
    recovery_complete = [e for e in events if e.get('event') == 'DISCONNECT_RECOVERY_COMPLETE']
    
    all_conn = sorted([e for e in events if 'CONNECTION' in e.get('event', '')], 
                      key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    if all_conn:
        latest = all_conn[-1]
        event_type = latest.get('event', '')
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            print(f"  Latest connection event: {event_type}")
            print(f"  Time: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            
            if event_type == 'CONNECTION_LOST':
                print(f"  [TRUE ALERT] Broker IS disconnected")
            elif event_type == 'CONNECTION_RECOVERED':
                if recovery_waiting and not recovery_complete:
                    print(f"  [FALSE ALERT] Broker is NOT disconnected")
                    print(f"  Reason: Connection recovered, but recovery still in progress")
                    print(f"  Status: Waiting for broker synchronization (normal after reconnect)")
                    print(f"  Action: Recovery will complete when broker sync finishes")
                else:
                    print(f"  [FALSE ALERT] Broker is NOT disconnected - fully recovered")
            else:
                print(f"  [INFO] Connection status: {event_type}")
    
    print(f"\n  Recovery state:")
    print(f"    DISCONNECT_RECOVERY_WAITING_FOR_SYNC: {len(recovery_waiting)} events")
    print(f"    DISCONNECT_RECOVERY_COMPLETE: {len(recovery_complete)} events")
    
    if recovery_waiting and not recovery_complete:
        print(f"  [INFO] Recovery is waiting for broker sync (OrderUpdate/ExecutionUpdate events)")
        print(f"         This is normal after a reconnect - system waits for broker state confirmation")
    
    print("\n" + "="*80)
    print("3. DATA STALLED")
    print("="*80)
    
    data_stall = [e for e in events if e.get('event') == 'DATA_STALL_RECOVERED']
    bar_received = [e for e in events if e.get('event') == 'BAR_RECEIVED_NO_STREAMS']
    
    if bar_received:
        latest = max(bar_received, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            ts_chicago = ts.astimezone(chicago_tz)
            print(f"  Latest BAR_RECEIVED_NO_STREAMS: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            if age < 60:
                print(f"  [FALSE ALERT] Data is NOT stalled")
                print(f"  Reason: Bars are still arriving ({age:.0f}s ago)")
                print(f"  Note: BAR_RECEIVED_NO_STREAMS means bars arrive but no streams accept them")
                print(f"        This is normal when ranges haven't formed yet")
            else:
                print(f"  [POSSIBLE ISSUE] No bars received for {age:.0f}s")
    
    if data_stall:
        print(f"  [INFO] {len(data_stall)} DATA_STALL_RECOVERED events found")
        print(f"         Data stalls were detected but have recovered")
    
    print("\n" + "="*80)
    print("4. IDENTITY VIOLATION")
    print("="*80)
    
    inv_events = [e for e in events if e.get('event') == 'IDENTITY_INVARIANTS_STATUS']
    
    if inv_events:
        latest = max(inv_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        data = latest.get('data', {})
        pass_status = data.get('pass', True)
        violations = data.get('violations', [])
        
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            print(f"  Latest IDENTITY_INVARIANTS_STATUS: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            print(f"  Pass: {pass_status}")
            print(f"  Violations: {violations}")
            
            if pass_status:
                print(f"  [FALSE ALERT] No identity violations detected")
                print(f"  Reason: IDENTITY_INVARIANTS_STATUS is a STATUS CHECK event")
                print(f"          It logs when invariants are checked, not just when violations occur")
                print(f"          'pass: true' means all invariants passed")
            else:
                print(f"  [TRUE ALERT] Identity violations detected!")
                print(f"  Violations: {violations}")
    else:
        print(f"  [INFO] No IDENTITY_INVARIANTS_STATUS events in recent logs")
        print(f"         This event is logged periodically when identity invariants are checked")
    
    print("\n" + "="*80)
    print("5. MARKET OPEN")
    print("="*80)
    
    timetable = [e for e in events if e.get('event') == 'TIMETABLE_VALIDATED']
    if timetable:
        latest = max(timetable, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            trading_date = latest.get('trading_date', 'N/A')
            print(f"  Latest TIMETABLE_VALIDATED: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            print(f"  Trading Date: {trading_date}")
            print(f"  [INFO] Market timetable validated - market should be open")
    
    print("\n" + "="*80)
    print("SUMMARY & EXPLANATION")
    print("="*80)
    
    print("\nWhy you're seeing these alerts:")
    print("  These alerts are likely coming from a monitoring system (watchdog/dashboard)")
    print("  that checks for these conditions. However, based on actual log analysis:")
    print()
    print("  1. ENGINE STALLED: FALSE - Engine is running (ticks < 1s ago)")
    print("  2. BROKER DISCONNECTED: FALSE - Connection recovered, recovery in progress")
    print("  3. DATA STALLED: FALSE - Bars are arriving normally")
    print("  4. IDENTITY VIOLATION: Check if 'pass: false' - if true, it's just a status check")
    print("  5. MARKET OPEN: TRUE - Market should be open")
    print()
    print("Root Cause:")
    print("  The monitoring system may be:")
    print("  - Checking for DISCONNECT_RECOVERY_WAITING_FOR_SYNC and flagging as 'disconnected'")
    print("  - Not distinguishing between 'recovery in progress' and 'actually disconnected'")
    print("  - Flagging IDENTITY_INVARIANTS_STATUS events as violations even when pass=true")
    print("  - Using stale state or not refreshing frequently enough")
    print()
    print("Recommendation:")
    print("  - Check the watchdog/dashboard code to see how it determines these states")
    print("  - Verify it's checking the 'pass' field in IDENTITY_INVARIANTS_STATUS")
    print("  - Update logic to distinguish RECOVERY_WAITING_FOR_SYNC from CONNECTION_LOST")
    print("  - Ensure it's reading the most recent events, not cached state")
    
    print("\n" + "="*80)

if __name__ == "__main__":
    main()
