"""Check all robot log files (current + rotated)"""
import json
from pathlib import Path
from datetime import datetime
from collections import defaultdict

qtsw2_root = Path(__file__).parent.parent
log_dir = qtsw2_root / "logs" / "robot"

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def read_log_file(filepath):
    """Read all events from a log file"""
    events = []
    try:
        with open(filepath, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except Exception as e:
        print(f"Error reading {filepath}: {e}")
    return events

def main():
    print("="*80)
    print("CHECKING ALL ROBOT LOG FILES")
    print("="*80)
    
    # Find all ENGINE log files
    current_log = log_dir / "robot_ENGINE.jsonl"
    rotated_logs = sorted(log_dir.glob("robot_ENGINE_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
    
    print(f"\n[LOG FILES FOUND]")
    print(f"  Current: {current_log.name} ({current_log.stat().st_size/1024:.1f} KB)")
    if rotated_logs:
        print(f"  Rotated files: {len(rotated_logs)}")
        for rlog in rotated_logs[:3]:
            print(f"    {rlog.name} ({rlog.stat().st_size/1024:.1f} KB)")
    
    # Read current log
    current_events = read_log_file(current_log)
    print(f"\n[CURRENT LOG]")
    print(f"  Events: {len(current_events)}")
    if current_events:
        first_ts = parse_timestamp(current_events[0].get('ts_utc', ''))
        last_ts = parse_timestamp(current_events[-1].get('ts_utc', ''))
        print(f"  First: {first_ts.strftime('%Y-%m-%d %H:%M:%S UTC') if first_ts else 'N/A'}")
        print(f"  Last:  {last_ts.strftime('%Y-%m-%d %H:%M:%S UTC') if last_ts else 'N/A'}")
    
    # Read most recent rotated log
    all_events = current_events.copy()
    if rotated_logs:
        rotated_events = read_log_file(rotated_logs[0])
        print(f"\n[MOST RECENT ROTATED LOG: {rotated_logs[0].name}]")
        print(f"  Events: {len(rotated_events)}")
        if rotated_events:
            first_ts = parse_timestamp(rotated_events[0].get('ts_utc', ''))
            last_ts = parse_timestamp(rotated_events[-1].get('ts_utc', ''))
            print(f"  First: {first_ts.strftime('%Y-%m-%d %H:%M:%S UTC') if first_ts else 'N/A'}")
            print(f"  Last:  {last_ts.strftime('%Y-%m-%d %H:%M:%S UTC') if last_ts else 'N/A'}")
        
        # Combine: rotated events + current events
        all_events = rotated_events + current_events
    
    # Sort all events by timestamp
    all_events.sort(key=lambda e: parse_timestamp(e.get('ts_utc', '')) or datetime.min)
    
    print(f"\n[COMBINED LOGS]")
    print(f"  Total events: {len(all_events)}")
    if all_events:
        first_ts = parse_timestamp(all_events[0].get('ts_utc', ''))
        last_ts = parse_timestamp(all_events[-1].get('ts_utc', ''))
        print(f"  Time range: {first_ts.strftime('%Y-%m-%d %H:%M:%S UTC') if first_ts else 'N/A'} to {last_ts.strftime('%Y-%m-%d %H:%M:%S UTC') if last_ts else 'N/A'}")
    
    # Get last 2000 events
    recent = all_events[-2000:]
    
    # Find health monitor events
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
    engine_starts = [e for e in recent if e.get('event') == 'ENGINE_START']
    
    print(f"  ENGINE_START: {len(engine_starts)}")
    print(f"  HEALTH_MONITOR_CONFIG_LOADED: {len(config_loaded)}")
    print(f"  HEALTH_MONITOR_STARTED: {len(monitor_started)}")
    print(f"  PUSHOVER_CONFIG_MISSING: {len(pushover_missing)}")
    print(f"  CRITICAL_EVENT_REPORTED: {len(critical_reported)}")
    print(f"  PUSHOVER_NOTIFY_ENQUEUED: {len(notifications_sent)}")
    
    if engine_starts:
        print(f"\n[LATEST ENGINE_START]")
        latest_start = engine_starts[-1]
        ts = parse_timestamp(latest_start.get('ts_utc', ''))
        print(f"  Time: {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'}")
        print(f"  Run ID: {latest_start.get('run_id', 'N/A')[:32]}...")
    
    if config_loaded:
        latest_cfg = config_loaded[-1].get('data', {})
        print(f"\n[LATEST CONFIG STATUS]")
        print(f"  Enabled: {latest_cfg.get('enabled', 'N/A')}")
        print(f"  Pushover Enabled: {latest_cfg.get('pushover_enabled', 'N/A')}")
        print(f"  User Key Length: {latest_cfg.get('pushover_user_key_length', 0)}")
        print(f"  App Token Length: {latest_cfg.get('pushover_app_token_length', 0)}")
        
        if latest_cfg.get('pushover_user_key_length', 0) > 0 and latest_cfg.get('pushover_app_token_length', 0) > 0:
            print(f"  [OK] Credentials loaded successfully")
        else:
            print(f"  [ERROR] Credentials missing!")
    
    if monitor_started:
        latest_mon = monitor_started[-1].get('data', {})
        print(f"\n[LATEST MONITOR STATUS]")
        print(f"  Enabled: {latest_mon.get('enabled', 'N/A')}")
        print(f"  Pushover Configured: {latest_mon.get('pushover_configured', 'N/A')}")
        
        if latest_mon.get('pushover_configured'):
            print(f"  [OK] Notification service ready")
        else:
            print(f"  [ERROR] Notification service not configured")

if __name__ == '__main__':
    main()
