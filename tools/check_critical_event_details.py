"""Check details of critical events to understand why notifications aren't being sent"""
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
    print("CRITICAL EVENT DETAILS")
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
    
    # Get latest critical events
    critical = [e for e in events if e.get('event') == 'CRITICAL_EVENT_REPORTED']
    
    if not critical:
        print("\nNo CRITICAL_EVENT_REPORTED events found")
        return
    
    print(f"\nFound {len(critical)} critical events\n")
    
    for i, e in enumerate(critical[-3:], 1):
        ts = parse_timestamp(e.get('ts_utc', ''))
        data = e.get('data', {})
        
        print(f"[Critical Event #{i}]")
        print(f"  Time: {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'}")
        print(f"  Event Type: {data.get('event_type', 'N/A')}")
        print(f"  Run ID: {data.get('run_id', 'N/A')}")
        print(f"  Dedupe Key: {data.get('dedupe_key', 'N/A')}")
        print(f"  Notification Sent: {data.get('notification_sent', 'N/A')}")
        print(f"  Priority: {data.get('priority', 'N/A')}")
        print()
    
    # Check for any notification-related events around the same time
    print("[Checking for notification events around critical events...]")
    if critical:
        latest_critical_time = parse_timestamp(critical[-1].get('ts_utc', ''))
        if latest_critical_time:
            # Get events within 5 seconds of latest critical event
            nearby = [e for e in events 
                     if parse_timestamp(e.get('ts_utc', '')) and
                     abs((parse_timestamp(e.get('ts_utc', '')) - latest_critical_time).total_seconds()) < 5]
            
            notification_events = [e for e in nearby if 'NOTIFY' in e.get('event', '').upper() or 
                                  'PUSHOVER' in e.get('event', '').upper() or
                                  'CRITICAL_NOTIFICATION' in e.get('event', '')]
            
            if notification_events:
                print(f"\nFound {len(notification_events)} notification-related events:")
                for e in notification_events:
                    ts = parse_timestamp(e.get('ts_utc', ''))
                    print(f"  {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {e.get('event', 'N/A')}")
                    data = e.get('data', {})
                    if data:
                        print(f"    Data: {json.dumps(data, indent=4)[:200]}")
            else:
                print("\n  No notification-related events found near critical events")

if __name__ == '__main__':
    main()
