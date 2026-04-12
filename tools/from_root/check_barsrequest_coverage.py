#!/usr/bin/env python3
"""
Check what BarsRequest requested and what it actually received
"""
import json
import datetime
from pathlib import Path
from collections import defaultdict

def check_barsrequest_coverage():
    """Check BarsRequest coverage"""
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
    print("BARSREQUEST COVERAGE ANALYSIS")
    print("="*80)
    
    # Find all BarsRequest bars (both attempted and committed)
    barsrequest_bars = []
    barsrequest_rejected = []
    
    for event in events:
        if event.get('event') == 'BAR_BUFFER_ADD_ATTEMPT':
            data = event.get('data', {})
            if data.get('bar_source') == 'BARSREQUEST':
                bar_chicago_str = data.get('bar_timestamp_chicago', '')
                if bar_chicago_str:
                    try:
                        bar_time = datetime.datetime.fromisoformat(bar_chicago_str.replace('.0000000', ''))
                        barsrequest_bars.append(bar_time)
                    except:
                        pass
        
        if event.get('event') == 'BAR_BUFFER_REJECTED':
            data = event.get('data', {})
            if data.get('bar_source') == 'BARSREQUEST':
                bar_chicago_str = data.get('bar_timestamp_chicago', '')
                if bar_chicago_str:
                    try:
                        bar_time = datetime.datetime.fromisoformat(bar_chicago_str.replace('.0000000', ''))
                        barsrequest_rejected.append(bar_time)
                    except:
                        pass
    
    barsrequest_bars.sort()
    
    print(f"\nBARSREQUEST BARS ATTEMPTED: {len(barsrequest_bars)}")
    print(f"BARSREQUEST BARS REJECTED (duplicates): {len(barsrequest_rejected)}")
    
    # Group by hour
    bars_by_hour = defaultdict(list)
    for bt in barsrequest_bars:
        bars_by_hour[bt.hour].append(bt.minute)
    
    print("\nBARSREQUEST BAR TIMELINE:")
    print("\n08:00 Hour Bars:")
    hour_8_bars = sorted(bars_by_hour.get(8, []))
    print(f"  Present: {hour_8_bars}")
    missing_8 = [m for m in range(0, 60) if m not in hour_8_bars]
    if missing_8:
        print(f"  Missing: {missing_8[:30]}")
    
    print("\n09:00 Hour Bars:")
    hour_9_bars = sorted(bars_by_hour.get(9, []))
    print(f"  Present: {hour_9_bars}")
    missing_9 = [m for m in range(0, 31) if m not in hour_9_bars]
    if missing_9:
        print(f"  Missing: {missing_9}")
    
    # Check what BarsRequest should have requested
    print("\n" + "="*80)
    print("BARSREQUEST EXPECTED VS ACTUAL")
    print("="*80)
    
    expected_range = list(range(0, 31))  # 08:00-08:30
    expected_range.extend(list(range(0, 31)))  # 09:00-09:30
    
    all_barsrequest_minutes = sorted(hour_8_bars + hour_9_bars)
    missing_from_barsrequest = [m for m in range(0, 60) if m not in hour_8_bars]
    missing_from_barsrequest.extend([m for m in range(0, 31) if m not in hour_9_bars])
    
    print(f"\nExpected BarsRequest bars: 90 (08:00-09:30)")
    print(f"Actual BarsRequest bars attempted: {len(barsrequest_bars)}")
    print(f"Missing from BarsRequest: {len(missing_from_barsrequest)} bars")
    
    # Check if missing bars are in the gap
    missing_window = list(range(57, 60))  # 08:57-08:59
    missing_window.extend(list(range(0, 13)))  # 09:00-09:12
    
    print(f"\nMissing Window: 08:57-09:12 ({len(missing_window)} bars)")
    print(f"Missing from BarsRequest: {missing_from_barsrequest}")
    
    if set(missing_window).issubset(set(missing_from_barsrequest)):
        print("\nCONCLUSION: BarsRequest DID NOT retrieve bars from missing window!")
        print("  BarsRequest only retrieved bars that existed in NinjaTrader's database")
        print("  If bars from 08:57-09:13 didn't exist in database, BarsRequest couldn't get them")
    else:
        print("\nCONCLUSION: Some missing bars WERE retrieved by BarsRequest")
        print("  But they may have been rejected as duplicates")

if __name__ == "__main__":
    check_barsrequest_coverage()
