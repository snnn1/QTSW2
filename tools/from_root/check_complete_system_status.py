#!/usr/bin/env python3
"""
Comprehensive system status check - analyzes recent logs to verify all streams and system health.
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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=2)  # Last 2 hours
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("COMPREHENSIVE SYSTEM STATUS CHECK")
    print("="*80)
    print(f"Analyzing logs from last 2 hours (since {cutoff.astimezone(chicago_tz).strftime('%Y-%m-%d %H:%M:%S')} CT)")
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
    
    print(f"\nLoaded {len(events):,} events from last 2 hours\n")
    
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
    print(f"  Stall Detection: {len(stall_detected)} detected, {len(stall_recovered)} recovered")
    if stall_detected:
        latest_stall = max(stall_detected, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest_stall.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            print(f"    [WARN] Latest stall: {ts_chicago.strftime('%H:%M:%S')} CT")
    
    # 2. CONNECTION STATUS
    print("\n" + "="*80)
    print("2. CONNECTION STATUS")
    print("="*80)
    
    conn_events = [e for e in events if 'CONNECTION' in e.get('event', '')]
    conn_by_type = defaultdict(list)
    for e in conn_events:
        conn_by_type[e.get('event')].append(e)
    
    print(f"  Total connection events: {len(conn_events)}")
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
                print(f"    {event_type:35} {count:4} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago) | Connection: {conn_name}")
            else:
                print(f"    {event_type:35} {count:4} events")
        else:
            print(f"    {event_type:35} {count:4} events")
    
    # Check for recovery states
    recovery_events = [e for e in events if 'RECOVERY' in e.get('event', '') or 'DISCONNECT' in e.get('event', '')]
    recovery_by_type = defaultdict(list)
    for e in recovery_events:
        recovery_by_type[e.get('event')].append(e)
    
    if recovery_by_type:
        print(f"\n  Recovery State Events:")
        for event_type in sorted(recovery_by_type.keys()):
            count = len(recovery_by_type[event_type])
            if recovery_by_type[event_type]:
                latest = max(recovery_by_type[event_type], key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
                ts = parse_timestamp(latest.get('ts_utc', ''))
                if ts:
                    ts_chicago = ts.astimezone(chicago_tz)
                    age = (datetime.now(timezone.utc) - ts).total_seconds()
                    print(f"    {event_type:40} {count:4} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
    
    # 3. STREAM STATUS
    print("\n" + "="*80)
    print("3. STREAM STATUS")
    print("="*80)
    
    stream_transitions = [e for e in events if e.get('event') == 'STREAM_STATE_TRANSITION']
    streams = defaultdict(lambda: {'states': [], 'latest': None, 'latest_time': None})
    
    for e in stream_transitions:
        stream = e.get('stream', 'UNKNOWN')
        state = e.get('data', {}).get('new_state', 'UNKNOWN')
        ts = parse_timestamp(e.get('ts_utc', ''))
        if stream and state:
            streams[stream]['states'].append((state, ts))
            if not streams[stream]['latest_time'] or (ts and ts > streams[stream]['latest_time']):
                streams[stream]['latest'] = state
                streams[stream]['latest_time'] = ts
    
    print(f"  Active streams: {len(streams)}")
    if streams:
        for stream in sorted(streams.keys()):
            info = streams[stream]
            latest_state = info['latest'] or 'UNKNOWN'
            latest_time = info['latest_time']
            if latest_time:
                ts_chicago = latest_time.astimezone(chicago_tz)
                age = (datetime.now(timezone.utc) - latest_time).total_seconds()
                print(f"    {stream:20} State: {latest_state:20} | Last update: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            else:
                print(f"    {stream:20} State: {latest_state:20} | Last update: N/A")
    
    # 4. STREAM ACTIVITY BY INSTRUMENT
    print("\n" + "="*80)
    print("4. STREAM ACTIVITY BY INSTRUMENT")
    print("="*80)
    
    stream_activity = defaultdict(lambda: {'events': 0, 'latest': None, 'instruments': set()})
    for e in events:
        stream = e.get('stream', '')
        instrument = e.get('instrument', '')
        if stream and stream != '__engine__':
            stream_activity[stream]['events'] += 1
            stream_activity[stream]['instruments'].add(instrument)
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                if not stream_activity[stream]['latest'] or ts > stream_activity[stream]['latest']:
                    stream_activity[stream]['latest'] = ts
    
    if stream_activity:
        for stream in sorted(stream_activity.keys()):
            info = stream_activity[stream]
            instruments = ', '.join(sorted(info['instruments'])) if info['instruments'] else 'N/A'
            latest = info['latest']
            if latest:
                ts_chicago = latest.astimezone(chicago_tz)
                age = (datetime.now(timezone.utc) - latest).total_seconds()
                print(f"    {stream:20} {info['events']:5} events | Instruments: {instruments:30} | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            else:
                print(f"    {stream:20} {info['events']:5} events | Instruments: {instruments}")
    else:
        print("    No stream activity found")
    
    # 5. BAR PROCESSING
    print("\n" + "="*80)
    print("5. BAR PROCESSING")
    print("="*80)
    
    bar_events = [e for e in events if 'BAR' in e.get('event', '')]
    bar_by_type = defaultdict(int)
    for e in bar_events:
        bar_by_type[e.get('event')] += 1
    
    print(f"  Total bar-related events: {len(bar_events)}")
    for event_type in sorted(bar_by_type.keys()):
        count = bar_by_type[event_type]
        print(f"    {event_type:35} {count:6} events")
    
    # Check bar acceptance/rejection
    bar_accepted = [e for e in events if e.get('event') == 'BAR_ACCEPTED']
    bar_rejected = [e for e in events if 'BAR' in e.get('event', '') and 'REJECT' in e.get('event', '')]
    print(f"\n  Bar Acceptance:")
    print(f"    Accepted: {len(bar_accepted)}")
    print(f"    Rejected: {len(bar_rejected)}")
    if bar_accepted:
        latest = max(bar_accepted, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            print(f"    Latest accepted: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
    
    # 6. EXECUTION STATUS
    print("\n" + "="*80)
    print("6. EXECUTION STATUS")
    print("="*80)
    
    exec_events = [e for e in events if 'ORDER' in e.get('event', '') or 'EXECUTION' in e.get('event', '') or 'INTENT' in e.get('event', '')]
    exec_by_type = defaultdict(int)
    for e in exec_events:
        exec_by_type[e.get('event')] += 1
    
    print(f"  Total execution events: {len(exec_events)}")
    for event_type in sorted(exec_by_type.keys()):
        count = exec_by_type[event_type]
        print(f"    {event_type:35} {count:6} events")
    
    # Check for execution blocks
    exec_blocked = [e for e in events if e.get('event') == 'EXECUTION_BLOCKED']
    exec_allowed = [e for e in events if e.get('event') == 'EXECUTION_ALLOWED']
    if exec_blocked or exec_allowed:
        print(f"\n  Execution Gating:")
        print(f"    Blocked: {len(exec_blocked)}")
        print(f"    Allowed: {len(exec_allowed)}")
        if exec_blocked:
            latest = max(exec_blocked, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                data = latest.get('data', {})
                reason = data.get('reason', 'N/A')
                print(f"    Latest blocked: {ts_chicago.strftime('%H:%M:%S')} CT | Reason: {reason}")
    
    # 7. ERRORS AND WARNINGS
    print("\n" + "="*80)
    print("7. ERRORS AND WARNINGS")
    print("="*80)
    
    error_events = [e for e in events if e.get('level') in ('ERROR', 'WARN') or 'ERROR' in e.get('event', '') or 'FAIL' in e.get('event', '')]
    error_by_type = defaultdict(list)
    for e in error_events:
        error_by_type[e.get('event')].append(e)
    
    if error_by_type:
        print(f"  Total errors/warnings: {len(error_events)}")
        for event_type in sorted(error_by_type.keys())[:20]:  # Show top 20
            count = len(error_by_type[event_type])
            latest = max(error_by_type[event_type], key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                age = (datetime.now(timezone.utc) - ts).total_seconds()
                data = latest.get('data', {})
                note = data.get('note', '')[:50] if data.get('note') else ''
                print(f"    {event_type:40} {count:4} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
                if note:
                    print(f"      Note: {note}")
    else:
        print("  [OK] No errors or warnings found")
    
    # 8. TRADING DATE TRACKING
    print("\n" + "="*80)
    print("8. TRADING DATE TRACKING")
    print("="*80)
    
    # Check connection events for trading_date
    conn_with_td = [e for e in conn_events if e.get('data', {}).get('trading_date')]
    conn_empty_td = [e for e in conn_events if e.get('data', {}).get('trading_date') == '']
    conn_null_td = [e for e in conn_events if e.get('data', {}).get('trading_date') is None]
    
    print(f"  Connection events with trading_date:")
    print(f"    Has trading_date: {len(conn_with_td)}")
    print(f"    Empty string: {len(conn_empty_td)}")
    print(f"    Null/missing: {len(conn_null_td)}")
    
    if conn_empty_td:
        print(f"    [WARN] Found {len(conn_empty_td)} connection events with empty trading_date string")
        print(f"           This should be null, not empty string (SetTradingDate should prevent this)")
    
    # Show trading dates in recent connection events
    if conn_with_td:
        print(f"\n  Recent trading dates in connection events:")
        for e in conn_with_td[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            td = e.get('data', {}).get('trading_date', '')
            event_type = e.get('event', '')
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                print(f"    {ts_chicago.strftime('%H:%M:%S')} CT | {event_type:35} | Trading Date: {td}")
    
    # 9. NOTIFICATION EVENTS
    print("\n" + "="*80)
    print("9. NOTIFICATION EVENTS")
    print("="*80)
    
    notif_events = [e for e in events if 'NOTIFICATION' in e.get('event', '') or 'PUSHOVER' in e.get('event', '')]
    notif_by_type = defaultdict(int)
    for e in notif_events:
        notif_by_type[e.get('event')] += 1
    
    if notif_by_type:
        print(f"  Total notification events: {len(notif_events)}")
        for event_type in sorted(notif_by_type.keys()):
            count = notif_by_type[event_type]
            print(f"    {event_type:40} {count:4} events")
    else:
        print("  No notification events found")
    
    # 10. SYSTEM HEALTH SUMMARY
    print("\n" + "="*80)
    print("10. SYSTEM HEALTH SUMMARY")
    print("="*80)
    
    # Calculate health score
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
    
    # Check connection
    if conn_empty_td:
        warnings.append(f"Found {len(conn_empty_td)} connection events with empty trading_date (should be null)")
    
    # Check stalls
    if stall_detected and not stall_recovered:
        issues.append("Engine tick stall detected but not recovered")
    
    # Check errors
    critical_errors = [e for e in error_events if e.get('level') == 'ERROR' and 'CRITICAL' in e.get('event', '')]
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
    
    # Overall status
    print(f"\n  Overall Status: {'[HEALTHY]' if not issues else '[ISSUES DETECTED]'}")
    print(f"  Active Streams: {len(streams)}")
    print(f"  Total Events (2h): {len(events):,}")
    print(f"  Engine Ticks: {len(tick_events):,}")
    print(f"  Connection Events: {len(conn_events)}")
    
    print("\n" + "="*80)
    print("ANALYSIS COMPLETE")
    print("="*80)

if __name__ == "__main__":
    main()
