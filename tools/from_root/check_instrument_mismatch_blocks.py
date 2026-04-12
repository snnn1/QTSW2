#!/usr/bin/env python3
"""
Investigate INSTRUMENT_MISMATCH blocks preventing order submission.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict
import pytz

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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=24)
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("INSTRUMENT_MISMATCH BLOCK INVESTIGATION")
    print("="*80)
    
    # Load all events
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
    
    # Find INSTRUMENT_MISMATCH blocks
    mismatches = [e for e in events if 'INSTRUMENT_MISMATCH' in str(e.get('data', {}))]
    
    print(f"\nFound {len(mismatches)} INSTRUMENT_MISMATCH events\n")
    
    if mismatches:
        # Show recent examples
        print("="*80)
        print("RECENT INSTRUMENT_MISMATCH EVENTS:")
        print("="*80)
        
        for e in mismatches[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                event_type = e.get('event', 'N/A')
                stream = e.get('stream', 'N/A')
                data = e.get('data', {})
                
                print(f"\n  {ts_chicago.strftime('%H:%M:%S')} CT | {event_type} | Stream: {stream}")
                if isinstance(data, dict):
                    for key, value in data.items():
                        if 'instrument' in key.lower() or 'mismatch' in key.lower() or 'expected' in key.lower() or 'actual' in key.lower():
                            print(f"    {key}: {value}")
        
        # Analyze pattern
        print("\n" + "="*80)
        print("PATTERN ANALYSIS:")
        print("="*80)
        
        by_stream = defaultdict(list)
        for e in mismatches:
            stream = e.get('stream', 'NONE')
            by_stream[stream].append(e)
        
        print(f"\n  By stream:")
        for stream in sorted(by_stream.keys()):
            count = len(by_stream[stream])
            print(f"    {stream}: {count} mismatches")
        
        # Check what the mismatch is
        print(f"\n  Sample mismatch details:")
        sample = mismatches[-1]
        data = sample.get('data', {})
        if isinstance(data, dict):
            print(f"    Full data: {json.dumps(data, indent=2, default=str)}")
    
    # Check ORDER_SUBMIT_BLOCKED events
    blocked = [e for e in events if e.get('event') == 'ORDER_SUBMIT_BLOCKED']
    
    print("\n" + "="*80)
    print("ORDER_SUBMIT_BLOCKED DETAILS:")
    print("="*80)
    
    if blocked:
        print(f"\n  Total blocked: {len(blocked)}")
        
        # Show recent examples
        print(f"\n  Recent blocks:")
        for e in blocked[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                stream = e.get('stream', 'N/A')
                data = e.get('data', {})
                
                print(f"\n    {ts_chicago.strftime('%H:%M:%S')} CT | Stream: {stream}")
                if isinstance(data, dict):
                    reason = data.get('reason', 'N/A')
                    print(f"      Reason: {reason}")
                    for key, value in data.items():
                        if key != 'reason':
                            print(f"      {key}: {value}")

if __name__ == "__main__":
    main()
