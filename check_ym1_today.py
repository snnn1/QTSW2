#!/usr/bin/env python3
"""
Check what happened to YM1 today
"""
import json
import os
import datetime
from pathlib import Path
from collections import defaultdict

def load_ym_events_today():
    """Load YM events from today and recent events"""
    events = []
    log_file = Path("logs/robot/robot_YM.jsonl")
    
    if not log_file.exists():
        print(f"[ERROR] Log file not found: {log_file}")
        return []
    
    # Today's date (UTC)
    today_start = datetime.datetime.now(datetime.timezone.utc).replace(
        hour=0, minute=0, second=0, microsecond=0
    )
    
    # Also check last 24 hours for context
    recent_start = datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(hours=24)
    
    try:
        with open(log_file, 'r', encoding='utf-8-sig', errors='ignore') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    if 'timestamp_utc' in event:
                        ts = datetime.datetime.fromisoformat(
                            event['timestamp_utc'].replace('Z', '+00:00')
                        )
                        # Load today's events, or recent events if none today
                        if ts >= today_start or (ts >= recent_start and len(events) < 100):
                            events.append(event)
                except json.JSONDecodeError:
                    continue
    except Exception as e:
        print(f"[ERROR] Error reading log file: {e}")
        return []
    
    return sorted(events, key=lambda x: x.get('timestamp_utc', ''))

def analyze_ym_events(events):
    """Analyze YM events"""
    print("\n" + "="*80)
    print("YM1 TODAY - EVENT ANALYSIS")
    print("="*80)
    
    if not events:
        print("\n[INFO] No YM events found today")
        return
    
    print(f"\nTotal YM events today: {len(events)}")
    
    # Event type breakdown
    event_types = defaultdict(int)
    for event in events:
        event_type = event.get('event_type', 'UNKNOWN')
        event_types[event_type] += 1
    
    print(f"\nEvent Types ({len(event_types)} unique):")
    for event_type, count in sorted(event_types.items(), key=lambda x: -x[1])[:20]:
        print(f"   - {event_type}: {count}")
    
    # Time range
    if events:
        first = events[0].get('timestamp_utc', '')[:19]
        last = events[-1].get('timestamp_utc', '')[:19]
        print(f"\nTime Range:")
        print(f"   First event: {first}")
        print(f"   Last event: {last}")
    
    # Critical/Error events
    print("\n" + "="*80)
    print("CRITICAL / ERROR EVENTS")
    print("="*80)
    
    critical_events = []
    for event in events:
        event_type = event.get('event_type', '')
        if any(x in event_type for x in ['ERROR', 'FAILED', 'CRITICAL', 'REJECTED', 'EXCEPTION']):
            critical_events.append(event)
    
    if critical_events:
        print(f"\nFound {len(critical_events)} critical/error events:")
        for event in critical_events:
            ts = event.get('timestamp_utc', '')[:19]
            event_type = event.get('event_type', 'UNKNOWN')
            note = event.get('note', event.get('error', event.get('reason', '')))
            print(f"\n   [{ts}] {event_type}")
            if note:
                print(f"      Note: {note[:100]}")
            # Show relevant fields
            if 'intent_id' in event:
                print(f"      Intent: {event['intent_id'][:16]}")
            if 'stream' in event:
                print(f"      Stream: {event['stream']}")
            if 'instrument' in event:
                print(f"      Instrument: {event['instrument']}")
    else:
        print("\n[OK] No critical/error events found")
    
    # Intent events
    print("\n" + "="*80)
    print("INTENT EVENTS")
    print("="*80)
    
    intent_events = [e for e in events if 'INTENT' in e.get('event_type', '')]
    print(f"\nIntent events: {len(intent_events)}")
    
    if intent_events:
        print("\nRecent intent events:")
        for event in intent_events[-10:]:
            ts = event.get('timestamp_utc', '')[:19]
            event_type = event.get('event_type', 'UNKNOWN')
            intent_id = event.get('intent_id', 'UNKNOWN')[:16]
            stream = event.get('stream', 'UNKNOWN')
            print(f"   [{ts}] {event_type} | Intent: {intent_id} | Stream: {stream}")
    
    # Fill events
    print("\n" + "="*80)
    print("FILL EVENTS")
    print("="*80)
    
    fill_events = [e for e in events if 'FILL' in e.get('event_type', '')]
    print(f"\nFill events: {len(fill_events)}")
    
    if fill_events:
        print("\nRecent fill events:")
        for event in fill_events[-10:]:
            ts = event.get('timestamp_utc', '')[:19]
            event_type = event.get('event_type', 'UNKNOWN')
            intent_id = event.get('intent_id', 'UNKNOWN')[:16]
            fill_price = event.get('fill_price', event.get('price', '?'))
            fill_qty = event.get('fill_quantity', event.get('quantity', '?'))
            print(f"   [{ts}] {event_type} | Intent: {intent_id} | Price: {fill_price} | Qty: {fill_qty}")
    
    # Order events
    print("\n" + "="*80)
    print("ORDER EVENTS")
    print("="*80)
    
    order_events = [e for e in events if 'ORDER' in e.get('event_type', '')]
    print(f"\nOrder events: {len(order_events)}")
    
    if order_events:
        order_types = defaultdict(int)
        for event in order_events:
            order_type = event.get('order_type', event.get('event_type', 'UNKNOWN'))
            order_types[order_type] += 1
        
        print("\nOrder type breakdown:")
        for order_type, count in sorted(order_types.items(), key=lambda x: -x[1]):
            print(f"   - {order_type}: {count}")
    
    # Stream state events
    print("\n" + "="*80)
    print("STREAM STATE EVENTS")
    print("="*80)
    
    stream_events = [e for e in events if 'STREAM' in e.get('event_type', '') or 'STATE' in e.get('event_type', '')]
    print(f"\nStream/State events: {len(stream_events)}")
    
    if stream_events:
        print("\nRecent stream state events:")
        for event in stream_events[-10:]:
            ts = event.get('timestamp_utc', '')[:19]
            event_type = event.get('event_type', 'UNKNOWN')
            stream = event.get('stream', 'UNKNOWN')
            state = event.get('state', event.get('new_state', '?'))
            print(f"   [{ts}] {event_type} | Stream: {stream} | State: {state}")

