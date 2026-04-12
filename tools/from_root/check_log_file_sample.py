#!/usr/bin/env python3
"""Check actual log file contents"""
import json
import glob
import os

def check_log_files():
    engine_files = glob.glob('logs/robot/robot_ENGINE*.jsonl')
    print(f"Found {len(engine_files)} ENGINE log files")
    
    for log_file in engine_files[:1]:  # Check first file
        print(f"\nChecking: {log_file}")
        print(f"File size: {os.path.getsize(log_file)} bytes")
        
        # Read last 5 lines
        with open(log_file, 'r', encoding='utf-8') as f:
            lines = f.readlines()
            print(f"Total lines: {len(lines)}")
            
            if lines:
                print("\nLast 5 events:")
                for i, line in enumerate(lines[-5:], 1):
                    try:
                        event = json.loads(line.strip())
                        event_type = event.get('event_type', 'NO_TYPE')
                        utc_time = event.get('utc_time', 'N/A')
                        if isinstance(utc_time, str) and len(utc_time) > 19:
                            utc_time = utc_time[:19]
                        print(f"  {i}. event_type: {event_type}")
                        print(f"     utc_time: {utc_time}")
                        print(f"     Keys: {list(event.keys())[:10]}")
                    except Exception as e:
                        print(f"  {i}. Error parsing: {e}")
                        print(f"     Line: {line[:100]}")

if __name__ == '__main__':
    check_log_files()
