"""Check health monitor and notification setup from logs"""
import json
from pathlib import Path
from datetime import datetime

qtsw2_root = Path(__file__).parent.parent
log_file = qtsw2_root / "logs" / "robot" / "robot_ENGINE.jsonl"

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    print("="*80)
    print("HEALTH MONITOR & NOTIFICATION STATUS CHECK")
    print("="*80)
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass
    
    # Get last 1000 events
    recent = events[-1000:]
    
    # Find health monitor related events
    health_events = []
    for e in recent:
        event_name = e.get('event', '') or e.get('event_type', '')
        if any(x in event_name.upper() for x in ['HEALTH', 'PUSHOVER', 'CRITICAL', 'NOTIFICATION', 'ENGINE_START']):
            health_events.append(e)
    
    print(f"\n[HEALTH MONITOR EVENTS - Last {len(health_events)} relevant events]\n")
    
    for e in health_events[-30:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        ts_str = ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else e.get('ts_utc', '')[:19]
        event_name = e.get('event', '') or e.get('event_type', 'N/A')
        run_id = str(e.get('run_id', ''))[:16] + '...' if e.get('run_id') else 'N/A'
        level = e.get('level', 'N/A')
        data = e.get('data', {})
        
        print(f"{ts_str} | {event_name:45} | run_id: {run_id:20} | {level}")
        
        if event_name == 'HEALTH_MONITOR_CONFIG_LOADED':
            print(f"    Enabled: {data.get('enabled', 'N/A')}")
            print(f"    Pushover Enabled: {data.get('pushover_enabled', 'N/A')}")
            print(f"    User Key Length: {data.get('pushover_user_key_length', 0)}")
            print(f"    App Token Length: {data.get('pushover_app_token_length', 0)}")
        elif event_name == 'HEALTH_MONITOR_STARTED':
            print(f"    Enabled: {data.get('enabled', 'N/A')}")
            print(f"    Pushover Configured: {data.get('pushover_configured', 'N/A')}")
        elif event_name == 'CRITICAL_EVENT_REPORTED':
            print(f"    Event Type: {data.get('event_type', 'N/A')}")
            print(f"    Run ID: {data.get('run_id', 'N/A')}")
            print(f"    Dedupe Key: {data.get('dedupe_key', 'N/A')}")
        elif event_name == 'PUSHOVER_NOTIFY_ENQUEUED':
            print(f"    Title: {data.get('title', 'N/A')}")
            print(f"    Priority: {data.get('priority', 'N/A')}")
            print(f"    Skip Rate Limit: {data.get('skip_rate_limit', 'N/A')}")
    
    # Summary
    print(f"\n[SUMMARY]")
    config_loaded = [e for e in recent if e.get('event') == 'HEALTH_MONITOR_CONFIG_LOADED']
    monitor_started = [e for e in recent if e.get('event') == 'HEALTH_MONITOR_STARTED']
    pushover_missing = [e for e in recent if e.get('event') == 'PUSHOVER_CONFIG_MISSING']
    critical_reported = [e for e in recent if e.get('event') == 'CRITICAL_EVENT_REPORTED']
    notifications_sent = [e for e in recent if e.get('event') == 'PUSHOVER_NOTIFY_ENQUEUED']
    
    print(f"  HEALTH_MONITOR_CONFIG_LOADED: {len(config_loaded)}")
    print(f"  HEALTH_MONITOR_STARTED: {len(monitor_started)}")
    print(f"  PUSHOVER_CONFIG_MISSING: {len(pushover_missing)}")
    print(f"  CRITICAL_EVENT_REPORTED: {len(critical_reported)}")
    print(f"  PUSHOVER_NOTIFY_ENQUEUED: {len(notifications_sent)}")
    
    if config_loaded:
        latest_cfg = config_loaded[-1].get('data', {})
        print(f"\n[LATEST CONFIG STATUS]")
        print(f"  Enabled: {latest_cfg.get('enabled', 'N/A')}")
        print(f"  Pushover Enabled: {latest_cfg.get('pushover_enabled', 'N/A')}")
        print(f"  User Key Length: {latest_cfg.get('pushover_user_key_length', 0)}")
        print(f"  App Token Length: {latest_cfg.get('pushover_app_token_length', 0)}")
        
        if latest_cfg.get('pushover_user_key_length', 0) > 0 and latest_cfg.get('pushover_app_token_length', 0) > 0:
            print(f"  ✓ Credentials loaded successfully")
        else:
            print(f"  ✗ Credentials missing!")
    
    if monitor_started:
        latest_mon = monitor_started[-1].get('data', {})
        print(f"\n[LATEST MONITOR STATUS]")
        print(f"  Enabled: {latest_mon.get('enabled', 'N/A')}")
        print(f"  Pushover Configured: {latest_mon.get('pushover_configured', 'N/A')}")
        
        if latest_mon.get('pushover_configured'):
            print(f"  ✓ Notification service ready")
        else:
            print(f"  ✗ Notification service not configured")
    
    # Check run_id presence
    run_ids = set(e.get('run_id') for e in recent if e.get('run_id'))
    print(f"\n[RUN ID STATUS]")
    print(f"  Unique run_ids in recent logs: {len(run_ids)}")
    if run_ids:
        print(f"  Latest run_id: {sorted(run_ids)[-1][:32]}...")
        events_with_run_id = sum(1 for e in recent if e.get('run_id'))
        print(f"  Events with run_id: {events_with_run_id}/{len(recent)} ({100*events_with_run_id/len(recent):.1f}%)")

if __name__ == '__main__':
    main()
