#!/usr/bin/env python3
"""Quick script to check watchdog stream states"""
import requests
import json

try:
    r = requests.get('http://localhost:8002/api/watchdog/stream-states', timeout=10)
    data = r.json()
    streams = data.get('streams', [])
    timestamp = data.get('timestamp_chicago', 'N/A')
    
    print(f"Watchdog Stream States (as of {timestamp})")
    print(f"Total streams: {len(streams)}")
    print("\n" + "=" * 100)
    print(f"{'Stream':<8} | {'Inst':<6} | {'Sess':<5} | {'State':<20} | {'Committed':<10} | {'Entry Time (UTC)':<25} | {'Range':<20}")
    print("=" * 100)
    
    if not streams:
        print("  No active streams")
    else:
        for s in sorted(streams, key=lambda x: (x.get('trading_date', ''), x.get('stream', ''))):
            stream_id = s.get('stream', 'N/A')
            state = s.get('state', 'N/A')
            instrument = s.get('instrument', 'N/A')
            session = s.get('session', 'N/A')
            committed = s.get('committed', False)
            entry_time = s.get('state_entry_time_utc', 'N/A')
            range_high = s.get('range_high')
            range_low = s.get('range_low')
            
            # Format range
            if range_high is not None and range_low is not None:
                range_str = f"{range_low:.2f} - {range_high:.2f}"
            else:
                range_str = "-"
            
            # Format entry time (just show time part if it's a full timestamp)
            if entry_time != 'N/A' and 'T' in str(entry_time):
                entry_time = str(entry_time).split('T')[1].split('+')[0] if '+' in str(entry_time) else str(entry_time).split('T')[1]
            
            print(f"  {stream_id:<8} | {instrument:<6} | {session:<5} | {state:<20} | {str(committed):<10} | {entry_time:<25} | {range_str:<20}")
    
    print("=" * 100)
    
except Exception as e:
    print(f"Error: {e}")
