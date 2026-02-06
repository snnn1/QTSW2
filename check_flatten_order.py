#!/usr/bin/env python3
"""Check what triggered the flatten order"""
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
    # Check around 16:22:24 UTC (which is 10:22:24 CT)
    cutoff = datetime(2026, 2, 5, 16, 20, 0, tzinfo=timezone.utc)
    end = datetime(2026, 2, 5, 16, 25, 0, tzinfo=timezone.utc)
    
    print("="*80)
    print("FLATTEN ORDER INVESTIGATION")
    print("="*80)
    print(f"Checking logs around 16:22:24 UTC (10:22:24 CT)\n")
    
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
                            if ts and cutoff <= ts <= end:
                                events.append(e)
                        except:
                            pass
        except:
            pass
    
    events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    # Filter ES and flatten-related events
    relevant = []
    for e in events:
        data = e.get('data', {})
        stream = str(data.get('stream', '') + data.get('stream_id', ''))
        instrument = str(data.get('instrument', ''))
        event_type = e.get('event', '')
        
        if ('ES' in stream or 'ES' in instrument) or \
           'FLATTEN' in event_type.upper() or \
           'UNKNOWN' in event_type.upper() or \
           'UNTrackED' in event_type.upper():
            relevant.append(e)
    
    print(f"Relevant events: {len(relevant)}\n")
    
    print("CHRONOLOGICAL SEQUENCE:")
    print("-"*80)
    for e in relevant:
        ts = parse_timestamp(e.get('ts_utc', ''))
        event_type = e.get('event', 'N/A')
        data = e.get('data', {})
        
        stream = data.get('stream', data.get('stream_id', 'N/A'))
        intent_id = data.get('intent_id', 'N/A')
        if intent_id and len(intent_id) > 8:
            intent_id = intent_id[:8]
        
        error = data.get('error', '')
        note = data.get('note', '')
        fill_price = data.get('fill_price', data.get('actual_fill_price', ''))
        flatten_success = data.get('flatten_success', '')
        
        summary = event_type
        if stream and stream != 'N/A':
            summary += f" | Stream: {stream}"
        if intent_id and intent_id != 'N/A':
            summary += f" | Intent: {intent_id}"
        if fill_price:
            summary += f" | Price: {fill_price}"
        if error:
            summary += f" | Error: {error[:50]}"
        if note:
            summary += f" | Note: {note[:50]}"
        if flatten_success != '':
            summary += f" | Flatten: {flatten_success}"
        
        print(f"  {ts.strftime('%H:%M:%S.%f')[:-3] if ts else 'N/A'} UTC | {summary}")

if __name__ == "__main__":
    main()
