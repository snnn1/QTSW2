#!/usr/bin/env python3
"""
Focused check on core system functionality - engine, connection, logging, trading date.
Stream activity excluded since ranges haven't formed yet.
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
    cutoff = datetime.now(timezone.utc) - timedelta(minutes=30)  # Last 30 minutes
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("CORE FUNCTIONALITY CHECK")
    print("="*80)
    print(f"Analyzing logs from last 30 minutes (since {cutoff.astimezone(chicago_tz).strftime('%H:%M:%S')} CT)")
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
    
    # 1. ENGINE HEALTH
    print("="*80)
    print("1. ENGINE HEALTH")
    print("="*80)
    
    tick_events = [e for e in events if e.get('event') == 'ENGINE_TICK_CALLSITE']
    if tick_events:
        latest_tick = max(tick_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest_tick.get('ts_utc', ''))
        if ts:
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            ts_chicago = ts.astimezone(chicago_tz)
            print(f"  ENGINE_TICK_CALLSITE: {len(tick_events):,} events")
            print(f"    Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.1f}s ago)")
            if age < 15:
                print(f"    [OK] Engine is active and healthy")
            elif age < 60:
                print(f"    [WARN] Engine tick age exceeds threshold (15s) but < 60s")
            else:
                print(f"    [ERROR] Engine appears stopped (no ticks for {age:.0f}s)")
    else:
        print(f"  [ERROR] No ENGINE_TICK_CALLSITE events found")
    
    # Check for stalls
    stall_detected = [e for e in events if e.get('event') == 'ENGINE_TICK_STALL_DETECTED']
    stall_recovered = [e for e in events if e.get('event') == 'ENGINE_TICK_STALL_RECOVERED']
    if stall_detected or stall_recovered:
        print(f"  Stall Detection: {len(stall_detected)} detected, {len(stall_recovered)} recovered")
    
    # 2. CONNECTION STATUS & TRADING DATE
    print("\n" + "="*80)
    print("2. CONNECTION STATUS & TRADING DATE HANDLING")
    print("="*80)
    
    conn_events = [e for e in events if 'CONNECTION' in e.get('event', '')]
    conn_by_type = defaultdict(list)
    for e in conn_events:
        conn_by_type[e.get('event')].append(e)
    
    print(f"  Total connection events: {len(conn_events)}")
    
    empty_td_count = 0
    null_td_count = 0
    has_td_count = 0
    
    for event_type in sorted(conn_by_type.keys()):
        count = len(conn_by_type[event_type])
        if conn_by_type[event_type]:
            latest = max(conn_by_type[event_type], key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                age = (datetime.now(timezone.utc) - ts).total_seconds()
                data = latest.get('data', {})
                conn_name = data.get('connection_name', 'N/A')
                trading_date = data.get('trading_date')
                
                # Check trading_date
                if trading_date == "":
                    empty_td_count += 1
                    td_status = "[ISSUE: Empty string]"
                elif trading_date is None:
                    null_td_count += 1
                    td_status = "[OK: Null]"
                else:
                    has_td_count += 1
                    td_status = f"[OK: {trading_date}]"
                
                print(f"    {event_type:35} {count:4} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago) | {td_status}")
    
    print(f"\n  Trading Date Summary:")
    print(f"    Has trading_date: {has_td_count}")
    print(f"    Empty string: {empty_td_count} {'[ISSUE]' if empty_td_count > 0 else '[OK]'}")
    print(f"    Null/missing: {null_td_count} [OK - Expected]")
    
    if empty_td_count > 0:
        print(f"\n    [ERROR] Found {empty_td_count} connection events with empty trading_date string")
        print(f"           SetTradingDate() should prevent this - DLL may need update")
    else:
        print(f"\n    [OK] No empty trading_date strings - SetTradingDate() working correctly")
    
    # 3. RECOVERY STATE
    print("\n" + "="*80)
    print("3. RECOVERY STATE")
    print("="*80)
    
    recovery_events = [e for e in events if 'RECOVERY' in e.get('event', '') or 'DISCONNECT' in e.get('event', '')]
    recovery_by_type = defaultdict(list)
    for e in recovery_events:
        recovery_by_type[e.get('event')].append(e)
    
    if recovery_by_type:
        for event_type in sorted(recovery_by_type.keys()):
            count = len(recovery_by_type[event_type])
            if recovery_by_type[event_type]:
                latest = max(recovery_by_type[event_type], key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
                ts = parse_timestamp(latest.get('ts_utc', ''))
                if ts:
                    ts_chicago = ts.astimezone(chicago_tz)
                    age = (datetime.now(timezone.utc) - ts).total_seconds()
                    print(f"    {event_type:40} {count:4} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
    else:
        print("    [OK] No recovery events (system in normal operation)")
    
    # 4. LOGGING VERIFICATION
    print("\n" + "="*80)
    print("4. LOGGING VERIFICATION")
    print("="*80)
    
    # Check event types are being logged
    event_types = defaultdict(int)
    for e in events:
        event_types[e.get('event', 'UNKNOWN')] += 1
    
    print(f"  Unique event types logged: {len(event_types)}")
    print(f"  Total events: {len(events):,}")
    
    # Check for critical event types
    critical_types = [
        'ENGINE_TICK_CALLSITE',
        'CONNECTION_LOST',
        'CONNECTION_RECOVERED',
        'CONNECTION_RECOVERED_NOTIFICATION',
        'DISCONNECT_FAIL_CLOSED_ENTERED',
        'DISCONNECT_RECOVERY_STARTED',
        'DISCONNECT_RECOVERY_COMPLETE'
    ]
    
    print(f"\n  Critical Event Types:")
    for event_type in critical_types:
        count = event_types.get(event_type, 0)
        status = "[OK]" if count > 0 or event_type not in ['CONNECTION_RECOVERED_NOTIFICATION', 'DISCONNECT_RECOVERY_COMPLETE'] else "[NONE]"
        print(f"    {event_type:40} {count:6} events {status}")
    
    # 5. ERRORS AND WARNINGS
    print("\n" + "="*80)
    print("5. ERRORS AND WARNINGS")
    print("="*80)
    
    error_events = [e for e in events if e.get('level') == 'ERROR']
    warn_events = [e for e in events if e.get('level') == 'WARN']
    
    print(f"  ERROR level events: {len(error_events)}")
    print(f"  WARN level events: {len(warn_events)}")
    
    if error_events:
        error_by_type = defaultdict(int)
        for e in error_events:
            error_by_type[e.get('event')] += 1
        
        print(f"\n  Error Types:")
        for event_type, count in sorted(error_by_type.items(), key=lambda x: x[1], reverse=True)[:10]:
            print(f"    {event_type:40} {count:4} events")
    else:
        print(f"  [OK] No ERROR level events")
    
    # 6. SYSTEM INITIALIZATION
    print("\n" + "="*80)
    print("6. SYSTEM INITIALIZATION")
    print("="*80)
    
    init_events = [e for e in events if 'INIT' in e.get('event', '') or 'STARTED' in e.get('event', '')]
    if init_events:
        print(f"  Initialization events: {len(init_events)}")
        for e in init_events[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                print(f"    {ts_chicago.strftime('%H:%M:%S')} CT | {e.get('event', '')}")
    else:
        print(f"  [INFO] No initialization events in last 30 minutes (system already running)")
    
    # 7. SUMMARY
    print("\n" + "="*80)
    print("7. CORE FUNCTIONALITY SUMMARY")
    print("="*80)
    
    issues = []
    warnings = []
    
    # Check engine
    if tick_events:
        latest_tick_ts = max(parse_timestamp(e.get('ts_utc', '')) for e in tick_events if parse_timestamp(e.get('ts_utc', '')))
        if latest_tick_ts:
            age = (datetime.now(timezone.utc) - latest_tick_ts).total_seconds()
            if age > 60:
                issues.append(f"Engine appears stopped (no ticks for {age:.0f}s)")
            elif age > 15:
                warnings.append(f"Engine tick age exceeds threshold ({age:.0f}s)")
    else:
        issues.append("No engine ticks found")
    
    # Check trading date
    if empty_td_count > 0:
        issues.append(f"Found {empty_td_count} connection events with empty trading_date (should be null)")
    
    # Check errors
    if error_events:
        critical_errors = [e for e in error_events if 'CRITICAL' in e.get('event', '')]
        if critical_errors:
            issues.append(f"Found {len(critical_errors)} critical errors")
    
    # Print summary
    if issues:
        print("  [ERROR] Issues found:")
        for issue in issues:
            print(f"    - {issue}")
    else:
        print("  [OK] No critical issues found")
    
    if warnings:
        print("\n  [WARN] Warnings:")
        for warning in warnings:
            print(f"    - {warning}")
    else:
        print("  [OK] No warnings")
    
    print(f"\n  Overall Status: {'[HEALTHY]' if not issues else '[ISSUES DETECTED]'}")
    print(f"  Engine Ticks: {len(tick_events):,}")
    print(f"  Connection Events: {len(conn_events)}")
    print(f"  Total Events (30m): {len(events):,}")
    print(f"  Logging: {'[OK]' if len(events) > 0 else '[ISSUE]'}")
    
    print("\n" + "="*80)
    print("CORE FUNCTIONALITY CHECK COMPLETE")
    print("="*80)
    print("\nNote: Stream activity not checked (ranges haven't formed yet, as expected)")

if __name__ == "__main__":
    main()
