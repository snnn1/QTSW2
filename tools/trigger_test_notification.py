"""Trigger a test Pushover notification via the robot's trigger file mechanism"""
import json
from pathlib import Path
from datetime import datetime
import time

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    qtsw2_root = Path(__file__).parent.parent
    trigger_file = qtsw2_root / "data" / "test_notification_trigger.txt"
    log_file = qtsw2_root / "logs" / "robot" / "robot_ENGINE.jsonl"
    
    print("="*80)
    print("TRIGGER TEST NOTIFICATION")
    print("="*80)
    
    # Create trigger file
    print(f"\n[1] Creating trigger file: {trigger_file}")
    trigger_file.parent.mkdir(parents=True, exist_ok=True)
    
    with open(trigger_file, 'w') as f:
        f.write(f"Test notification triggered at {datetime.utcnow().isoformat()}Z\n")
    
    print(f"  [OK] Trigger file created")
    print(f"  The robot will check this file on its next Tick() call")
    print(f"  Waiting for robot to process trigger...\n")
    
    # Get initial event count
    initial_events = []
    if log_file.exists():
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        initial_events.append(json.loads(line))
                    except:
                        pass
    
    initial_test_count = len([e for e in initial_events if e.get('event') == 'TEST_NOTIFICATION_SENT'])
    
    print(f"[2] Monitoring logs for test notification...")
    print(f"  Initial test notification count: {initial_test_count}")
    print(f"  Waiting up to 30 seconds for notification...\n")
    
    # Wait and check for test notification
    max_wait = 30
    check_interval = 2
    waited = 0
    
    while waited < max_wait:
        time.sleep(check_interval)
        waited += check_interval
        
        if not log_file.exists():
            continue
        
        events = []
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
        
        test_events = [e for e in events if e.get('event') == 'TEST_NOTIFICATION_SENT']
        test_skipped = [e for e in events if e.get('event') == 'TEST_NOTIFICATION_SKIPPED']
        notify_enqueued = [e for e in events if e.get('event') == 'PUSHOVER_NOTIFY_ENQUEUED']
        
        current_test_count = len(test_events)
        
        if current_test_count > initial_test_count:
            print(f"  [SUCCESS] Test notification triggered!")
            latest_test = test_events[-1]
            ts = parse_timestamp(latest_test.get('ts_utc', ''))
            data = latest_test.get('data', {})
            
            print(f"\n[TEST NOTIFICATION DETAILS]")
            print(f"  Time: {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'}")
            print(f"  Run ID: {data.get('run_id', 'N/A')}")
            print(f"  Notification Key: {data.get('notification_key', 'N/A')}")
            print(f"  Title: {data.get('title', 'N/A')}")
            print(f"  Message: {data.get('message', 'N/A')[:100]}...")
            
            # Check if notification was enqueued
            if notify_enqueued:
                latest_notify = notify_enqueued[-1]
                notify_ts = parse_timestamp(latest_notify.get('ts_utc', ''))
                notify_data = latest_notify.get('data', {})
                
                print(f"\n[PUSHOVER NOTIFICATION ENQUEUED]")
                print(f"  Time: {notify_ts.strftime('%Y-%m-%d %H:%M:%S UTC') if notify_ts else 'N/A'}")
                print(f"  Title: {notify_data.get('title', 'N/A')}")
                print(f"  Priority: {notify_data.get('priority', 'N/A')}")
                print(f"  [OK] Notification was enqueued - check your Pushover app!")
            else:
                print(f"\n[WARNING] Test notification sent but PUSHOVER_NOTIFY_ENQUEUED not found")
                print(f"  This may indicate the notification service is not running properly")
            
            # Clean up trigger file
            if trigger_file.exists():
                trigger_file.unlink()
                print(f"\n[3] Cleaned up trigger file")
            
            return
        
        if test_skipped and len(test_skipped) > len([e for e in initial_events if e.get('event') == 'TEST_NOTIFICATION_SKIPPED']):
            latest_skipped = test_skipped[-1]
            ts = parse_timestamp(latest_skipped.get('ts_utc', ''))
            data = latest_skipped.get('data', {})
            
            print(f"  [SKIPPED] Test notification was skipped")
            print(f"  Time: {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}")
            print(f"  Reason: {data.get('note', 'N/A')}")
            
            # Clean up trigger file
            if trigger_file.exists():
                trigger_file.unlink()
            
            return
        
        if waited % 5 == 0:
            print(f"  Still waiting... ({waited}s)")
    
    print(f"\n[TIMEOUT] No test notification detected after {max_wait} seconds")
    print(f"  Possible reasons:")
    print(f"    - Robot engine is not running")
    print(f"    - Robot is not processing Tick() calls")
    print(f"    - Health monitor is disabled")
    print(f"    - Notification service is not configured")
    
    # Clean up trigger file
    if trigger_file.exists():
        trigger_file.unlink()
        print(f"\n[3] Cleaned up trigger file")

if __name__ == '__main__':
    main()
