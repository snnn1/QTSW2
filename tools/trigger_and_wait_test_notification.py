"""Trigger test notification and wait for it to appear in logs"""
import json
import time
from pathlib import Path
from datetime import datetime

qtsw2_root = Path(__file__).parent.parent
trigger_file = qtsw2_root / "data" / "test_notification_trigger.txt"
log_file = qtsw2_root / "logs" / "robot" / "robot_ENGINE.jsonl"

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def get_latest_events(count=50):
    """Get latest events from log"""
    events = []
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
    return events[-count:] if len(events) > count else events

def main():
    print("="*80)
    print("TRIGGERING TEST NOTIFICATION")
    print("="*80)
    
    # Ensure data directory exists
    trigger_file.parent.mkdir(parents=True, exist_ok=True)
    
    # Get baseline - latest event timestamp
    baseline_events = get_latest_events(10)
    baseline_time = None
    if baseline_events:
        baseline_time = parse_timestamp(baseline_events[-1].get('ts_utc', ''))
        print(f"\n[BASELINE]")
        print(f"  Latest event time: {baseline_time.strftime('%Y-%m-%d %H:%M:%S UTC') if baseline_time else 'N/A'}")
    
    # Create trigger file
    print(f"\n[CREATING TRIGGER FILE]")
    print(f"  Path: {trigger_file}")
    try:
        trigger_file.write_text("test")
        print(f"  [OK] Trigger file created")
    except Exception as e:
        print(f"  [ERROR] Failed to create trigger file: {e}")
        return
    
    # Wait for robot to process (Tick runs every 1 second)
    print(f"\n[WAITING FOR PROCESSING]")
    print(f"  Robot Tick() runs every 1 second")
    print(f"  Waiting up to 10 seconds for notification...")
    
    max_wait = 10
    check_interval = 1
    waited = 0
    
    while waited < max_wait:
        time.sleep(check_interval)
        waited += check_interval
        
        # Check if trigger file was deleted (robot processed it)
        if not trigger_file.exists():
            print(f"  [OK] Trigger file deleted - robot processed it!")
            break
        
        # Check for new events
        current_events = get_latest_events(50)
        test_events = [e for e in current_events if e.get('event') in ['TEST_NOTIFICATION_SENT', 'TEST_NOTIFICATION_SKIPPED', 'TEST_NOTIFICATION_TRIGGER_ERROR']]
        
        if test_events:
            print(f"  [OK] Found test notification event!")
            break
        
        print(f"  Waiting... ({waited}s)")
    
    # Check results
    print(f"\n[CHECKING RESULTS]")
    
    # Check if trigger file still exists
    if trigger_file.exists():
        print(f"  [WARN] Trigger file still exists - robot may not have processed it yet")
        print(f"    Possible reasons:")
        print(f"      - Robot hasn't restarted with new code")
        print(f"      - Tick() method not running")
        print(f"      - File path mismatch")
    else:
        print(f"  [OK] Trigger file was deleted")
    
    # Check for test notification events
    all_events = get_latest_events(100)
    test_sent = [e for e in all_events if e.get('event') == 'TEST_NOTIFICATION_SENT']
    test_skipped = [e for e in all_events if e.get('event') == 'TEST_NOTIFICATION_SKIPPED']
    test_error = [e for e in all_events if e.get('event') == 'TEST_NOTIFICATION_TRIGGER_ERROR']
    notify_enqueued = [e for e in all_events if e.get('event') == 'PUSHOVER_NOTIFY_ENQUEUED']
    
    print(f"\n[EVENTS FOUND]")
    print(f"  TEST_NOTIFICATION_SENT: {len(test_sent)}")
    print(f"  TEST_NOTIFICATION_SKIPPED: {len(test_skipped)}")
    print(f"  TEST_NOTIFICATION_TRIGGER_ERROR: {len(test_error)}")
    print(f"  PUSHOVER_NOTIFY_ENQUEUED: {len(notify_enqueued)}")
    
    if test_sent:
        latest = test_sent[-1]
        ts = parse_timestamp(latest.get('ts_utc', ''))
        data = latest.get('data', {})
        print(f"\n[SUCCESS] Test notification sent!")
        print(f"  Time: {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'}")
        print(f"  Run ID: {latest.get('run_id', 'N/A')[:32]}...")
        print(f"  Title: {data.get('title', 'N/A')}")
        print(f"  Priority: {data.get('priority', 'N/A')}")
        
        # Check for matching PUSHOVER_NOTIFY_ENQUEUED
        if notify_enqueued:
            test_time = ts if ts else datetime.min
            matching = [
                e for e in notify_enqueued
                if abs((parse_timestamp(e.get('ts_utc', '')) or datetime.min) - test_time).total_seconds() < 5
            ]
            if matching:
                print(f"\n  [OK] Found matching PUSHOVER_NOTIFY_ENQUEUED!")
                print(f"    Check your phone for the notification!")
            else:
                print(f"\n  [WARN] No matching PUSHOVER_NOTIFY_ENQUEUED found")
                print(f"    Check notification_errors.log for send failures")
    
    elif test_skipped:
        latest = test_skipped[-1]
        ts = parse_timestamp(latest.get('ts_utc', ''))
        data = latest.get('data', {})
        print(f"\n[SKIPPED] Test notification was skipped")
        print(f"  Reason: {data.get('reason', 'N/A')}")
        print(f"  Note: {data.get('note', 'N/A')}")
    
    elif test_error:
        latest = test_error[-1]
        ts = parse_timestamp(latest.get('ts_utc', ''))
        data = latest.get('data', {})
        print(f"\n[ERROR] Trigger processing failed")
        print(f"  Error: {data.get('error', 'N/A')}")
    
    else:
        print(f"\n[INFO] No test notification events found yet")
        print(f"  The robot may need to restart to pick up the new code")
        print(f"  Or wait a bit longer and run: python tools/test_notification.py")

if __name__ == '__main__':
    main()
