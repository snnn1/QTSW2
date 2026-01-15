#!/usr/bin/env python3
"""Verify logging cleanup is working"""
import json
import glob
from datetime import datetime, timezone
from collections import defaultdict

def main():
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    
    if not log_files:
        print("No robot log files found")
        return
    
    print("=" * 80)
    print("LOGGING CLEANUP VERIFICATION")
    print("=" * 80)
    
    # Get today's date for filtering
    today_start = datetime.now(timezone.utc).replace(hour=0, minute=0, second=0, microsecond=0)
    
    all_entries = []
    for log_file in log_files:
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        entry = json.loads(line)
                        all_entries.append(entry)
                    except:
                        continue
        except Exception as e:
            print(f"Error reading {log_file}: {e}")
    
    # Filter to today's entries
    today_entries = []
    for entry in all_entries:
        try:
            ts_str = entry.get('ts_utc', '')
            if ts_str:
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                if ts >= today_start:
                    today_entries.append(entry)
        except:
            continue
    
    print(f"\nTotal entries today: {len(today_entries)}")
    
    # Count events
    events = defaultdict(int)
    for entry in today_entries:
        event = entry.get('event', 'UNKNOWN')
        events[event] += 1
    
    # Check RANGE_COMPUTE_START
    print("\n" + "=" * 80)
    print("RANGE_COMPUTE_START ANALYSIS")
    print("=" * 80)
    
    range_start_entries = [e for e in today_entries if e.get('event') == 'RANGE_COMPUTE_START']
    print(f"Total RANGE_COMPUTE_START events today: {len(range_start_entries)}")
    
    if range_start_entries:
        # Group by stream to see if it's logging multiple times per stream
        by_stream = defaultdict(list)
        for entry in range_start_entries:
            stream = entry.get('data', {}).get('stream', 'UNKNOWN')
            instrument = entry.get('data', {}).get('instrument', 'UNKNOWN')
            key = f"{instrument}_{stream}"
            by_stream[key].append(entry)
        
        print(f"\nUnique streams: {len(by_stream)}")
        print("\nRANGE_COMPUTE_START per stream:")
        multiple_logs = []
        for stream_key, entries in sorted(by_stream.items()):
            count = len(entries)
            if count > 1:
                multiple_logs.append((stream_key, count))
            print(f"  {stream_key}: {count} occurrence(s)")
        
        if multiple_logs:
            print(f"\n[WARNING] {len(multiple_logs)} streams logged RANGE_COMPUTE_START multiple times:")
            for stream_key, count in multiple_logs:
                print(f"  {stream_key}: {count} times")
        else:
            print("\n[SUCCESS] RANGE_COMPUTE_START appears only once per stream (or less)")
    
    # Check RANGE_COMPUTE_FAILED rate limiting
    print("\n" + "=" * 80)
    print("RANGE_COMPUTE_FAILED RATE LIMITING ANALYSIS")
    print("=" * 80)
    
    range_failed_entries = [e for e in today_entries if e.get('event') == 'RANGE_COMPUTE_FAILED']
    print(f"Total RANGE_COMPUTE_FAILED events today: {len(range_failed_entries)}")
    
    if range_failed_entries:
        # Group by stream and check timing
        by_stream_failed = defaultdict(list)
        for entry in range_failed_entries:
            stream = entry.get('data', {}).get('stream', 'UNKNOWN')
            instrument = entry.get('data', {}).get('instrument', 'UNKNOWN')
            key = f"{instrument}_{stream}"
            try:
                ts_str = entry.get('ts_utc', '')
                if ts_str.endswith('Z'):
                    ts_str = ts_str[:-1] + '+00:00'
                ts = datetime.fromisoformat(ts_str)
                by_stream_failed[key].append(ts)
            except:
                pass
        
        print(f"\nUnique streams with failures: {len(by_stream_failed)}")
        
        # Check if rate limiting is working (should be max 1 per minute per stream)
        violations = []
        for stream_key, timestamps in sorted(by_stream_failed.items()):
            timestamps.sort()
            # Check for entries less than 1 minute apart
            for i in range(len(timestamps) - 1):
                diff = (timestamps[i+1] - timestamps[i]).total_seconds()
                if diff < 60:
                    violations.append((stream_key, timestamps[i], timestamps[i+1], diff))
        
        if violations:
            print(f"\n[WARNING] Found {len(violations)} rate limit violations (< 60 seconds apart):")
            for stream_key, ts1, ts2, diff in violations[:10]:  # Show first 10
                print(f"  {stream_key}: {ts1.strftime('%H:%M:%S')} -> {ts2.strftime('%H:%M:%S')} ({diff:.1f}s apart)")
        else:
            print("\n[SUCCESS] RANGE_COMPUTE_FAILED is properly rate-limited (>= 60 seconds apart)")
        
        # Show distribution
        print("\nFailure frequency per stream:")
        for stream_key, timestamps in sorted(by_stream_failed.items()):
            count = len(timestamps)
            if count > 0:
                span = (timestamps[-1] - timestamps[0]).total_minutes() if len(timestamps) > 1 else 0
                rate = count / max(span, 1) if span > 0 else count
                print(f"  {stream_key}: {count} failures over {span:.1f} minutes ({rate:.2f} per minute)")
    else:
        print("\n[SUCCESS] No RANGE_COMPUTE_FAILED errors today!")
    
    # Overall statistics
    print("\n" + "=" * 80)
    print("TOP 15 MOST FREQUENT EVENTS TODAY")
    print("=" * 80)
    
    sorted_events = sorted(events.items(), key=lambda x: x[1], reverse=True)
    for i, (event, count) in enumerate(sorted_events[:15], 1):
        percentage = (count / len(today_entries) * 100) if today_entries else 0
        print(f"{i:2d}. {event:50s} {count:6d} ({percentage:5.1f}%)")
    
    # Compare to expected reduction
    print("\n" + "=" * 80)
    print("CLEANUP EFFECTIVENESS")
    print("=" * 80)
    
    total_today = len(today_entries)
    range_start_count = events.get('RANGE_COMPUTE_START', 0)
    range_failed_count = events.get('RANGE_COMPUTE_FAILED', 0)
    total_problematic = range_start_count + range_failed_count
    
    if total_today > 0:
        problematic_percentage = (total_problematic / total_today * 100)
        print(f"Total entries today: {total_today:,}")
        print(f"RANGE_COMPUTE_START: {range_start_count:,}")
        print(f"RANGE_COMPUTE_FAILED: {range_failed_count:,}")
        print(f"Combined problematic: {total_problematic:,} ({problematic_percentage:.1f}% of total)")
        
        if problematic_percentage < 5:
            print("\n[SUCCESS] Cleanup working well! Problematic logs are < 5% of total")
        elif problematic_percentage < 20:
            print("\n[GOOD] Cleanup working! Problematic logs reduced significantly")
        else:
            print("\n[WARNING] Still seeing high percentage of problematic logs")
    
    # Verify critical logs still present
    print("\n" + "=" * 80)
    print("CRITICAL ERROR LOGS VERIFICATION")
    print("=" * 80)
    
    critical_events = {
        'RANGE_INVALIDATED': events.get('RANGE_INVALIDATED', 0),
        'RANGE_HYDRATION_ERROR': events.get('RANGE_HYDRATION_ERROR', 0),
        'INVARIANT_VIOLATION': sum(1 for e in today_entries if 'INVARIANT' in e.get('event', '')),
        'DATA_FEED_STALL': events.get('DATA_FEED_STALL', 0),
        'RANGE_COMPUTE_COMPLETE': events.get('RANGE_COMPUTE_COMPLETE', 0),
        'RANGE_LOCKED': sum(1 for e in today_entries if 'RANGE_LOCKED' in e.get('event', '')),
    }
    
    for event_name, count in critical_events.items():
        status = "[OK]" if count > 0 or event_name in ['RANGE_INVALIDATED', 'RANGE_HYDRATION_ERROR', 'INVARIANT_VIOLATION', 'DATA_FEED_STALL'] else "[MISSING]"
        print(f"  {status} {event_name}: {count}")

if __name__ == '__main__':
    main()
