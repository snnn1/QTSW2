#!/usr/bin/env python3
"""Check ES recent activity for stop loss and re-entry"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

def parse_timestamp(ts_str: str):
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
    cutoff = datetime.now(timezone.utc) - timedelta(minutes=15)
    
    print("="*80)
    print("ES RECENT ACTIVITY CHECK")
    print("="*80)
    print(f"Last 15 minutes\n")
    
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
    es_events = []
    for e in events:
        data = e.get('data', {})
        stream = str(data.get('stream', '') + data.get('stream_id', ''))
        instrument = str(data.get('instrument', ''))
        if 'ES' in stream or 'ES' in instrument:
            es_events.append(e)
    
    print(f"Total ES events: {len(es_events)}\n")
    
    # Show all relevant events
    print("CHRONOLOGICAL SEQUENCE:")
    print("-"*80)
    for e in es_events[-30:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        event_type = e.get('event', 'N/A')
        data = e.get('data', {})
        
        # Extract key info
        stream = data.get('stream', data.get('stream_id', 'N/A'))
        intent_id = data.get('intent_id', 'N/A')
        if intent_id and len(intent_id) > 8:
            intent_id = intent_id[:8]
        
        direction = data.get('direction', '')
        order_type = data.get('order_type', data.get('order_name', ''))
        fill_price = data.get('fill_price', data.get('actual_fill_price', ''))
        error = data.get('error', '')
        
        summary = event_type
        if direction:
            summary += f" | Dir: {direction}"
        if intent_id and intent_id != 'N/A':
            summary += f" | Intent: {intent_id}"
        if order_type:
            summary += f" | Order: {order_type}"
        if fill_price:
            summary += f" | Price: {fill_price}"
        if error:
            summary += f" | Error: {error[:40]}"
        
        print(f"  {ts.strftime('%H:%M:%S.%f')[:-3] if ts else 'N/A'} UTC | {summary}")

if __name__ == "__main__":
    main()
