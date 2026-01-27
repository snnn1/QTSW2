"""Check notification status after restart"""
import json
from pathlib import Path
from datetime import datetime

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    log_dir = Path("logs/robot")
    log_file = sorted(log_dir.glob('robot_ENGINE*.jsonl'), key=lambda p: p.stat().st_mtime, reverse=True)[0]
    
    print("="*80)
    print("NOTIFICATION STATUS CHECK")
    print("="*80)
    print(f"\nReading from: {log_file.name}\n")
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass
    
    # Get events from latest engine start
    starts = [e for e in events if e.get('event') == 'ENGINE_START']
    if not starts:
        print("[ERROR] No ENGINE_START found")
        return
    
    latest_start = starts[-1]
    start_time = parse_timestamp(latest_start.get('ts_utc', ''))
    run_id = latest_start.get('run_id', '')
    
    print(f"[LATEST ENGINE START]")
    print(f"  Time: {start_time.strftime('%Y-%m-%d %H:%M:%S UTC') if start_time else 'N/A'}")
    print(f"  Run ID: {run_id[:16]}...\n")
    
    # Get events since latest start
    events_since_start = [e for e in events 
                          if parse_timestamp(e.get('ts_utc', '')) and 
                          parse_timestamp(e.get('ts_utc', '')) >= start_time]
    
    # Check health monitor config
    config_loaded = [e for e in events_since_start if e.get('event') == 'HEALTH_MONITOR_CONFIG_LOADED']
    monitor_started = [e for e in events_since_start if e.get('event') == 'HEALTH_MONITOR_STARTED']
    critical_reported = [e for e in events_since_start if e.get('event') == 'CRITICAL_EVENT_REPORTED']
    notifications = [e for e in events_since_start if e.get('event') == 'PUSHOVER_NOTIFY_ENQUEUED']
    notify_skipped = [e for e in events_since_start if e.get('event') == 'CRITICAL_NOTIFICATION_SKIPPED']
    notify_rejected = [e for e in events_since_start if e.get('event') == 'CRITICAL_NOTIFICATION_REJECTED']
    
    print(f"[HEALTH MONITOR CONFIG]")
    if config_loaded:
        latest_cfg = config_loaded[-1]
        cfg_data = latest_cfg.get('data', {})
        print(f"  User Key Length: {cfg_data.get('pushover_user_key_length', 0)}")
        print(f"  App Token Length: {cfg_data.get('pushover_app_token_length', 0)}")
        print(f"  Note: This logs BEFORE secrets merge, so may show 0 even if secrets are loaded")
    
    if monitor_started:
        latest_mon = monitor_started[-1]
        mon_data = latest_mon.get('data', {})
        print(f"\n[HEALTH MONITOR STATUS]")
        print(f"  Enabled: {mon_data.get('enabled', 'N/A')}")
        print(f"  Pushover Configured: {mon_data.get('pushover_configured', 'N/A')}")
        
        if mon_data.get('pushover_configured'):
            print(f"  [OK] Pushover is configured and ready")
        else:
            print(f"  [WARN] Pushover not configured")
    
    print(f"\n[CRITICAL EVENTS & NOTIFICATIONS]")
    print(f"  Critical Events Reported: {len(critical_reported)}")
    print(f"  Notifications Enqueued: {len(notifications)}")
    print(f"  Notifications Skipped: {len(notify_skipped)}")
    print(f"  Notifications Rejected: {len(notify_rejected)}")
    
    if critical_reported:
        print(f"\n  Recent Critical Events:")
        for e in critical_reported[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            event_type = data.get('event_type', 'N/A')
            dedupe_key = data.get('dedupe_key', 'N/A')
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {event_type}")
            print(f"      Dedupe Key: {dedupe_key}")
    
    if notifications:
        print(f"\n  Recent Notifications:")
        for e in notifications[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {data.get('title', 'N/A')}")
    
    if notify_skipped:
        print(f"\n  Skipped Notifications:")
        for e in notify_skipped[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            reason = data.get('reason', 'N/A')
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {reason}")
    
    if notify_rejected:
        print(f"\n  Rejected Notifications:")
        for e in notify_rejected[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            reason = data.get('reason', 'N/A')
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {reason}")
    
    # Analysis
    print(f"\n[ANALYSIS]")
    if mon_data.get('pushover_configured') and critical_reported and not notifications:
        print(f"  [ISSUE] Pushover is configured but no notifications sent for {len(critical_reported)} critical events")
        print(f"  Possible reasons:")
        print(f"    - Rate limiting (min_notify_interval_seconds)")
        print(f"    - Deduplication (same event type + run_id already notified)")
        print(f"    - Notification service error (check for PUSHOVER_ERROR events)")
    elif mon_data.get('pushover_configured') and notifications:
        print(f"  [OK] Notifications are working - {len(notifications)} sent")
    elif not mon_data.get('pushover_configured'):
        print(f"  [WARN] Pushover not configured - notifications cannot be sent")

if __name__ == '__main__':
    main()
