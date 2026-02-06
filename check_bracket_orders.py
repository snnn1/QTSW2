#!/usr/bin/env python3
"""Check if stop bracket orders are working correctly"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

def parse_timestamp(ts_str: str):
    """Parse ISO timestamp"""
    if not ts_str:
        return None
    try:
        if 'T' in ts_str:
            if ts_str.endswith('Z'):
                ts_str = ts_str[:-1] + '+00:00'
            elif '+' not in ts_str:
                ts_str = ts_str + '+00:00'
            dt = datetime.fromisoformat(ts_str)
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            return dt
    except:
        pass
    return None

def main():
    log_dir = Path("logs/robot")
    cutoff = datetime.now(timezone.utc) - timedelta(hours=2)
    
    print("="*80)
    print("STOP BRACKET ORDER VERIFICATION")
    print("="*80)
    print(f"Checking logs from last 2 hours (since {cutoff.strftime('%Y-%m-%d %H:%M:%S')} UTC)\n")
    
    # Load events
    events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            ts = parse_timestamp(e.get('ts_utc', ''))
                            if ts and ts >= cutoff:
                                events.append(e)
                        except:
                            pass
        except:
            pass
    
    events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    # Find bracket-related events
    bracket_events = [e for e in events if 'BRACKET' in e.get('event', '').upper() or 'STOP_BRACKET' in e.get('event', '').upper()]
    order_events = [e for e in events if 'ORDER' in e.get('event', '')]
    range_locked = [e for e in events if e.get('event') == 'RANGE_LOCKED']
    
    print("1. RANGE LOCKED EVENTS")
    print("-"*80)
    if range_locked:
        print(f"Found {len(range_locked)} RANGE_LOCKED events:")
        for e in sorted(range_locked, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', 'N/A')
            print(f"  {stream} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    else:
        print("  No RANGE_LOCKED events found")
    
    print("\n2. STOP BRACKET SUBMISSION EVENTS")
    print("-"*80)
    bracket_submitted = [e for e in bracket_events if 'SUBMITTED' in e.get('event', '')]
    bracket_failed = [e for e in bracket_events if 'FAILED' in e.get('event', '')]
    bracket_entered = [e for e in bracket_events if 'ENTERED' in e.get('event', '')]
    
    print(f"STOP_BRACKETS_SUBMITTED: {len(bracket_submitted)} events")
    print(f"STOP_BRACKETS_SUBMIT_FAILED: {len(bracket_failed)} events")
    print(f"STOP_BRACKETS_SUBMIT_ENTERED: {len(bracket_entered)} events")
    
    if bracket_submitted:
        print("\n  Recent STOP_BRACKETS_SUBMITTED events:")
        for e in sorted(bracket_submitted, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream_id', data.get('stream', 'N/A'))
            long_id = data.get('long_intent_id', 'N/A')[:8] if data.get('long_intent_id') else 'N/A'
            short_id = data.get('short_intent_id', 'N/A')[:8] if data.get('short_intent_id') else 'N/A'
            print(f"    {stream} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC | Long: {long_id} | Short: {short_id}")
    
    if bracket_failed:
        print("\n  Recent STOP_BRACKETS_SUBMIT_FAILED events:")
        for e in sorted(bracket_failed, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-3:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream_id', data.get('stream', 'N/A'))
            error = data.get('error', data.get('reason', 'N/A'))
            print(f"    {stream} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC | Error: {error}")
    
    print("\n3. ORDER CREATION EVENTS")
    print("-"*80)
    stop_orders = [e for e in order_events if 'STOPMARKET' in e.get('event', '')]
    limit_orders = [e for e in order_events if 'LIMIT' in e.get('event', '')]
    entry_orders = [e for e in order_events if 'ENTRY' in e.get('event', '')]
    
    print(f"ORDER_CREATED_STOPMARKET: {len(stop_orders)} events")
    print(f"ORDER_CREATED_LIMIT: {len(limit_orders)} events")
    print(f"Entry order events: {len(entry_orders)} events")
    
    # Check what LIMIT orders are
    if limit_orders:
        print("\n  LIMIT Order Details:")
        for e in sorted(limit_orders, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            order_name = data.get('order_name', 'N/A')
            is_target = '_TARGET' in order_name
            is_entry = not is_target and ('ENTRY' in order_name.upper() or order_name.startswith('QTSW2:') and '_TARGET' not in order_name and '_STOP' not in order_name)
            order_type = 'TARGET' if is_target else 'ENTRY' if is_entry else 'UNKNOWN'
            print(f"    {order_type} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC | {order_name}")
    
    if stop_orders:
        print("\n  Recent ORDER_CREATED_STOPMARKET events:")
        for e in sorted(stop_orders, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            order_name = data.get('order_name', 'N/A')
            direction = 'Long' if 'LONG' in order_name.upper() else 'Short' if 'SHORT' in order_name.upper() else 'N/A'
            print(f"    {direction} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC | {order_name[:30]}")
    
    print("\n4. CHECKING FOR DUPLICATE ORDERS")
    print("-"*80)
    # Check if we see immediate entry orders (should NOT see these anymore)
    immediate_entry = [e for e in events if 'IMMEDIATE' in e.get('event', '').upper() or 'CHECKIMMEDIATE' in e.get('event', '').upper()]
    breakout_entry = [e for e in events if 'CHECKBREAKOUT' in e.get('event', '').upper() or 'BREAKOUT_ENTRY' in e.get('event', '').upper()]
    
    print(f"Immediate entry events: {len(immediate_entry)}")
    print(f"Breakout entry events: {len(breakout_entry)}")
    
    if immediate_entry:
        print("  [WARNING] Found immediate entry events - should not exist after simplification")
    else:
        print("  [OK] No immediate entry events found (expected after simplification)")
    
    if breakout_entry:
        print("  [WARNING] Found breakout entry events - CheckBreakoutEntry should be removed")
    else:
        print("  [OK] No breakout entry events found (expected after simplification)")
    
    print("\n5. SUMMARY")
    print("-"*80)
    
    # Check if brackets are being submitted
    if bracket_submitted:
        print(f"[OK] STOP_BRACKETS_SUBMITTED: {len(bracket_submitted)} events")
        print("   Status: Stop brackets ARE being submitted")
    else:
        print("[ERROR] STOP_BRACKETS_SUBMITTED: 0 events")
        print("   Status: No stop brackets submitted (may be normal if no ranges locked)")
    
    # Check if we're using new simplified code
    if bracket_entered:
        print(f"[OK] STOP_BRACKETS_SUBMIT_ENTERED: {len(bracket_entered)} events")
        print("   Status: Using new simplified code path")
    
    # Check for failures
    if bracket_failed:
        print(f"[WARN] STOP_BRACKETS_SUBMIT_FAILED: {len(bracket_failed)} events")
        print("   Status: Some bracket submissions failed - check errors above")
    else:
        print("[OK] STOP_BRACKETS_SUBMIT_FAILED: 0 events")
        print("   Status: No bracket submission failures")
    
    # Check for immediate entry (should be zero)
    if immediate_entry or breakout_entry:
        print("[ERROR] Found immediate/breakout entry events - simplification may not be active")
    else:
        print("[OK] No immediate/breakout entry events - simplification is working")
    
    print("\n" + "="*80)
    print("VERIFICATION COMPLETE")
    print("="*80)

if __name__ == "__main__":
    main()
