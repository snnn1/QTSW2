#!/usr/bin/env python3
"""
Check RTY2 bar timeline to see which bars are present and missing
"""
import json
import datetime
from pathlib import Path
from collections import defaultdict

def check_bar_timeline():
    """Check which bars are present and missing"""
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
    print("RTY2 BAR TIMELINE ANALYSIS")
    print("="*80)
    
    # Extract bar timestamps from BAR_BUFFER_ADD_COMMITTED events
    bar_times = []
    bar_data = {}
    
    for event in events:
        if event.get('event') == 'BAR_BUFFER_ADD_COMMITTED':
            data = event.get('data', {})
            bar_chicago_str = data.get('bar_timestamp_chicago', '')
            if bar_chicago_str:
                try:
                    # Parse: "2026-02-04T08:00:00.0000000-06:00"
                    bar_time = datetime.datetime.fromisoformat(bar_chicago_str.replace('.0000000', ''))
                    bar_times.append(bar_time)
                    bar_data[bar_time] = data
                except:
                    pass
    
    # Also check for bar data in other events
    for event in events:
        if 'bar_time_chicago' in str(event.get('data', {})):
            data = event.get('data', {})
            bar_chicago_str = data.get('bar_time_chicago', '')
            if bar_chicago_str and 'BAR_BUFFER' not in event.get('event', ''):
                try:
                    bar_time = datetime.datetime.fromisoformat(bar_chicago_str.replace('.0000000', ''))
                    if bar_time not in bar_data:
                        bar_times.append(bar_time)
                        bar_data[bar_time] = data
                except:
                    pass
    
    bar_times.sort()
    
    print(f"\nTOTAL BARS FOUND: {len(bar_times)}")
    print(f"\nBAR TIMELINE:")
    
    # Group by hour
    bars_by_hour = defaultdict(list)
    for bt in bar_times:
        bars_by_hour[bt.hour].append(bt.minute)
    
    # Expected: 08:00 to 09:30 = 90 bars
    expected_minutes = list(range(0, 31))  # 08:00-08:30
    expected_minutes.extend(list(range(0, 31)))  # 09:00-09:30
    
    print("\n08:00 Hour Bars:")
    hour_8_bars = sorted(bars_by_hour.get(8, []))
    print(f"  Present: {hour_8_bars}")
    missing_8 = [m for m in range(0, 60) if m not in hour_8_bars]
    if missing_8:
        print(f"  Missing: {missing_8[:20]}...")  # Show first 20
    
    print("\n09:00 Hour Bars:")
    hour_9_bars = sorted(bars_by_hour.get(9, []))
    print(f"  Present: {hour_9_bars}")
    missing_9 = [m for m in range(0, 31) if m not in hour_9_bars]  # Only up to 09:30
    if missing_9:
        print(f"  Missing: {missing_9}")
    
    # Show gap details
    print("\n" + "="*80)
    print("GAP ANALYSIS")
    print("="*80)
    
    if len(bar_times) > 1:
        gaps = []
        for i in range(len(bar_times) - 1):
            gap_minutes = (bar_times[i+1] - bar_times[i]).total_seconds() / 60
            if gap_minutes > 1.5:  # More than 1 minute gap
                gaps.append((bar_times[i], bar_times[i+1], gap_minutes))
        
        print(f"\nGAPS > 1 minute: {len(gaps)}")
        for gap_start, gap_end, gap_mins in gaps:
            print(f"  Gap: {gap_start.strftime('%H:%M')} to {gap_end.strftime('%H:%M')} = {gap_mins:.1f} minutes")
    
    # Show first and last bars
    if bar_times:
        print(f"\nFirst Bar: {bar_times[0].strftime('%H:%M:%S')}")
        print(f"Last Bar: {bar_times[-1].strftime('%H:%M:%S')}")
    
    # Check RANGE_WINDOW_AUDIT for bar window
    print("\n" + "="*80)
    print("RANGE WINDOW AUDIT")
    print("="*80)
    
    for event in events:
        if event.get('event') == 'RANGE_WINDOW_AUDIT':
            data = event.get('data', {})
            first_bar = data.get('first_bar_chicago', '')
            last_bar = data.get('last_bar_chicago', '')
            bar_count = data.get('bar_count', '')
            ts = event.get('ts_utc', '')[:19]
            print(f"\n[{ts}]")
            print(f"  First Bar: {first_bar}")
            print(f"  Last Bar: {last_bar}")
            print(f"  Bar Count: {bar_count}")

if __name__ == "__main__":
    check_bar_timeline()
