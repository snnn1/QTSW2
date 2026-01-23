"""Test notification verification script"""
import json
from pathlib import Path
from datetime import datetime, timedelta

qtsw2_root = Path(__file__).parent.parent
log_dir = qtsw2_root / "logs" / "robot"

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def read_all_engine_logs():
    """Read all ENGINE log files (current + rotated)"""
    current_log = log_dir / "robot_ENGINE.jsonl"
    rotated_logs = sorted(log_dir.glob("robot_ENGINE_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
    
    all_events = []
    
    # Read rotated logs first (oldest to newest)
    for rlog in reversed(rotated_logs):
        try:
            with open(rlog, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            all_events.append(json.loads(line))
                        except:
                            pass
        except:
            pass
    
    # Read current log
    if current_log.exists():
        try:
            with open(current_log, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            all_events.append(json.loads(line))
                        except:
                            pass
        except:
            pass
    
    # Sort by timestamp
    all_events.sort(key=lambda e: parse_timestamp(e.get('ts_utc', '')) or datetime.min)
    return all_events

def main():
    print("="*80)
    print("TEST NOTIFICATION VERIFICATION")
    print("="*80)
    
    print("\n[INSTRUCTIONS]")
    print("To send a test notification:")
    print("  1. In NinjaTrader, access your RobotSimStrategy instance")
    print("  2. Call: strategy.SendTestNotification()")
    print("  3. Or wait for this script to check logs after you trigger it")
    print("\nThis script will check for:")
    print("  - TEST_NOTIFICATION_SENT event")
    print("  - PUSHOVER_NOTIFY_ENQUEUED event")
    print("  - PUSHOVER_ENDPOINT event (if available)")
    
    print("\n" + "="*80)
    print("CHECKING LOGS...")
    print("="*80)
    
    events = read_all_engine_logs()
    
    # Get last 1000 events
    recent = events[-1000:] if len(events) > 1000 else events
    
    # Find test notification events
    test_sent = [e for e in recent if e.get('event') == 'TEST_NOTIFICATION_SENT']
    test_skipped = [e for e in recent if e.get('event') == 'TEST_NOTIFICATION_SKIPPED']
    notify_enqueued = [e for e in recent if e.get('event') == 'PUSHOVER_NOTIFY_ENQUEUED']
    pushover_endpoint = [e for e in recent if e.get('event') == 'PUSHOVER_ENDPOINT']
    
    print(f"\n[TEST NOTIFICATION EVENTS]")
    print(f"  TEST_NOTIFICATION_SENT: {len(test_sent)}")
    print(f"  TEST_NOTIFICATION_SKIPPED: {len(test_skipped)}")
    print(f"  PUSHOVER_NOTIFY_ENQUEUED: {len(notify_enqueued)}")
    print(f"  PUSHOVER_ENDPOINT: {len(pushover_endpoint)}")
    
    if test_sent:
        print(f"\n[LATEST TEST NOTIFICATION SENT]")
        latest = test_sent[-1]
        ts = parse_timestamp(latest.get('ts_utc', ''))
        data = latest.get('data', {})
        payload_str = data.get('payload', '') if isinstance(data.get('payload'), str) else ''
        
        print(f"  Time: {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'}")
        print(f"  Run ID: {latest.get('run_id', 'N/A')[:32]}...")
        print(f"  Notification Key: {data.get('notification_key', 'N/A')}")
        print(f"  Title: {data.get('title', 'N/A')}")
        print(f"  Priority: {data.get('priority', 'N/A')}")
        
        # Check if corresponding PUSHOVER_NOTIFY_ENQUEUED exists
        if notify_enqueued:
            # Find notifications around the same time (within 5 seconds)
            test_time = ts if ts else datetime.min
            matching_notifications = [
                e for e in notify_enqueued
                if abs((parse_timestamp(e.get('ts_utc', '')) or datetime.min) - test_time).total_seconds() < 5
            ]
            
            if matching_notifications:
                print(f"\n  [OK] Found matching PUSHOVER_NOTIFY_ENQUEUED event!")
                notif = matching_notifications[-1]
                notif_data = notif.get('data', {})
                print(f"    Time: {parse_timestamp(notif.get('ts_utc', '')).strftime('%Y-%m-%d %H:%M:%S UTC') if parse_timestamp(notif.get('ts_utc', '')) else 'N/A'}")
                print(f"    Title: {notif_data.get('title', 'N/A')}")
                print(f"    Priority: {notif_data.get('priority', 'N/A')}")
                print(f"    Skip Rate Limit: {notif_data.get('skip_rate_limit', 'N/A')}")
            else:
                print(f"\n  [WARN] No matching PUSHOVER_NOTIFY_ENQUEUED found within 5 seconds")
                print(f"    Check notification_errors.log for send failures")
        else:
            print(f"\n  [WARN] No PUSHOVER_NOTIFY_ENQUEUED events found")
    
    if test_skipped:
        print(f"\n[TEST NOTIFICATION SKIPPED]")
        latest_skipped = test_skipped[-1]
        ts = parse_timestamp(latest_skipped.get('ts_utc', ''))
        data = latest_skipped.get('data', {})
        print(f"  Time: {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'}")
        print(f"  Reason: {data.get('reason', 'N/A')}")
        print(f"  Note: {data.get('note', 'N/A')}")
    
    if not test_sent and not test_skipped:
        print(f"\n[INFO] No test notification events found yet")
        print(f"  Trigger a test notification and run this script again")
    
    # Check for any recent PUSHOVER_ENDPOINT events
    if pushover_endpoint:
        print(f"\n[PUSHOVER ENDPOINT EVENTS]")
        for e in pushover_endpoint[-3:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            print(f"  {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'}")
            print(f"    Endpoint: {data.get('endpoint', 'N/A')}")
            print(f"    Status: {data.get('status_code', 'N/A')}")
            print(f"    Success: {data.get('success', 'N/A')}")

if __name__ == '__main__':
    main()
