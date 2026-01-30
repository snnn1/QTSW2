#!/usr/bin/env python3
"""
Check tick events used by watchdog for monitoring.
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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=6)
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("WATCHDOG TICK EVENTS CHECK")
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
    
    print(f"\nLoaded {len(events):,} events from last 6 hours\n")
    
    # Watchdog tick event types (from config.py)
    watchdog_tick_events = [
        'ENGINE_TICK_CALLSITE',      # Primary: Tick() call site (rate-limited to every 5s)
        'ENGINE_TICK_EXECUTED',      # Diagnostic: Tick() execution
        'ENGINE_TICK_BEFORE_LOCK',   # Diagnostic: before lock acquisition
        'ENGINE_TICK_LOCK_ACQUIRED', # Diagnostic: after lock acquired
        'ENGINE_TICK_AFTER_LOCK',    # Diagnostic: after lock released
        'ENGINE_TICK_HEARTBEAT',     # Bar-driven heartbeat
        'ENGINE_HEARTBEAT',          # Engine heartbeat (deprecated)
        'ENGINE_TICK_STALL_DETECTED',
        'ENGINE_TICK_STALL_RECOVERED'
    ]
    
    print("="*80)
    print("WATCHDOG TICK EVENT TYPES:")
    print("="*80)
    
    total_watchdog_ticks = 0
    for event_type in watchdog_tick_events:
        matching = [e for e in events if e.get('event') == event_type]
        count = len(matching)
        total_watchdog_ticks += count
        
        if matching:
            latest = max(matching, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                age_seconds = (datetime.now(timezone.utc) - ts).total_seconds()
                print(f"    {event_type:30}: {count:6} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age_seconds:.1f} sec ago)")
            else:
                print(f"    {event_type:30}: {count:6} events")
        else:
            print(f"    {event_type:30}: {count:6} events | NOT FOUND")
    
    print(f"\n  TOTAL WATCHDOG TICK EVENTS: {total_watchdog_ticks}")
    
    # Check ENGINE_TICK_CALLSITE specifically (primary watchdog signal)
    print("\n" + "="*80)
    print("ENGINE_TICK_CALLSITE (PRIMARY WATCHDOG SIGNAL):")
    print("="*80)
    
    tick_callsite = [e for e in events if e.get('event') == 'ENGINE_TICK_CALLSITE']
    
    print(f"\n  Total ENGINE_TICK_CALLSITE events: {len(tick_callsite)}")
    
    if tick_callsite:
        print(f"  [OK] Watchdog tick events found")
        
        # Check frequency
        times = sorted([parse_timestamp(e.get('ts_utc', '')) for e in tick_callsite if parse_timestamp(e.get('ts_utc', ''))])
        if len(times) > 1:
            gaps = [(times[i+1] - times[i]).total_seconds() for i in range(len(times)-1)]
            avg_gap = sum(gaps) / len(gaps) if gaps else 0
            min_gap = min(gaps) if gaps else 0
            max_gap = max(gaps) if gaps else 0
            
            print(f"\n  Frequency analysis:")
            print(f"    Average gap: {avg_gap:.1f} seconds")
            print(f"    Min gap: {min_gap:.1f} seconds")
            print(f"    Max gap: {max_gap:.1f} seconds")
            print(f"    Expected: ~5 seconds (rate-limited)")
        
        latest = max(tick_callsite, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age_seconds = (datetime.now(timezone.utc) - ts).total_seconds()
            print(f"\n  Latest event:")
            print(f"    Time: {ts_chicago.strftime('%H:%M:%S')} CT")
            print(f"    Age: {age_seconds:.1f} seconds ago")
            
            # Check against watchdog threshold (15 seconds)
            if age_seconds > 15:
                print(f"    [WARN] Age exceeds watchdog threshold (15 seconds)")
            else:
                print(f"    [OK] Age within watchdog threshold (15 seconds)")
        
        # Show recent events
        print(f"\n  Recent events (last 5):")
        for e in tick_callsite[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                print(f"    {ts_chicago.strftime('%H:%M:%S')} CT")
    else:
        print(f"  [WARN] No ENGINE_TICK_CALLSITE events found!")
        print(f"         Watchdog uses this as primary liveness signal")
        print(f"         This could mean:")
        print(f"         - Tick() not being called")
        print(f"         - ENGINE_TICK_CALLSITE not being logged")
        print(f"         - Events filtered out")
    
    # Check for stall detection
    print("\n" + "="*80)
    print("ENGINE TICK STALL DETECTION:")
    print("="*80)
    
    stall_detected = [e for e in events if e.get('event') == 'ENGINE_TICK_STALL_DETECTED']
    stall_recovered = [e for e in events if e.get('event') == 'ENGINE_TICK_STALL_RECOVERED']
    
    print(f"\n  ENGINE_TICK_STALL_DETECTED: {len(stall_detected)}")
    print(f"  ENGINE_TICK_STALL_RECOVERED: {len(stall_recovered)}")
    
    if stall_detected:
        print(f"\n  [WARN] Engine tick stalls detected!")
        for e in stall_detected[-3:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                print(f"    {ts_chicago.strftime('%H:%M:%S')} CT")
    else:
        print(f"\n  [OK] No engine tick stalls detected")

if __name__ == "__main__":
    main()
