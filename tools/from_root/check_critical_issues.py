#!/usr/bin/env python3
"""
Check for critical issues: ENGINE STALLED, BROKER DISCONNECTED, DATA STALLED, IDENTITY VIOLATION
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict
import pytz

def parse_timestamp(ts_str: str):
    """Parse ISO timestamp"""
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
    cutoff = datetime.now(timezone.utc) - timedelta(minutes=30)
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("CRITICAL ISSUES ANALYSIS")
    print("="*80)
    print(f"Analyzing logs from last 30 minutes")
    print("="*80)
    
    # Load all events
    events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True):
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
    
    print(f"\nLoaded {len(events):,} events from last 30 minutes\n")
    
    # 1. ENGINE STALLED
    print("="*80)
    print("1. ENGINE STALLED")
    print("="*80)
    
    stall_detected = [e for e in events if e.get('event') == 'ENGINE_TICK_STALL_DETECTED']
    stall_recovered = [e for e in events if e.get('event') == 'ENGINE_TICK_STALL_RECOVERED']
    tick_events = [e for e in events if e.get('event') == 'ENGINE_TICK_CALLSITE']
    
    print(f"  Stall Detected: {len(stall_detected)}")
    print(f"  Stall Recovered: {len(stall_recovered)}")
    print(f"  Engine Ticks: {len(tick_events)}")
    
    if stall_detected:
        latest_stall = max(stall_detected, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest_stall.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            data = latest_stall.get('data', {})
            stall_duration = data.get('stall_duration_ms', 'N/A')
            print(f"\n  Latest Stall:")
            print(f"    Time: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            print(f"    Duration: {stall_duration}ms")
            if not stall_recovered or max(parse_timestamp(e.get('ts_utc', '')) for e in stall_recovered if parse_timestamp(e.get('ts_utc', ''))) < ts:
                print(f"    [ERROR] Stall NOT recovered yet!")
    
    if tick_events:
        latest_tick = max(tick_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest_tick.get('ts_utc', ''))
        if ts:
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            ts_chicago = ts.astimezone(chicago_tz)
            print(f"\n  Latest Engine Tick:")
            print(f"    Time: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.1f}s ago)")
            if age > 15:
                print(f"    [ERROR] Engine appears stalled (no ticks for {age:.0f}s)")
            else:
                print(f"    [OK] Engine is running")
    
    # 2. BROKER DISCONNECTED
    print("\n" + "="*80)
    print("2. BROKER DISCONNECTED")
    print("="*80)
    
    conn_lost = [e for e in events if e.get('event') == 'CONNECTION_LOST']
    conn_recovered = [e for e in events if e.get('event') == 'CONNECTION_RECOVERED']
    conn_lost_sustained = [e for e in events if e.get('event') == 'CONNECTION_LOST_SUSTAINED']
    disconnect_fail_closed = [e for e in events if e.get('event') == 'DISCONNECT_FAIL_CLOSED_ENTERED']
    recovery_waiting = [e for e in events if e.get('event') == 'DISCONNECT_RECOVERY_WAITING_FOR_SYNC']
    recovery_complete = [e for e in events if e.get('event') == 'DISCONNECT_RECOVERY_COMPLETE']
    
    print(f"  CONNECTION_LOST: {len(conn_lost)}")
    print(f"  CONNECTION_RECOVERED: {len(conn_recovered)}")
    print(f"  CONNECTION_LOST_SUSTAINED: {len(conn_lost_sustained)}")
    print(f"  DISCONNECT_FAIL_CLOSED_ENTERED: {len(disconnect_fail_closed)}")
    print(f"  DISCONNECT_RECOVERY_WAITING_FOR_SYNC: {len(recovery_waiting)}")
    print(f"  DISCONNECT_RECOVERY_COMPLETE: {len(recovery_complete)}")
    
    # Check current connection status
    all_conn_events = sorted([e for e in events if 'CONNECTION' in e.get('event', '')], 
                              key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    if all_conn_events:
        latest_conn = all_conn_events[-1]
        ts = parse_timestamp(latest_conn.get('ts_utc', ''))
        event_type = latest_conn.get('event', '')
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            data = latest_conn.get('data', {})
            conn_name = data.get('connection_name', 'N/A')
            print(f"\n  Latest Connection Event:")
            print(f"    Time: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            print(f"    Event: {event_type}")
            print(f"    Connection: {conn_name}")
            
            if event_type in ['CONNECTION_LOST', 'CONNECTION_LOST_SUSTAINED']:
                print(f"    [ERROR] Broker is disconnected!")
            elif event_type == 'CONNECTION_RECOVERED':
                if recovery_waiting and not recovery_complete:
                    print(f"    [WARN] Connection recovered but recovery still in progress")
                else:
                    print(f"    [OK] Connection recovered")
            else:
                print(f"    [INFO] Connection status: {event_type}")
    
    # 3. DATA STALLED
    print("\n" + "="*80)
    print("3. DATA STALLED")
    print("="*80)
    
    data_loss = [e for e in events if e.get('event') == 'DATA_LOSS_DETECTED']
    data_stall = [e for e in events if 'DATA' in e.get('event', '') and 'STALL' in e.get('event', '')]
    bar_received = [e for e in events if e.get('event') == 'BAR_RECEIVED_NO_STREAMS']
    bar_accepted = [e for e in events if e.get('event') == 'BAR_ACCEPTED']
    
    print(f"  DATA_LOSS_DETECTED: {len(data_loss)}")
    print(f"  DATA_STALL events: {len(data_stall)}")
    print(f"  BAR_RECEIVED_NO_STREAMS: {len(bar_received)}")
    print(f"  BAR_ACCEPTED: {len(bar_accepted)}")
    
    if data_loss:
        latest = max(data_loss, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            data = latest.get('data', {})
            note = data.get('note', '')
            print(f"\n  Latest DATA_LOSS_DETECTED:")
            print(f"    Time: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            print(f"    Note: {note}")
    
    if bar_received:
        latest = max(bar_received, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            print(f"\n  Latest BAR_RECEIVED_NO_STREAMS:")
            print(f"    Time: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            if age > 60:
                print(f"    [WARN] No bars received for {age:.0f}s - possible data stall")
    
    # 4. IDENTITY VIOLATION
    print("\n" + "="*80)
    print("4. IDENTITY VIOLATION")
    print("="*80)
    
    identity_violations = [e for e in events if 'IDENTITY' in e.get('event', '') or 'VIOLATION' in e.get('event', '')]
    duplicate_errors = [e for e in events if 'DUPLICATE' in e.get('event', '')]
    
    print(f"  IDENTITY_VIOLATION events: {len(identity_violations)}")
    print(f"  DUPLICATE events: {len(duplicate_errors)}")
    
    if identity_violations:
        print(f"\n  Identity Violation Events:")
        for e in identity_violations[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                event_type = e.get('event', '')
                data = e.get('data', {})
                note = data.get('note', '')[:80] if data.get('note') else ''
                print(f"    {ts_chicago.strftime('%H:%M:%S')} CT | {event_type:40} | {note}")
    
    # Check for ERROR level events
    error_events = [e for e in events if e.get('level') == 'ERROR']
    print(f"\n  Total ERROR level events: {len(error_events)}")
    if error_events:
        error_by_type = defaultdict(int)
        for e in error_events:
            error_by_type[e.get('event')] += 1
        
        print(f"  Error Types:")
        for event_type, count in sorted(error_by_type.items(), key=lambda x: x[1], reverse=True)[:10]:
            print(f"    {event_type:40} {count:4} events")
    
    # 5. MARKET OPEN STATUS
    print("\n" + "="*80)
    print("5. MARKET OPEN STATUS")
    print("="*80)
    
    timetable_validated = [e for e in events if e.get('event') == 'TIMETABLE_VALIDATED']
    market_open = [e for e in events if 'MARKET' in e.get('event', '') and 'OPEN' in e.get('event', '')]
    
    print(f"  TIMETABLE_VALIDATED: {len(timetable_validated)}")
    print(f"  MARKET_OPEN events: {len(market_open)}")
    
    if timetable_validated:
        latest = max(timetable_validated, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            trading_date = latest.get('trading_date', 'N/A')
            print(f"\n  Latest TIMETABLE_VALIDATED:")
            print(f"    Time: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            print(f"    Trading Date: {trading_date}")
    
    # 6. ROOT CAUSE ANALYSIS
    print("\n" + "="*80)
    print("6. ROOT CAUSE ANALYSIS")
    print("="*80)
    
    print("\n  Timeline of Critical Events:")
    critical_events = []
    for e in events:
        event_type = e.get('event', '')
        if any(x in event_type for x in ['STALL', 'CONNECTION', 'DATA_LOSS', 'IDENTITY', 'VIOLATION', 'ERROR']):
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                critical_events.append((ts, event_type, e))
    
    critical_events.sort(key=lambda x: x[0])
    
    for ts, event_type, e in critical_events[-20:]:
        ts_chicago = ts.astimezone(chicago_tz)
        data = e.get('data', {})
        note = data.get('note', '')[:60] if data.get('note') else ''
        print(f"    {ts_chicago.strftime('%H:%M:%S')} CT | {event_type:40} | {note}")
    
    # Summary
    print("\n" + "="*80)
    print("SUMMARY")
    print("="*80)
    
    issues = []
    if stall_detected and (not stall_recovered or max(parse_timestamp(e.get('ts_utc', '')) for e in stall_recovered if parse_timestamp(e.get('ts_utc', ''))) < max(parse_timestamp(e.get('ts_utc', '')) for e in stall_detected if parse_timestamp(e.get('ts_utc', '')))):
        issues.append("ENGINE STALLED - Not recovered")
    
    if all_conn_events and all_conn_events[-1].get('event') in ['CONNECTION_LOST', 'CONNECTION_LOST_SUSTAINED']:
        issues.append("BROKER DISCONNECTED")
    
    if bar_received:
        latest_bar = max(bar_received, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts_bar = parse_timestamp(latest_bar.get('ts_utc', ''))
        if ts_bar:
            age_bar = (datetime.now(timezone.utc) - ts_bar).total_seconds()
            if age_bar > 300:  # 5 minutes
                issues.append("DATA STALLED - No bars received")
    
    if identity_violations:
        issues.append("IDENTITY VIOLATION detected")
    
    if issues:
        print("\n  [CRITICAL] Issues Detected:")
        for issue in issues:
            print(f"    - {issue}")
    else:
        print("\n  [OK] No active critical issues detected")
    
    print("="*80)

if __name__ == "__main__":
    main()
