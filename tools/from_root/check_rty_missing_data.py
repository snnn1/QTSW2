#!/usr/bin/env python3
"""
Check RTY2 for missing data indicators
"""
import json
import datetime
from pathlib import Path

def check_missing_data():
    """Check for missing data indicators"""
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
    print("RTY2 MISSING DATA CHECK")
    print("="*80)
    
    # Check hydration summary for completeness
    hydration_events = [e for e in events if 'HYDRATION_SUMMARY' in e.get('event', '')]
    
    print(f"\nHYDRATION SUMMARY EVENTS: {len(hydration_events)}")
    for event in hydration_events:
        data = event.get('data', {})
        ts = event.get('ts_utc', '')[:19]
        print(f"\n[{ts}] HYDRATION_SUMMARY:")
        print(f"  Expected Bars: {data.get('expected_bars', '?')}")
        print(f"  Expected Full Range Bars: {data.get('expected_full_range_bars', '?')}")
        print(f"  Loaded Bars: {data.get('loaded_bars', '?')}")
        print(f"  Completeness: {data.get('completeness_pct', '?')}%")
        print(f"  Late Start: {data.get('late_start', '?')}")
        print(f"  Missed Breakout: {data.get('missed_breakout', '?')}")
    
    # Check for gap warnings
    gap_events = [e for e in events if 'GAP' in e.get('event', '')]
    print(f"\nGAP EVENTS: {len(gap_events)}")
    for event in gap_events[:10]:
        ts = event.get('ts_utc', '')[:19]
        event_type = event.get('event', 'UNKNOWN')
        data = event.get('data', {})
        gap_minutes = data.get('gap_minutes', data.get('missing_minutes', '?'))
        print(f"  [{ts}] {event_type} - Gap: {gap_minutes} minutes")
    
    # Check for missing data warnings
    missing_events = [e for e in events if 'MISSING' in e.get('event', '') or 'NO_BARS' in e.get('event', '')]
    print(f"\nMISSING DATA EVENTS: {len(missing_events)}")
    for event in missing_events[:10]:
        ts = event.get('ts_utc', '')[:19]
        event_type = event.get('event', 'UNKNOWN')
        print(f"  [{ts}] {event_type}")
    
    # Check range lock for bar count
    lock_events = [e for e in events if 'RANGE_LOCKED' in e.get('event', '') or 'RANGE_LOCK_VALIDATION' in e.get('event', '')]
    print(f"\nRANGE LOCK EVENTS: {len(lock_events)}")
    for event in lock_events:
        ts = event.get('ts_utc', '')[:19]
        data = event.get('data', {})
        payload = data.get('payload', '')
        if isinstance(payload, str):
            import re
            bar_count_match = re.search(r'bar_count\s*=\s*(\d+)', payload)
            if bar_count_match:
                bar_count = bar_count_match.group(1)
                print(f"  [{ts}] Bar Count at Lock: {bar_count}")
        
        # Also check direct data
        if 'bar_count' in data:
            print(f"  [{ts}] Bar Count: {data.get('bar_count')}")
    
    # Check slot end summary
    summary_events = [e for e in events if 'SLOT_END_SUMMARY' in e.get('event', '')]
    print(f"\nSLOT END SUMMARY:")
    for event in summary_events:
        ts = event.get('ts_utc', '')[:19]
        data = event.get('data', {})
        payload = data.get('payload', '')
        if isinstance(payload, str):
            import re
            bar_count_match = re.search(r'live_bar_count\s*=\s*(\d+)', payload)
            gap_match = re.search(r'largest_single_gap_minutes\s*=\s*([\d.]+)', payload)
            total_gap_match = re.search(r'total_gap_minutes\s*=\s*([\d.]+)', payload)
            
            if bar_count_match:
                print(f"  [{ts}] Live Bar Count: {bar_count_match.group(1)}")
            if gap_match:
                print(f"  [{ts}] Largest Single Gap: {gap_match.group(1)} minutes")
            if total_gap_match:
                print(f"  [{ts}] Total Gap Minutes: {total_gap_match.group(1)}")

if __name__ == "__main__":
    check_missing_data()