def main():
    print("="*80)
    print("YM1 TODAY - ANALYSIS")
    print("="*80)
    print(f"Checking logs for today ({datetime.datetime.now(datetime.timezone.utc).date()})...")
    
    events = load_ym_events_today()
    
    # Check if we have any events at all
    log_file = Path("logs/robot/robot_YM.jsonl")
    all_events = []
    try:
        with open(log_file, 'r', encoding='utf-8-sig', errors='ignore') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    all_events.append(event)
                except json.JSONDecodeError:
                    continue
    except Exception as e:
        print(f"[ERROR] Error reading log file: {e}")
    
    if not events and all_events:
        print(f"\n[INFO] No YM events found today, but log has {len(all_events)} total events")
        print(f"   Last event timestamp: {all_events[-1].get('timestamp_utc', 'N/A')[:19] if all_events else 'N/A'}")
        print("   Analyzing most recent events instead...")
        # Analyze last 100 events for context
        events = all_events[-100:]
    
    if not events:
        print("\n[INFO] No YM events found at all")
        print("   This could mean:")
        print("   1. YM1 not trading today (market closed or no signals)")
        print("   2. Log file issue")
        return
    
    analyze_ym_events(events)
    
    print("\n" + "="*80)
    print("SUMMARY")
    print("="*80)
    
    error_count = len([e for e in events if any(x in e.get('event_type', '') for x in ['ERROR', 'FAILED', 'CRITICAL'])])
    intent_count = len([e for e in events if 'INTENT' in e.get('event_type', '')])
    fill_count = len([e for e in events if 'FILL' in e.get('event_type', '')])
    
    print(f"\nTotal Events: {len(events)}")
    print(f"Intent Events: {intent_count}")
    print(f"Fill Events: {fill_count}")
    print(f"Error/Critical Events: {error_count}")
    
    if error_count > 0:
        print(f"\n[WARNING] Found {error_count} error/critical events - check details above")
    else:
        print(f"\n[OK] No error/critical events found")

if __name__ == "__main__":
    main()
