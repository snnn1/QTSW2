#!/usr/bin/env python3
"""Check ES stop loss and re-entry issue"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

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
    cutoff = datetime.now(timezone.utc) - timedelta(minutes=30)  # Last 30 minutes
    
    print("="*80)
    print("ES STOP LOSS AND RE-ENTRY INVESTIGATION")
    print("="*80)
    print(f"Checking logs from last 30 minutes\n")
    
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
    
    # Filter ES events
    es_events = [e for e in events if 'ES' in str(e.get('data', {}).get('stream', '')) or 'ES' in str(e.get('data', {}).get('stream_id', '')) or 'ES' in str(e.get('data', {}).get('instrument', ''))]
    
    print(f"Total ES events: {len(es_events)}\n")
    
    # 1. Stop loss fills
    print("1. STOP LOSS FILLS")
    print("-"*80)
    stop_fills = [e for e in es_events if 'STOP' in e.get('event', '').upper() and ('FILL' in e.get('event', '').upper() or 'FILLED' in e.get('event', '').upper())]
    order_fills = [e for e in es_events if 'EXECUTION_FILLED' in e.get('event', '')]
    
    print(f"Stop-related fills: {len(stop_fills)}")
    print(f"All execution fills: {len(order_fills)}")
    
    if order_fills:
        print("\n  Recent fills:")
        for e in order_fills[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', data.get('stream_id', 'N/A'))
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            fill_price = data.get('fill_price', data.get('actual_fill_price', 'N/A'))
            order_type = data.get('order_type', data.get('order_name', 'N/A'))
            direction = data.get('direction', 'N/A')
            print(f"    {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC | {order_type} | {direction} | Intent: {intent_id} | Price: {fill_price}")
    
    # 2. Order submissions after stop
    print("\n2. ORDER SUBMISSIONS")
    print("-"*80)
    order_submitted = [e for e in es_events if 'ORDER_SUBMITTED' in e.get('event', '') or 'ORDER_CREATED' in e.get('event', '')]
    bracket_submitted = [e for e in es_events if 'STOP_BRACKETS_SUBMITTED' in e.get('event', '')]
    
    print(f"Order submissions: {len(order_submitted)}")
    print(f"Bracket submissions: {len(bracket_submitted)}")
    
    if order_submitted:
        print("\n  Recent order submissions:")
        for e in order_submitted[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            order_type = data.get('order_type', data.get('order_name', 'N/A'))
            direction = data.get('direction', 'N/A')
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            print(f"    {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC | {order_type} | {direction} | Intent: {intent_id}")
    
    # 3. Order cancellations
    print("\n3. ORDER CANCELLATIONS")
    print("-"*80)
    cancellations = [e for e in es_events if 'CANCELLED' in e.get('event', '').upper() or 'CANCEL' in e.get('event', '').upper()]
    print(f"Cancellations: {len(cancellations)}")
    
    if cancellations:
        print("\n  Recent cancellations:")
        for e in cancellations[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            order_type = data.get('order_type', data.get('order_name', 'N/A'))
            reason = data.get('reason', data.get('cancel_reason', 'N/A'))
            print(f"    {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC | {order_type} | Reason: {reason}")
    
    # 4. Stream state changes
    print("\n4. STREAM STATE CHANGES")
    print("-"*80)
    state_changes = [e for e in es_events if 'STATE' in e.get('event', '').upper() or 'RANGE_LOCKED' in e.get('event', '') or 'ARMED' in e.get('event', '')]
    print(f"State changes: {len(state_changes)}")
    
    if state_changes:
        print("\n  Recent state changes:")
        for e in state_changes[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            event_type = e.get('event', 'N/A')
            data = e.get('data', {})
            state = data.get('state', data.get('new_state', 'N/A'))
            print(f"    {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC | {event_type} | State: {state}")
    
    # 5. Timeline analysis
    print("\n5. TIMELINE ANALYSIS")
    print("-"*80)
    
    # Get all relevant events in chronological order
    relevant_events = []
    for e in es_events:
        event_type = e.get('event', '')
        if any(x in event_type.upper() for x in ['FILL', 'SUBMIT', 'CANCEL', 'STOP', 'BRACKET', 'STATE', 'RANGE_LOCKED']):
            relevant_events.append(e)
    
    relevant_events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    print("Chronological sequence:")
    for e in relevant_events[-20:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        event_type = e.get('event', 'N/A')
        data = e.get('data', {})
        stream = data.get('stream', data.get('stream_id', 'N/A'))
        direction = data.get('direction', '')
        intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else ''
        order_type = data.get('order_type', data.get('order_name', ''))
        
        summary = f"{event_type}"
        if direction:
            summary += f" | {direction}"
        if intent_id and intent_id != 'N/A':
            summary += f" | Intent: {intent_id}"
        if order_type:
            summary += f" | {order_type}"
        
        print(f"  {ts.strftime('%H:%M:%S.%f')[:-3] if ts else 'N/A'} UTC | {summary}")
    
    print("\n" + "="*80)

if __name__ == "__main__":
    main()
