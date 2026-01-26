"""Comprehensive assessment of current robot status"""
import json
from pathlib import Path
from datetime import datetime
from collections import Counter

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    log_dir = Path("logs/robot")
    
    # Read all ENGINE logs
    all_logs = sorted([f for f in log_dir.glob('robot_ENGINE*.jsonl')], 
                      key=lambda p: p.stat().st_mtime, reverse=True)
    
    print("="*80)
    print("ROBOT STATUS ASSESSMENT")
    print("="*80)
    print(f"\nFound {len(all_logs)} log files")
    
    # Read events from most recent log files
    events = []
    for log_file in all_logs[:2]:  # Check 2 most recent
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            events.append(json.loads(line))
                        except:
                            pass
        except:
            pass
    
    # Sort by timestamp
    events.sort(key=lambda e: parse_timestamp(e.get('ts_utc', '')) or datetime.min)
    
    # Get recent events (last 200)
    recent = events[-200:] if len(events) > 200 else events
    
    print(f"Total events in recent logs: {len(events)}")
    print(f"Analyzing last {len(recent)} events\n")
    
    # Find latest ENGINE_START
    starts = [e for e in recent if e.get('event') == 'ENGINE_START']
    if starts:
        latest_start = starts[-1]
        start_time = parse_timestamp(latest_start.get('ts_utc', ''))
        run_id = latest_start.get('run_id', 'N/A')[:32]
        print(f"[LATEST ENGINE START]")
        print(f"  Time: {start_time.strftime('%Y-%m-%d %H:%M:%S UTC') if start_time else 'N/A'}")
        print(f"  Run ID: {run_id}...")
        print()
    
    # Events since latest start
    if starts:
        events_since_start = [e for e in recent 
                              if parse_timestamp(e.get('ts_utc', '')) and 
                              parse_timestamp(e.get('ts_utc', '')) >= start_time]
    else:
        events_since_start = recent[-50:]
    
    print(f"[ACTIVITY SINCE LATEST START]")
    print(f"  Events: {len(events_since_start)}")
    
    # Level distribution
    levels = Counter([e.get('level', 'UNKNOWN') for e in events_since_start])
    print(f"\n  Level distribution:")
    for level, count in sorted(levels.items()):
        print(f"    {level}: {count}")
    
    # Top event types
    event_types = Counter([e.get('event', 'UNKNOWN') for e in events_since_start])
    print(f"\n  Top 10 event types:")
    for event_type, count in event_types.most_common(10):
        print(f"    {event_type}: {count}")
    
    # Stream activity
    stream_events = [e for e in events_since_start if 'STREAM' in e.get('event', '').upper()]
    print(f"\n[STREAM ACTIVITY]")
    print(f"  Stream-related events: {len(stream_events)}")
    if stream_events:
        stream_types = Counter([e.get('event') for e in stream_events])
        for stype, count in sorted(stream_types.items()):
            print(f"    {stype}: {count}")
    
    # Health monitor status
    health_events = [e for e in events_since_start 
                    if any(x in e.get('event', '').upper() 
                          for x in ['HEALTH', 'PUSHOVER', 'CRITICAL', 'NOTIFICATION'])]
    
    print(f"\n[HEALTH MONITOR STATUS]")
    print(f"  Health-related events: {len(health_events)}")
    
    # Find latest health monitor config
    config_loaded = [e for e in events_since_start 
                     if e.get('event') == 'HEALTH_MONITOR_CONFIG_LOADED']
    monitor_started = [e for e in events_since_start 
                      if e.get('event') == 'HEALTH_MONITOR_STARTED']
    
    if config_loaded:
        latest_cfg = config_loaded[-1]
        cfg_data = latest_cfg.get('data', {})
        print(f"\n  Latest HEALTH_MONITOR_CONFIG_LOADED:")
        print(f"    Enabled: {cfg_data.get('enabled', 'N/A')}")
        print(f"    Pushover Enabled: {cfg_data.get('pushover_enabled', 'N/A')}")
        print(f"    User Key Length: {cfg_data.get('pushover_user_key_length', 0)}")
        print(f"    App Token Length: {cfg_data.get('pushover_app_token_length', 0)}")
        
        if cfg_data.get('pushover_user_key_length', 0) > 0:
            print(f"    [OK] Pushover credentials configured")
        else:
            print(f"    [WARN] Pushover credentials missing (length 0)")
    
    if monitor_started:
        latest_mon = monitor_started[-1]
        mon_data = latest_mon.get('data', {})
        print(f"\n  Latest HEALTH_MONITOR_STARTED:")
        print(f"    Enabled: {mon_data.get('enabled', 'N/A')}")
        print(f"    Pushover Configured: {mon_data.get('pushover_configured', 'N/A')}")
        
        if mon_data.get('pushover_configured'):
            print(f"    [OK] Notification service ready")
        else:
            print(f"    [WARN] Notification service not configured")
    
    # Critical events
    critical = [e for e in events_since_start 
               if e.get('event') == 'CRITICAL_EVENT_REPORTED']
    notifications = [e for e in events_since_start 
                   if e.get('event') == 'PUSHOVER_NOTIFY_ENQUEUED']
    
    print(f"\n[NOTIFICATIONS]")
    print(f"  Critical Events Reported: {len(critical)}")
    print(f"  Notifications Enqueued: {len(notifications)}")
    
    if critical:
        print(f"\n  Recent Critical Events:")
        for e in critical[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {data.get('event_type', 'N/A')}")
    
    if notifications:
        print(f"\n  Recent Notifications:")
        for e in notifications[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {data.get('title', 'N/A')}")
    
    # Errors
    errors = [e for e in events_since_start if e.get('level') == 'ERROR']
    print(f"\n[ERRORS]")
    print(f"  Total ERROR events: {len(errors)}")
    if errors:
        error_types = Counter([e.get('event', 'UNKNOWN') for e in errors])
        print(f"  Error breakdown:")
        for err_type, count in error_types.most_common(5):
            print(f"    {err_type}: {count}")
        
        print(f"\n  Recent errors:")
        for e in errors[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {e.get('event', 'N/A')}")
    
    # Run ID coverage
    events_with_run_id = sum(1 for e in events_since_start if e.get('run_id'))
    total_events = len(events_since_start)
    run_id_pct = (events_with_run_id / total_events * 100) if total_events > 0 else 0
    
    print(f"\n[RUN ID STATUS]")
    print(f"  Events with run_id: {events_with_run_id}/{total_events} ({run_id_pct:.1f}%)")
    if run_id_pct == 100:
        print(f"  [OK] All events have run_id")
    else:
        print(f"  [WARN] Some events missing run_id")
    
    # Latest events
    print(f"\n[LATEST 10 EVENTS]")
    for e in events_since_start[-10:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        level = e.get('level', '')
        event_type = e.get('event', '')
        inst = e.get('instrument', '')
        print(f"  {ts.strftime('%H:%M:%S UTC') if ts else 'N/A':19} | {level:5} | {inst:4} | {event_type}")
    
    print("\n" + "="*80)
    print("ASSESSMENT SUMMARY")
    print("="*80)
    
    # Overall status
    issues = []
    if len(errors) > 0:
        issues.append(f"{len(errors)} ERROR events")
    if not notifications and critical:
        issues.append("Critical events reported but no notifications sent")
    if run_id_pct < 100:
        issues.append("Some events missing run_id")
    
    if not issues:
        print("\n[OK] System appears to be operating normally")
        print("  - No errors detected")
        print("  - Streams are initializing")
        print("  - Health monitor is active")
    else:
        print(f"\n[ISSUES DETECTED]")
        for issue in issues:
            print(f"  - {issue}")
    
    if stream_events:
        print(f"\n[OK] Stream activity detected - {len(stream_events)} stream events")
    
    if monitor_started:
        mon_data = monitor_started[-1].get('data', {})
        if mon_data.get('pushover_configured'):
            print(f"[OK] Pushover notifications configured and ready")

if __name__ == '__main__':
    main()
