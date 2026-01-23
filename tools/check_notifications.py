#!/usr/bin/env python3
"""Check if notifications are working properly."""

import json
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict, Counter

def parse_timestamp(ts_str):
    """Parse ISO8601 timestamp."""
    try:
        ts_str = ts_str.replace('Z', '+00:00')
        if '.' in ts_str:
            ts_str = ts_str.split('.')[0] + '+00:00'
        return datetime.fromisoformat(ts_str)
    except:
        return None

def main():
    log_dir = Path("logs/robot")
    
    print("=" * 80)
    print("NOTIFICATION SYSTEM CHECK")
    print("=" * 80)
    print()
    
    # Collect all notification-related events
    notification_events = []
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        event_type = event.get('event', '')
                        if 'PUSHOVER' in event_type or 'NOTIFY' in event_type or 'NOTIFICATION' in event_type:
                            notification_events.append(event)
                    except:
                        continue
        except:
            continue
    
    print(f"Total notification events found: {len(notification_events)}")
    print()
    
    if notification_events:
        # Group by event type
        event_types = Counter([e.get('event', 'N/A') for e in notification_events])
        print("Notification event types:")
        for event_type, count in sorted(event_types.items()):
            print(f"  {event_type}: {count}")
        print()
        
        # Show recent notifications
        recent = sorted(notification_events, key=lambda x: x.get('ts_utc', ''))[-20:]
        print("Recent notification events (last 20):")
        for e in recent:
            ts = e.get('ts_utc', '')[:19]
            event_type = e.get('event', 'N/A')
            level = e.get('level', 'N/A')
            data = e.get('data', {})
            payload = data.get('payload', {})
            
            # Extract message/title if available
            message = payload.get('message', '') if isinstance(payload, dict) else str(payload)[:60]
            title = payload.get('title', '') if isinstance(payload, dict) else ''
            
            print(f"  {ts} | {level:5} | {event_type:35} | {title[:30] if title else 'N/A':30} | {str(message)[:40]}")
        print()
    else:
        print("[WARN] No notification events found in logs")
        print("This could mean:")
        print("  1. Notifications are not configured")
        print("  2. No critical events occurred that trigger notifications")
        print("  3. Notification service is not running")
        print()
    
    # Check for notification events on Jan 22 (during panic)
    jan22_notifications = [e for e in notification_events if e.get('ts_utc', '').startswith('2026-01-22')]
    
    print(f"Notifications on Jan 22 (panic day): {len(jan22_notifications)}")
    if jan22_notifications:
        print("\nNotification events during panic:")
        for e in jan22_notifications:
            ts = e.get('ts_utc', '')[:19]
            event_type = e.get('event', 'N/A')
            data = e.get('data', {})
            payload = data.get('payload', {})
            message = payload.get('message', '') if isinstance(payload, dict) else str(payload)[:80]
            print(f"  {ts} | {event_type:35} | {str(message)[:60]}")
        print()
    else:
        print("[WARN] No notifications sent during Jan 22 panic")
        print("This suggests notifications may not be working for critical events")
        print()
    
    # Check configuration
    print("=" * 80)
    print("CONFIGURATION CHECK")
    print("=" * 80)
    print()
    
    secrets_file = Path("configs/robot/health_monitor.secrets.json")
    if secrets_file.exists():
        try:
            with open(secrets_file, 'r', encoding='utf-8') as f:
                config = json.load(f)
                print("Health monitor secrets file found:")
                if 'pushover_user_key' in config:
                    user_key = config['pushover_user_key']
                    print(f"  pushover_user_key: {'*' * (len(user_key) - 4) + user_key[-4:] if len(user_key) > 4 else '***'}")
                else:
                    print("  pushover_user_key: [MISSING]")
                
                # Check for both pushover_app_token (correct) and pushover_api_token (legacy)
                if 'pushover_app_token' in config:
                    api_token = config['pushover_app_token']
                    print(f"  pushover_app_token: {'*' * (len(api_token) - 4) + api_token[-4:] if len(api_token) > 4 else '***'}")
                elif 'pushover_api_token' in config:
                    api_token = config['pushover_api_token']
                    print(f"  pushover_api_token: {'*' * (len(api_token) - 4) + api_token[-4:] if len(api_token) > 4 else '***'} [LEGACY NAME]")
                else:
                    print("  pushover_app_token: [MISSING]")
        except Exception as e:
            print(f"  [ERROR] Failed to read config: {e}")
    else:
        print("[WARN] health_monitor.secrets.json not found")
        print("  Location: configs/robot/health_monitor.secrets.json")
        print()
    
    # Check for critical events that should trigger notifications
    print("=" * 80)
    print("CRITICAL EVENTS THAT SHOULD TRIGGER NOTIFICATIONS")
    print("=" * 80)
    print()
    
    critical_keywords = ['EXECUTION_GATE_INVARIANT_VIOLATION', 'DISCONNECT_FAIL_CLOSED', 
                        'KILL_SWITCH', 'DATA_LOSS_DETECTED', 'CONNECTION_LOST_SUSTAINED',
                        'RANGE_COMPUTE_FAILED', 'ORDER_REJECTED', 'PROTECTIVE_ORDER_FAILED']
    
    # Check today's critical events
    today = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    today_events = []
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        ts_str = event.get('ts_utc', '')
                        if ts_str.startswith(today):
                            event_type = event.get('event', '')
                            if any(kw in event_type for kw in critical_keywords):
                                today_events.append(event)
                    except:
                        continue
        except:
            continue
    
    print(f"Critical events today ({today}): {len(today_events)}")
    if today_events:
        critical_types = Counter([e.get('event', 'N/A') for e in today_events])
        print("\nCritical event types:")
        for event_type, count in sorted(critical_types.items(), key=lambda x: x[1], reverse=True):
            print(f"  {event_type}: {count}")
        print()
        
        # Check if notifications were sent for these
        today_notifications = [e for e in notification_events if e.get('ts_utc', '').startswith(today)]
        print(f"Notifications sent today: {len(today_notifications)}")
        
        if len(today_events) > 0 and len(today_notifications) == 0:
            print("\n[WARN] Critical events occurred but no notifications were sent!")
            print("This suggests notifications may not be configured or working")
    else:
        print("No critical events today (good!)")
    
    print()
    print("=" * 80)
    print("SUMMARY")
    print("=" * 80)
    print(f"Total notification events: {len(notification_events)}")
    print(f"Notifications on Jan 22: {len(jan22_notifications)}")
    print(f"Critical events today: {len(today_events)}")
    print(f"Notifications today: {len([e for e in notification_events if e.get('ts_utc', '').startswith(today)])}")
    
    # Recommendations
    print()
    print("RECOMMENDATIONS:")
    if len(notification_events) == 0:
        print("  [ ] Check if HealthMonitor is initialized in RobotEngine")
        print("  [ ] Verify health_monitor.secrets.json exists and has valid keys")
        print("  [ ] Check if NotificationService background worker is running")
    elif len(jan22_notifications) == 0 and len(jan22_notifications) < 10:
        print("  [ ] Notifications may not be triggering for all critical events")
        print("  [ ] Review HealthMonitor.ReportCritical() calls")
        print("  [ ] Check notification rate limiting")
    else:
        print("  [OK] Notification system appears to be working")

if __name__ == "__main__":
    main()
