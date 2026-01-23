"""
Check Recent Robot Status - Analyze logs since restart
"""
import json
import sys
from pathlib import Path
from datetime import datetime, timedelta
from collections import defaultdict

qtsw2_root = Path(__file__).parent.parent
log_dir = qtsw2_root / "logs" / "robot"

def parse_timestamp(ts_str):
    """Parse ISO8601 timestamp"""
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    print("="*80)
    print("ROBOT STATUS - SINCE RESTART")
    print("="*80)
    
    # Read ENGINE log
    engine_log = log_dir / "robot_ENGINE.jsonl"
    if not engine_log.exists():
        print(f"[ERROR] Engine log not found: {engine_log}")
        return
    
    events = []
    with open(engine_log, 'r', encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    event = json.loads(line)
                    events.append(event)
                except:
                    pass
    
    if not events:
        print("[ERROR] No events found")
        return
    
    # Sort by timestamp
    events.sort(key=lambda e: parse_timestamp(e.get('ts_utc', '')) or datetime.min)
    
    # Find most recent ENGINE_START
    engine_starts = [e for e in events if (e.get('event') == 'ENGINE_START' or e.get('event_type') == 'ENGINE_START')]
    if not engine_starts:
        print("[INFO] No ENGINE_START found - showing last 200 events")
        recent_events = events[-200:]
    else:
        last_start = engine_starts[-1]
        start_time = parse_timestamp(last_start.get('ts_utc', ''))
        if start_time:
            # Get events since last start
            recent_events = [e for e in events if parse_timestamp(e.get('ts_utc', '')) >= start_time]
            print(f"[INFO] Found ENGINE_START at {start_time.strftime('%Y-%m-%d %H:%M:%S UTC')}")
        else:
            recent_events = events[-200:]
    
    print(f"\n[ANALYSIS PERIOD]")
    if recent_events:
        first_ts = parse_timestamp(recent_events[0].get('ts_utc', ''))
        last_ts = parse_timestamp(recent_events[-1].get('ts_utc', ''))
        if first_ts and last_ts:
            print(f"  From: {first_ts.strftime('%Y-%m-%d %H:%M:%S UTC')}")
            print(f"  To:   {last_ts.strftime('%Y-%m-%d %H:%M:%S UTC')}")
            duration = last_ts - first_ts
            print(f"  Duration: {duration}")
        print(f"  Total events: {len(recent_events)}")
    
    # Extract run_id
    run_ids = set(e.get('run_id') for e in recent_events if e.get('run_id'))
    if run_ids:
        print(f"\n[RUN ID]")
        for rid in sorted(run_ids):
            count = sum(1 for e in recent_events if e.get('run_id') == rid)
            print(f"  {rid[:16]}... ({count} events)")
    
    # Key events summary
    print(f"\n[KEY EVENTS]")
    key_events = {
        'ENGINE_START': 'Engine Started',
        'ENGINE_STOP': 'Engine Stopped',
        'HEALTH_MONITOR_CONFIG_LOADED': 'Health Monitor Config',
        'HEALTH_MONITOR_STARTED': 'Health Monitor Started',
        'PUSHOVER_CONFIG_MISSING': 'Pushover Config Missing',
        'CRITICAL_EVENT_REPORTED': 'Critical Event Reported',
        'PUSHOVER_NOTIFY_ENQUEUED': 'Notification Enqueued',
        'EXECUTION_GATE_INVARIANT_VIOLATION': 'Execution Gate Violation',
        'DISCONNECT_FAIL_CLOSED_ENTERED': 'Disconnect Fail-Closed',
        'STREAMS_CREATED': 'Streams Created',
        'RANGE_LOCKED': 'Range Locked',
        'OPERATOR_BANNER': 'Operator Banner'
    }
    
    for event_type, label in key_events.items():
        matches = [e for e in recent_events if (e.get('event') == event_type or e.get('event_type') == event_type)]
        if matches:
            latest = matches[-1]
            ts = parse_timestamp(latest.get('ts_utc', ''))
            ts_str = ts.strftime('%H:%M:%S') if ts else latest.get('ts_utc', '')[:19]
            data = latest.get('data', {})
            
            if event_type == 'HEALTH_MONITOR_CONFIG_LOADED':
                print(f"  ✓ {label}:")
                print(f"      Enabled: {data.get('enabled', 'N/A')}")
                print(f"      Pushover Enabled: {data.get('pushover_enabled', 'N/A')}")
                print(f"      User Key Length: {data.get('pushover_user_key_length', 0)}")
                print(f"      App Token Length: {data.get('pushover_app_token_length', 0)}")
                print(f"      Time: {ts_str}")
            elif event_type == 'HEALTH_MONITOR_STARTED':
                print(f"  ✓ {label}:")
                print(f"      Enabled: {data.get('enabled', 'N/A')}")
                print(f"      Pushover Configured: {data.get('pushover_configured', 'N/A')}")
                print(f"      Time: {ts_str}")
            elif event_type == 'STREAMS_CREATED':
                print(f"  ✓ {label}: {len(matches)} times")
                if data.get('streams'):
                    print(f"      Count: {len(data.get('streams', []))}")
                print(f"      Latest: {ts_str}")
            elif event_type == 'CRITICAL_EVENT_REPORTED':
                print(f"  ⚠ {label}: {len(matches)} times")
                for m in matches:
                    m_data = m.get('data', {})
                    print(f"      Event: {m_data.get('event_type', 'N/A')}")
                    print(f"      Run ID: {m_data.get('run_id', 'N/A')[:16]}...")
                    print(f"      Time: {parse_timestamp(m.get('ts_utc', '')).strftime('%H:%M:%S') if parse_timestamp(m.get('ts_utc', '')) else 'N/A'}")
            elif event_type == 'PUSHOVER_NOTIFY_ENQUEUED':
                print(f"  ✓ {label}: {len(matches)} times")
                for m in matches[-3:]:  # Show last 3
                    m_data = m.get('data', {})
                    print(f"      Title: {m_data.get('title', 'N/A')}")
                    print(f"      Priority: {m_data.get('priority', 'N/A')}")
                    print(f"      Time: {parse_timestamp(m.get('ts_utc', '')).strftime('%H:%M:%S') if parse_timestamp(m.get('ts_utc', '')) else 'N/A'}")
            else:
                print(f"  ✓ {label}: {len(matches)} times (latest: {ts_str})")
    
    # Error/Warning summary
    errors = [e for e in recent_events if e.get('level') in ['ERROR', 'error']]
    warnings = [e for e in recent_events if e.get('level') in ['WARN', 'warn', 'WARNING', 'warning']]
    
    print(f"\n[ERRORS & WARNINGS]")
    print(f"  Errors: {len(errors)}")
    print(f"  Warnings: {len(warnings)}")
    
    if errors:
        print(f"\n  Recent Errors:")
        for e in errors[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            ts_str = ts.strftime('%H:%M:%S') if ts else e.get('ts_utc', '')[:19]
            print(f"    [{ts_str}] {e.get('event', 'N/A')}: {e.get('message', 'N/A')[:60]}")
    
    if warnings:
        print(f"\n  Recent Warnings:")
        for e in warnings[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            ts_str = ts.strftime('%H:%M:%S') if ts else e.get('ts_utc', '')[:19]
            print(f"    [{ts_str}] {e.get('event', 'N/A')}: {e.get('message', 'N/A')[:60]}")
    
    # Stream state summary
    range_locked = [e for e in recent_events if e.get('event') == 'RANGE_LOCKED']
    if range_locked:
        print(f"\n[STREAM STATES]")
        print(f"  RANGE_LOCKED events: {len(range_locked)}")
        latest_ranges = {}
        for e in range_locked:
            data = e.get('data', {})
            stream = data.get('stream', 'unknown')
            latest_ranges[stream] = parse_timestamp(e.get('ts_utc', ''))
        
        print(f"  Streams with locked ranges: {len(latest_ranges)}")
        for stream, ts in sorted(latest_ranges.items(), key=lambda x: x[1] or datetime.min)[-5:]:
            ts_str = ts.strftime('%H:%M:%S') if ts else 'N/A'
            print(f"    {stream}: {ts_str}")
    
    # Notification status check
    print(f"\n[NOTIFICATION STATUS]")
    config_loaded = [e for e in recent_events if e.get('event') == 'HEALTH_MONITOR_CONFIG_LOADED']
    monitor_started = [e for e in recent_events if e.get('event') == 'HEALTH_MONITOR_STARTED']
    pushover_missing = [e for e in recent_events if e.get('event') == 'PUSHOVER_CONFIG_MISSING']
    
    if config_loaded:
        cfg = config_loaded[-1].get('data', {})
        if cfg.get('pushover_user_key_length', 0) > 0 and cfg.get('pushover_app_token_length', 0) > 0:
            print(f"  ✓ Pushover credentials loaded")
        else:
            print(f"  ✗ Pushover credentials missing")
    
    if monitor_started:
        mon = monitor_started[-1].get('data', {})
        if mon.get('pushover_configured'):
            print(f"  ✓ Notification service configured and started")
        else:
            print(f"  ✗ Notification service not configured")
    
    if pushover_missing:
        print(f"  ⚠ Pushover config missing warning detected")
    
    critical_reported = [e for e in recent_events if e.get('event') == 'CRITICAL_EVENT_REPORTED']
    notifications_sent = [e for e in recent_events if e.get('event') == 'PUSHOVER_NOTIFY_ENQUEUED']
    
    print(f"  Critical events reported: {len(critical_reported)}")
    print(f"  Notifications enqueued: {len(notifications_sent)}")
    
    if critical_reported and not notifications_sent:
        print(f"  ⚠ WARNING: Critical events reported but no notifications sent!")

if __name__ == '__main__':
    main()
