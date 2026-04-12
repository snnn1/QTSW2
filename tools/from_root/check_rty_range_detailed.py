#!/usr/bin/env python3
"""
Detailed check of RTY2 range computation - extract all range values
"""
import json
import datetime
import re
from pathlib import Path

def extract_range_values():
    """Extract all range values from logs"""
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
    print("RTY2 RANGE VALUES - ALL OCCURRENCES")
    print("="*80)
    
    range_values = []
    
    for event in events:
        event_type = event.get('event', '')
        data = event.get('data', {})
        ts = event.get('ts_utc', '')[:19]
        
        # Extract range values from various sources
        range_high = None
        range_low = None
        
        # Method 1: Direct data fields
        if isinstance(data, dict):
            if 'range_high' in data:
                range_high = data.get('range_high')
            if 'range_low' in data:
                range_low = data.get('range_low')
            if 'reconstructed_range_high' in data:
                range_high = data.get('reconstructed_range_high')
            if 'reconstructed_range_low' in data:
                range_low = data.get('reconstructed_range_low')
            
            # Method 2: Extract from payload string
            payload = data.get('payload', '')
            if isinstance(payload, str):
                high_match = re.search(r'range_high\s*=\s*([\d.]+)', payload)
                low_match = re.search(r'range_low\s*=\s*([\d.]+)', payload)
                if high_match and not range_high:
                    range_high = high_match.group(1)
                if low_match and not range_low:
                    range_low = low_match.group(1)
        
        if range_high is not None or range_low is not None:
            try:
                range_high = float(range_high) if range_high else None
                range_low = float(range_low) if range_low else None
                range_size = (range_high - range_low) if (range_high and range_low) else None
                
                range_values.append({
                    'timestamp': ts,
                    'event': event_type,
                    'range_high': range_high,
                    'range_low': range_low,
                    'range_size': range_size
                })
            except:
                pass
    
    print(f"\nFound {len(range_values)} events with range values:\n")
    
    for i, rv in enumerate(range_values, 1):
        print(f"{i}. [{rv['timestamp']}] {rv['event']}")
        print(f"   Range High: {rv['range_high']}")
        print(f"   Range Low: {rv['range_low']}")
        if rv['range_size']:
            print(f"   Range Size: {rv['range_size']:.2f}")
        print()
    
    # Check for any different values
    unique_ranges = {}
    for rv in range_values:
        key = f"{rv['range_high']}_{rv['range_low']}"
        if key not in unique_ranges:
            unique_ranges[key] = []
        unique_ranges[key].append(rv['timestamp'])
    
    print("="*80)
    print("UNIQUE RANGE VALUES")
    print("="*80)
    for key, timestamps in unique_ranges.items():
        high, low = key.split('_')
        print(f"\nRange High: {high}, Range Low: {low}")
        print(f"  Occurred at: {len(timestamps)} time(s)")
        print(f"  First: {timestamps[0]}")
        if len(timestamps) > 1:
            print(f"  Last: {timestamps[-1]}")

if __name__ == "__main__":
    extract_range_values()
