#!/usr/bin/env python3
"""
Check RTY2 range computation throughout the day
"""
import json
import datetime
from pathlib import Path

def check_range_computation():
    """Check all range computation events"""
    log_file = Path("logs/robot/robot_RTY.jsonl")
    
    today_start = datetime.datetime.now(datetime.timezone.utc).replace(
        hour=0, minute=0, second=0, microsecond=0
    )
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig', errors='ignore') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                event = json.loads(line)
                if 'ts_utc' in event:
                    ts = datetime.datetime.fromisoformat(
                        event['ts_utc'].replace('Z', '+00:00')
                    )
                    if ts >= today_start:
                        events.append(event)
            except:
                continue
    
    print("="*80)
    print("RTY2 RANGE COMPUTATION EVENTS")
    print("="*80)
    
    # Find all range computation events
    range_events = []
    for event in events:
        event_type = event.get('event', '')
        data = event.get('data', {})
        
        # Check for range values in various events
        if 'range_high' in str(event) or 'range_low' in str(event) or 'RANGE' in event_type:
            range_high = None
            range_low = None
            
            # Try different ways to extract range values
            if isinstance(data, dict):
                payload = data.get('payload', '')
                if isinstance(payload, str):
                    if 'range_high' in payload:
                        # Extract from payload string
                        try:
                            import re
                            high_match = re.search(r'range_high\s*=\s*([\d.]+)', payload)
                            low_match = re.search(r'range_low\s*=\s*([\d.]+)', payload)
                            if high_match:
                                range_high = float(high_match.group(1))
                            if low_match:
                                range_low = float(low_match.group(1))
                        except:
                            pass
                
                # Also check direct data fields
                if 'range_high' in data:
                    range_high = data.get('range_high')
                if 'range_low' in data:
                    range_low = data.get('range_low')
                if 'reconstructed_range_high' in data:
                    range_high = data.get('reconstructed_range_high')
                if 'reconstructed_range_low' in data:
                    range_low = data.get('reconstructed_range_low')
            
            if range_high is not None or range_low is not None:
                # Convert to float if string
                try:
                    if isinstance(range_high, str):
                        range_high = float(range_high)
                    if isinstance(range_low, str):
                        range_low = float(range_low)
                except:
                    pass
                
                range_size = None
                if range_high is not None and range_low is not None:
                    try:
                        range_size = float(range_high) - float(range_low)
                    except:
                        pass
                
                range_events.append({
                    'timestamp': event.get('ts_utc', '')[:19],
                    'event': event_type,
                    'range_high': range_high,
                    'range_low': range_low,
                    'range_size': range_size
                })
    
    print(f"\nFound {len(range_events)} range computation events:\n")
    
    for i, revent in enumerate(range_events, 1):
        print(f"{i}. [{revent['timestamp']}] {revent['event']}")
        if revent['range_high']:
            print(f"   Range High: {revent['range_high']}")
        if revent['range_low']:
            print(f"   Range Low: {revent['range_low']}")
        if revent['range_size']:
            print(f"   Range Size: {revent['range_size']:.2f}")
        print()
    
    # Find the final locked range
    locked_events = [e for e in range_events if 'RANGE_LOCKED' in e['event'] or 'RANGE_LOCK_VALIDATION' in e['event']]
    
    if locked_events:
        print("="*80)
        print("FINAL RANGE AT LOCK")
        print("="*80)
        final = locked_events[-1]
        print(f"\nTimestamp: {final['timestamp']}")
        print(f"Event: {final['event']}")
        print(f"Range High: {final['range_high']}")
        print(f"Range Low: {final['range_low']}")
        print(f"Range Size: {final['range_size']:.2f}")

if __name__ == "__main__":
    check_range_computation()
