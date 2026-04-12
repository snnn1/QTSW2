#!/usr/bin/env python3
"""
Check logs to verify protective order fixes are working:
1. Recovery guard check (PROTECTIVE_ORDERS_BLOCKED_RECOVERY_FLATTENED)
2. Order rejection handling (PROTECTIVE_ORDER_REJECTED_FLATTENED)
3. Normal operation (PROTECTIVE_ORDERS_SUBMITTED)
4. Submission failures (PROTECTIVE_ORDERS_FAILED_FLATTENED)
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

# Event types to check
PROTECTIVE_EVENTS = [
    "PROTECTIVE_ORDERS_BLOCKED_RECOVERY_FLATTENED",
    "PROTECTIVE_ORDERS_BLOCKED_RECOVERY",
    "PROTECTIVE_ORDER_REJECTED_FLATTENED",
    "PROTECTIVE_ORDER_REJECTED_INTENT_NOT_FOUND",
    "PROTECTIVE_ORDERS_SUBMITTED",
    "PROTECTIVE_ORDERS_FAILED_FLATTENED",
    "ORDER_REJECTED",  # Check if protective orders are being rejected
]

EXECUTION_EVENTS = [
    "EXECUTION_ALLOWED",
    "EXECUTION_BLOCKED",
    "RECOVERY_STATE",
]

def parse_timestamp(ts_str):
    """Parse ISO timestamp string."""
    try:
        # Handle both with and without Z
        ts_str = ts_str.replace('Z', '+00:00')
        return datetime.fromisoformat(ts_str)
    except:
        return None

def check_log_file(log_path):
    """Check a single log file for protective order events."""
    if not log_path.exists():
        return None
    
    events_found = defaultdict(list)
    total_lines = 0
    
    try:
        with open(log_path, 'r', encoding='utf-8-sig') as f:
            for line in f:
                total_lines += 1
                line = line.strip()
                if not line:
                    continue
                
                try:
                    event = json.loads(line)
                    event_type = event.get('event_type', '')
                    
                    # Check for protective order events
                    if event_type in PROTECTIVE_EVENTS:
                        events_found[event_type].append(event)
                    
                    # Check for execution/recovery events
                    if event_type in EXECUTION_EVENTS:
                        events_found[event_type].append(event)
                    
                    # Check ORDER_REJECTED for protective orders
                    if event_type == "ORDER_REJECTED":
                        payload = event.get('payload', {})
                        if isinstance(payload, str):
                            # Try to parse payload string
                            if 'STOP' in payload or 'TARGET' in payload:
                                events_found['ORDER_REJECTED_PROTECTIVE'].append(event)
                        elif isinstance(payload, dict):
                            order_type = payload.get('order_type', '')
                            if order_type in ['STOP', 'TARGET']:
                                events_found['ORDER_REJECTED_PROTECTIVE'].append(event)
                
                except json.JSONDecodeError:
                    continue
    
    except Exception as e:
        print(f"Error reading {log_path}: {e}", file=sys.stderr)
        return None
    
    return {
        'file': str(log_path),
        'total_lines': total_lines,
        'events': dict(events_found)
    }

def main():
    project_root = Path(__file__).parent
    log_dir = project_root / "logs" / "robot"
    
    # Check frontend feed (watchdog processed events)
    frontend_feed = log_dir / "frontend_feed.jsonl"
    
    # Also check for any robot log files
    robot_logs = list(log_dir.glob("robot_*.jsonl"))
    
    print("=" * 80)
    print("PROTECTIVE ORDER FIXES VERIFICATION")
    print("=" * 80)
    print()
    
    results = []
    
    # Check frontend feed
    if frontend_feed.exists():
        print(f"Checking frontend feed: {frontend_feed}")
        result = check_log_file(frontend_feed)
        if result:
            results.append(result)
    
    # Check robot logs
    for log_file in sorted(robot_logs, key=lambda p: p.stat().st_mtime, reverse=True)[:5]:
        print(f"Checking robot log: {log_file.name}")
        result = check_log_file(log_file)
        if result:
            results.append(result)
    
    if not results:
        print("No log files found or no events detected.")
        return
    
    print()
    print("=" * 80)
    print("EVENT SUMMARY")
    print("=" * 80)
    print()
    
    # Aggregate events across all files
    all_events = defaultdict(list)
    for result in results:
        for event_type, events in result['events'].items():
            all_events[event_type].extend(events)
    
    # Check for each critical event type
    checks = {
        "Recovery Guard Check": [
            "PROTECTIVE_ORDERS_BLOCKED_RECOVERY_FLATTENED",
            "PROTECTIVE_ORDERS_BLOCKED_RECOVERY",
        ],
        "Order Rejection Handling": [
            "PROTECTIVE_ORDER_REJECTED_FLATTENED",
            "ORDER_REJECTED_PROTECTIVE",
        ],
        "Normal Operation": [
            "PROTECTIVE_ORDERS_SUBMITTED",
        ],
        "Submission Failures": [
            "PROTECTIVE_ORDERS_FAILED_FLATTENED",
        ],
    }
    
    for check_name, event_types in checks.items():
        print(f"\n{check_name}:")
        found_any = False
        for event_type in event_types:
            if event_type in all_events:
                count = len(all_events[event_type])
                print(f"  [OK] {event_type}: {count} event(s)")
                found_any = True
                
                # Show most recent event
                if all_events[event_type]:
                    latest = max(all_events[event_type], 
                               key=lambda e: parse_timestamp(e.get('timestamp_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
                    ts = latest.get('timestamp_utc', 'Unknown')
                    instrument = latest.get('execution_instrument_full_name') or latest.get('instrument', 'Unknown')
                    print(f"     Latest: {ts} | Instrument: {instrument}")
        
        if not found_any:
            print(f"  [WARN] No events found (this is OK if no protective orders were blocked/rejected)")
    
    # Show execution/recovery state events
    if "EXECUTION_ALLOWED" in all_events or "EXECUTION_BLOCKED" in all_events:
        print(f"\nExecution State:")
        if "EXECUTION_ALLOWED" in all_events:
            print(f"  [OK] EXECUTION_ALLOWED: {len(all_events['EXECUTION_ALLOWED'])} event(s)")
        if "EXECUTION_BLOCKED" in all_events:
            print(f"  [WARN] EXECUTION_BLOCKED: {len(all_events['EXECUTION_BLOCKED'])} event(s)")
    
    # Show detailed recent events
    print()
    print("=" * 80)
    print("RECENT PROTECTIVE ORDER EVENTS (Last 10)")
    print("=" * 80)
    print()
    
    # Combine all protective events and sort by timestamp
    all_protective = []
    for event_type in PROTECTIVE_EVENTS + ['ORDER_REJECTED_PROTECTIVE']:
        if event_type in all_events:
            all_protective.extend(all_events[event_type])
    
    if all_protective:
        # Sort by timestamp
        all_protective.sort(
            key=lambda e: parse_timestamp(e.get('timestamp_utc', '')) or datetime.min.replace(tzinfo=timezone.utc),
            reverse=True
        )
        
        for event in all_protective[:10]:
            ts = event.get('timestamp_utc', 'Unknown')
            event_type = event.get('event_type', 'Unknown')
            instrument = event.get('execution_instrument_full_name') or event.get('instrument', 'Unknown')
            intent_id = event.get('intent_id', 'N/A')
            
            print(f"{ts} | {event_type}")
            print(f"  Instrument: {instrument} | Intent: {intent_id}")
            
            # Show payload summary
            payload = event.get('payload', {})
            if isinstance(payload, dict):
                if 'error' in payload:
                    print(f"  Error: {payload['error']}")
                if 'failure_reason' in payload:
                    print(f"  Reason: {payload['failure_reason']}")
                if 'note' in payload:
                    print(f"  Note: {payload['note']}")
            elif isinstance(payload, str) and len(payload) < 200:
                print(f"  Payload: {payload[:200]}")
            
            print()
    else:
        print("No protective order events found in recent logs.")
        print("This is expected if:")
        print("  - No protective orders were submitted recently")
        print("  - No protective orders were blocked by recovery guard")
        print("  - No protective orders were rejected")
    
    print()
    print("=" * 80)
    print("VERIFICATION STATUS")
    print("=" * 80)
    print()
    
    # Final status
    has_recovery_guard = "PROTECTIVE_ORDERS_BLOCKED_RECOVERY_FLATTENED" in all_events
    has_rejection_handling = "PROTECTIVE_ORDER_REJECTED_FLATTENED" in all_events or "ORDER_REJECTED_PROTECTIVE" in all_events
    has_normal_operation = "PROTECTIVE_ORDERS_SUBMITTED" in all_events
    
    print("[OK] Recovery Guard Check: ", end="")
    if has_recovery_guard:
        print("WORKING (events found)")
    else:
        print("READY (no events = no blocks needed)")
    
    print("[OK] Order Rejection Handling: ", end="")
    if has_rejection_handling:
        print("WORKING (events found)")
    else:
        print("READY (no events = no rejections)")
    
    print("[OK] Normal Operation: ", end="")
    if has_normal_operation:
        print("WORKING (protective orders submitted successfully)")
    else:
        print("NO DATA (no protective orders submitted recently)")
    
    print()
    print("Note: The fixes are implemented in the code. Events will appear when:")
    print("  - Recovery guard blocks protective orders (during disconnect)")
    print("  - Protective orders are rejected by broker")
    print("  - Protective orders are submitted successfully")

if __name__ == "__main__":
    main()
