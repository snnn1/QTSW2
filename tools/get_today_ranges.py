#!/usr/bin/env python3
"""Extract computed ranges for enabled streams today"""

import json
import sys
from datetime import datetime
from collections import defaultdict
from pathlib import Path

def main():
    log_file = Path("logs/robot/robot_ENGINE.jsonl")
    if not log_file.exists():
        print(f"Log file not found: {log_file}")
        return
    
    today = datetime.now().date()
    today_str = str(today)
    
    # Collect range information
    ranges_by_stream = defaultdict(dict)
    
    with open(log_file, 'r', encoding='utf-8') as f:
        for line in f:
            try:
                event = json.loads(line)
                ts = event.get('ts_utc', '')
                
                # Check if event is from today
                if not ts.startswith(today_str):
                    continue
                
                event_type = event.get('event', '')
                data = event.get('data', {})
                
                # Look for range information in various event types
                if 'RANGE' in event_type or 'BREAKOUT' in event_type:
                    stream_id = data.get('stream_id')
                    if stream_id:
                        # Update with latest range info
                        if 'range_low' in data or 'range_high' in data:
                            ranges_by_stream[stream_id].update({
                                'stream_id': stream_id,
                                'instrument': data.get('instrument', ''),
                                'range_low': data.get('range_low'),
                                'range_high': data.get('range_high'),
                                'range_size': data.get('range_size'),
                                'brk_long': data.get('brk_long'),
                                'brk_short': data.get('brk_short'),
                                'freeze_close': data.get('freeze_close'),
                                'timestamp': ts
                            })
                
                # Also check STOP_BRACKETS_SUBMITTED events
                if event_type == 'STOP_BRACKETS_SUBMITTED':
                    stream_id = data.get('stream_id')
                    if stream_id:
                        ranges_by_stream[stream_id].update({
                            'stream_id': stream_id,
                            'instrument': data.get('instrument', ''),
                            'brk_long': data.get('long_brk') or data.get('brk_long'),
                            'brk_short': data.get('short_brk') or data.get('brk_short'),
                            'timestamp': ts
                        })
            
            except json.JSONDecodeError:
                continue
            except Exception as e:
                print(f"Error processing line: {e}", file=sys.stderr)
                continue
    
    # Print results
    if not ranges_by_stream:
        print(f"No range information found for today ({today_str})")
        print("\nTrying to find any recent range information...")
        # Fallback: get latest ranges regardless of date
        with open(log_file, 'r', encoding='utf-8') as f:
            lines = f.readlines()
            for line in reversed(lines[-100000:]):
                try:
                    event = json.loads(line)
                    event_type = event.get('event', '')
                    data = event.get('data', {})
                    
                    if 'RANGE_LOCKED' in event_type and 'range_low' in data:
                        stream_id = data.get('stream_id')
                        if stream_id and stream_id not in ranges_by_stream:
                            ranges_by_stream[stream_id] = {
                                'stream_id': stream_id,
                                'instrument': data.get('instrument', ''),
                                'range_low': data.get('range_low'),
                                'range_high': data.get('range_high'),
                                'range_size': data.get('range_size'),
                                'brk_long': data.get('brk_long'),
                                'brk_short': data.get('brk_short'),
                                'freeze_close': data.get('freeze_close'),
                                'timestamp': event.get('ts_utc', '')
                            }
                            if len(ranges_by_stream) >= 20:  # Limit to avoid too much output
                                break
                except:
                    continue
    
    if ranges_by_stream:
        print(f"\n{'='*80}")
        print(f"Computed Ranges for Enabled Streams")
        print(f"{'='*80}\n")
        
        for stream_id in sorted(ranges_by_stream.keys()):
            r = ranges_by_stream[stream_id]
            print(f"Stream: {r.get('stream_id', 'N/A')}")
            print(f"  Instrument: {r.get('instrument', 'N/A')}")
            if r.get('range_low') is not None:
                print(f"  Range Low:  {r.get('range_low')}")
                print(f"  Range High: {r.get('range_high')}")
                print(f"  Range Size: {r.get('range_size')}")
            if r.get('brk_long') is not None:
                print(f"  Breakout Long:  {r.get('brk_long')}")
            if r.get('brk_short') is not None:
                print(f"  Breakout Short: {r.get('brk_short')}")
            if r.get('freeze_close') is not None:
                print(f"  Freeze Close:  {r.get('freeze_close')}")
            print(f"  Last Updated: {r.get('timestamp', 'N/A')}")
            print()
    else:
        print("No range information found in logs")

if __name__ == '__main__':
    main()
