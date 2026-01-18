#!/usr/bin/env python3
"""
Analyze today's robot logs to show:
1. Recent logging summary
2. Ranges for today
3. Panic/alert events and why push notifications weren't sent
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict
from typing import Dict, List, Optional
import pytz

# Setup paths
QTSW2_ROOT = Path(__file__).parent
LOGS_DIR = QTSW2_ROOT / "logs" / "robot"
JOURNAL_DIR = LOGS_DIR / "journal"
CHICAGO_TZ = pytz.timezone("America/Chicago")

# Get today's date
today = datetime.now(CHICAGO_TZ).date()
today_str = today.strftime("%Y-%m-%d")

print(f"=== TODAY'S LOG ANALYSIS ({today_str}) ===\n")

# 1. Find today's journal files (ranges)
print("1. TODAY'S RANGES (from journal files)")
print("-" * 80)
journal_files = list(JOURNAL_DIR.glob(f"{today_str}_*.json"))
if not journal_files:
    print(f"[!] No journal files found for {today_str}")
    print(f"   Looking in: {JOURNAL_DIR}")
    print(f"   Available dates: {sorted(set(f.stem.split('_')[0] for f in JOURNAL_DIR.glob('*.json')))[-5:]}")
else:
    ranges_by_stream = {}
    for journal_file in sorted(journal_files):
        try:
            with open(journal_file, 'r') as f:
                journal = json.load(f)
            stream = journal.get('stream', 'UNKNOWN')
            trading_date = journal.get('trading_date', 'UNKNOWN')
            state = journal.get('last_state', 'UNKNOWN')
            committed = journal.get('committed', False)
            
            # Try to extract range info if available
            range_high = journal.get('range_high')
            range_low = journal.get('range_low')
            freeze_close = journal.get('freeze_close')
            
            ranges_by_stream[stream] = {
                'trading_date': trading_date,
                'state': state,
                'committed': committed,
                'range_high': range_high,
                'range_low': range_low,
                'freeze_close': freeze_close,
                'slot_time': journal.get('slot_time_chicago', 'N/A')
            }
            
            print(f"[RANGE] {stream}:")
            print(f"   State: {state}")
            print(f"   Committed: {committed}")
            if range_high is not None and range_low is not None:
                range_size = float(range_high) - float(range_low)
                print(f"   Range: {range_low} - {range_high} (size: {range_size:.2f})")
            else:
                print(f"   Range: Not computed yet")
            if freeze_close is not None:
                print(f"   Freeze Close: {freeze_close}")
            print()
        except Exception as e:
            print(f"[ERROR] Error reading {journal_file.name}: {e}")
    
    if not ranges_by_stream:
        print(f"[WARN] Found {len(journal_files)} journal files but couldn't parse ranges")

# 2. Analyze ENGINE log for panic/alert events
print("\n2. PANIC/ALERT EVENTS (from ENGINE log)")
print("-" * 80)
engine_log = LOGS_DIR / "robot_ENGINE.jsonl"
if not engine_log.exists():
    print(f"[ERROR] ENGINE log not found: {engine_log}")
else:
    panic_events = []
    alert_events = []
    notification_events = []
    range_events = []
    
    # Read last 1000 lines to find recent events
    try:
        with open(engine_log, 'r', encoding='utf-8') as f:
            lines = f.readlines()
            recent_lines = lines[-1000:] if len(lines) > 1000 else lines
        
        for line in recent_lines:
            try:
                event = json.loads(line.strip())
                event_type = event.get('event', '')
                data = event.get('data', {})
                payload = data.get('payload', {})
                ts_utc = event.get('ts_utc', '')
                
                # Check for panic/alert keywords
                if any(keyword in event_type.upper() for keyword in ['PANIC', 'ALERT', 'STALL', 'FAILURE', 'ERROR', 'CONNECTION_LOST']):
                    if 'NOTIFICATION' in event_type.upper() or 'PUSHOVER' in event_type.upper():
                        notification_events.append((ts_utc, event_type, payload))
                    elif 'RANGE' in event_type.upper():
                        range_events.append((ts_utc, event_type, payload))
                    else:
                        alert_events.append((ts_utc, event_type, payload))
                
                # Also check for notification and range events
                if 'NOTIFICATION' in event_type.upper() or 'PUSHOVER' in event_type.upper():
                    notification_events.append((ts_utc, event_type, payload))
                if 'RANGE' in event_type.upper():
                    range_events.append((ts_utc, event_type, payload))
                    
            except json.JSONDecodeError:
                continue
    
    except Exception as e:
        print(f"❌ Error reading ENGINE log: {e}")
    
    # Display panic/alert events
    if alert_events:
        print(f"[ALERT] Found {len(alert_events)} alert events:")
        for ts, event_type, payload in alert_events[-10:]:  # Last 10
            print(f"\n   [{ts}] {event_type}")
            if isinstance(payload, dict):
                for key, value in list(payload.items())[:5]:
                    print(f"      {key}: {value}")
    else:
        print("[OK] No alert events found in recent logs")
    
    # Display notification events
    if notification_events:
        print(f"\n[NOTIFICATION] Found {len(notification_events)} notification events:")
        for ts, event_type, payload in notification_events[-10:]:  # Last 10
            print(f"\n   [{ts}] {event_type}")
            if isinstance(payload, dict):
                for key, value in list(payload.items())[:5]:
                    print(f"      {key}: {value}")
    else:
        print("\n[ERROR] No notification events found - this explains why you didn't receive push notifications!")
    
    # Display range events
    if range_events:
        print(f"\n[RANGE] Found {len(range_events)} range-related events:")
        for ts, event_type, payload in range_events[-10:]:  # Last 10
            print(f"\n   [{ts}] {event_type}")
            if isinstance(payload, dict):
                for key, value in list(payload.items())[:5]:
                    print(f"      {key}: {value}")

# 3. Check push notification configuration
print("\n\n3. PUSH NOTIFICATION CONFIGURATION")
print("-" * 80)
health_monitor_config = QTSW2_ROOT / "configs" / "robot" / "health_monitor.json"
if health_monitor_config.exists():
    try:
        with open(health_monitor_config, 'r') as f:
            config = json.load(f)
        enabled = config.get('enabled', False)
        pushover_enabled = config.get('pushover_enabled', False)
        user_key = config.get('pushover_user_key', '')
        app_token = config.get('pushover_app_token', '')
        
        print(f"Health Monitor Enabled: {enabled}")
        print(f"Pushover Enabled: {pushover_enabled}")
        print(f"User Key Present: {'Yes' if user_key else 'No'}")
        print(f"App Token Present: {'Yes' if app_token else 'No'}")
        
        if not enabled:
            print("\n[ERROR] Health monitor is DISABLED - no alerts will be sent")
        elif not pushover_enabled:
            print("\n[ERROR] Pushover notifications are DISABLED - alerts logged but not sent")
        elif not user_key or not app_token:
            print("\n[ERROR] Pushover credentials missing - notifications cannot be sent")
        else:
            print("\n[OK] Pushover configuration looks correct")
    except Exception as e:
        print(f"[ERROR] Error reading health monitor config: {e}")
else:
    print(f"[ERROR] Health monitor config not found: {health_monitor_config}")
    print("   This means health monitoring is disabled - no alerts will be sent")

# 4. Check for recent log activity
print("\n\n4. RECENT LOG ACTIVITY SUMMARY")
print("-" * 80)
if engine_log.exists():
    try:
        with open(engine_log, 'r', encoding='utf-8') as f:
            lines = f.readlines()
        
        if lines:
            last_line = lines[-1]
            last_event = json.loads(last_line.strip())
            last_ts = last_event.get('ts_utc', '')
            last_event_type = last_event.get('event', '')
            
            print(f"Last log entry: {last_ts}")
            print(f"Last event type: {last_event_type}")
            print(f"Total log entries: {len(lines):,}")
            
            # Count events by type in last 100 lines
            event_counts = defaultdict(int)
            for line in lines[-100:]:
                try:
                    event = json.loads(line.strip())
                    event_type = event.get('event', 'UNKNOWN')
                    event_counts[event_type] += 1
                except:
                    continue
            
            print(f"\nMost common events (last 100 entries):")
            for event_type, count in sorted(event_counts.items(), key=lambda x: -x[1])[:10]:
                print(f"   {event_type}: {count}")
    except Exception as e:
        print(f"[ERROR] Error analyzing log activity: {e}")

print("\n" + "=" * 80)
print("Analysis complete!")
