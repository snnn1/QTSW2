"""Check latest health monitor status with full details"""
import json
from pathlib import Path
from datetime import datetime

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

# Check both logs
current_log = Path('logs/robot/robot_ENGINE.jsonl')
rotated_log = Path('logs/robot/robot_ENGINE_20260123_021621.jsonl')

all_events = []
for log_file in [rotated_log, current_log]:
    if log_file.exists():
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        all_events.append(json.loads(line))
                    except:
                        pass

# Sort by timestamp
all_events.sort(key=lambda e: parse_timestamp(e.get('ts_utc', '')) or datetime.min)

# Get health monitor events
health_events = []
for e in all_events:
    event_name = e.get('event', '')
    if any(x in event_name.upper() for x in ['HEALTH', 'PUSHOVER', 'CRITICAL', 'NOTIFICATION']):
        health_events.append(e)

print("="*80)
print("LATEST HEALTH MONITOR STATUS")
print("="*80)

if health_events:
    latest_config = None
    latest_started = None
    
    for e in reversed(health_events):
        if e.get('event') == 'HEALTH_MONITOR_CONFIG_LOADED' and not latest_config:
            latest_config = e
        if e.get('event') == 'HEALTH_MONITOR_STARTED' and not latest_started:
            latest_started = e
    
    if latest_config:
        ts = parse_timestamp(latest_config.get('ts_utc', ''))
        data = latest_config.get('data', {})
        run_id = latest_config.get('run_id', 'N/A')
        
        print(f"\n[LATEST HEALTH_MONITOR_CONFIG_LOADED]")
        print(f"  Time: {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'}")
        print(f"  Run ID: {run_id[:32]}...")
        print(f"  Enabled: {data.get('enabled', 'N/A')}")
        print(f"  Pushover Enabled: {data.get('pushover_enabled', 'N/A')}")
        print(f"  User Key Length: {data.get('pushover_user_key_length', 0)}")
        print(f"  App Token Length: {data.get('pushover_app_token_length', 0)}")
        
        if data.get('pushover_user_key_length', 0) > 0 and data.get('pushover_app_token_length', 0) > 0:
            print(f"  [OK] Credentials loaded successfully!")
        else:
            print(f"  [ERROR] Credentials missing!")
    
    if latest_started:
        ts = parse_timestamp(latest_started.get('ts_utc', ''))
        data = latest_started.get('data', {})
        run_id = latest_started.get('run_id', 'N/A')
        
        print(f"\n[LATEST HEALTH_MONITOR_STARTED]")
        print(f"  Time: {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'}")
        print(f"  Run ID: {run_id[:32]}...")
        print(f"  Enabled: {data.get('enabled', 'N/A')}")
        print(f"  Pushover Configured: {data.get('pushover_configured', 'N/A')}")
        print(f"  Data Stall Seconds: {data.get('data_stall_seconds', 'N/A')}")
        print(f"  Min Notify Interval: {data.get('min_notify_interval_seconds', 'N/A')}")
        
        if data.get('pushover_configured'):
            print(f"  [OK] Notification service configured and started!")
            print(f"  [OK] Ready to send critical notifications!")
        else:
            print(f"  [ERROR] Notification service not configured!")
    
    # Check for critical events
    critical_events = [e for e in health_events if e.get('event') == 'CRITICAL_EVENT_REPORTED']
    notifications = [e for e in health_events if e.get('event') == 'PUSHOVER_NOTIFY_ENQUEUED']
    
    print(f"\n[NOTIFICATION ACTIVITY]")
    print(f"  Critical Events Reported: {len(critical_events)}")
    print(f"  Notifications Enqueued: {len(notifications)}")
    
    if critical_events:
        print(f"\n  Recent Critical Events:")
        for e in critical_events[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            print(f"    {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'}")
            print(f"      Event Type: {data.get('event_type', 'N/A')}")
            print(f"      Run ID: {data.get('run_id', 'N/A')[:32]}...")
            print(f"      Dedupe Key: {data.get('dedupe_key', 'N/A')}")
    
    if notifications:
        print(f"\n  Recent Notifications:")
        for e in notifications[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            print(f"    {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'}")
            print(f"      Title: {data.get('title', 'N/A')}")
            print(f"      Priority: {data.get('priority', 'N/A')}")
            print(f"      Skip Rate Limit: {data.get('skip_rate_limit', 'N/A')}")
else:
    print("\n[ERROR] No health monitor events found!")
