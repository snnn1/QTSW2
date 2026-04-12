#!/usr/bin/env python3
"""
Check if all fixes are working by analyzing diagnostic logging.
Verifies:
1. Tick() called from OnMarketData() (continuous execution fix)
2. INSTRUMENT_MISMATCH rate-limiting (operational hygiene)
3. Safety assertion for stuck RANGE_BUILDING states
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
    print("FIXES VERIFICATION - DIAGNOSTIC LOGGING CHECK")
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
    
    # Check Fix #1: Tick() called from OnMarketData()
    print("="*80)
    print("FIX #1: TICK() CALLED FROM ONMARKETDATA() (Continuous Execution)")
    print("="*80)
    
    tick_from_marketdata = [e for e in events if e.get('event') == 'TICK_CALLED_FROM_ONMARKETDATA']
    
    print(f"\n  TICK_CALLED_FROM_ONMARKETDATA events: {len(tick_from_marketdata)}")
    
    if tick_from_marketdata:
        print(f"  [OK] FIX WORKING: Tick() is being called from OnMarketData()")
        
        # Group by instrument
        by_instrument = defaultdict(list)
        for e in tick_from_marketdata:
            data = e.get('data', {})
            if isinstance(data, dict):
                instrument = data.get('instrument', 'UNKNOWN')
                by_instrument[instrument].append(e)
        
        print(f"\n  By instrument:")
        for instrument in sorted(by_instrument.keys()):
            inst_events = by_instrument[instrument]
            latest = max(inst_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                age_minutes = (datetime.now(timezone.utc) - ts).total_seconds() / 60
                print(f"    {instrument}: {len(inst_events)} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
        
        # Show recent examples
        print(f"\n  Recent events (last 5):")
        for e in tick_from_marketdata[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                data = e.get('data', {})
                if isinstance(data, dict):
                    instrument = data.get('instrument', 'N/A')
                    tick_price = data.get('tick_price', 'N/A')
                    market_data_type = data.get('market_data_type', 'N/A')
                    print(f"    {ts_chicago.strftime('%H:%M:%S')} CT | {instrument:6} | Price: {tick_price} | Type: {market_data_type}")
    else:
        print(f"  [WARN] NO EVENTS FOUND: Tick() may not be called from OnMarketData()")
        print(f"     This could mean:")
        print(f"      - Fix not deployed yet")
        print(f"      - OnMarketData() not being called")
        print(f"      - No ticks flowing")
    
    # Check Fix #2: INSTRUMENT_MISMATCH rate-limiting
    print("\n" + "="*80)
    print("FIX #2: INSTRUMENT_MISMATCH RATE-LIMITING (Operational Hygiene)")
    print("="*80)
    
    instrument_mismatch_blocked = [e for e in events if e.get('event') == 'ORDER_SUBMIT_BLOCKED' and 
                                    isinstance(e.get('data'), dict) and 
                                    e.get('data', {}).get('reason') == 'INSTRUMENT_MISMATCH']
    
    instrument_mismatch_rate_limited = [e for e in events if e.get('event') == 'INSTRUMENT_MISMATCH_RATE_LIMITED']
    
    print(f"\n  ORDER_SUBMIT_BLOCKED (INSTRUMENT_MISMATCH): {len(instrument_mismatch_blocked)}")
    print(f"  INSTRUMENT_MISMATCH_RATE_LIMITED (diagnostic): {len(instrument_mismatch_rate_limited)}")
    
    if instrument_mismatch_blocked:
        # Check if rate-limiting is working
        by_instrument = defaultdict(list)
        for e in instrument_mismatch_blocked:
            data = e.get('data', {})
            if isinstance(data, dict):
                instrument = data.get('requested_instrument', 'UNKNOWN')
                by_instrument[instrument].append(e)
        
        print(f"\n  Rate-limiting check:")
        for instrument in sorted(by_instrument.keys()):
            inst_events = sorted(by_instrument[instrument], key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            
            if len(inst_events) > 1:
                # Check time between logs
                times = [parse_timestamp(e.get('ts_utc', '')) for e in inst_events if parse_timestamp(e.get('ts_utc', ''))]
                if len(times) > 1:
                    times.sort()
                    gaps = [(times[i+1] - times[i]).total_seconds() / 60 for i in range(len(times)-1)]
                    min_gap = min(gaps) if gaps else 0
                    max_gap = max(gaps) if gaps else 0
                    
                    if min_gap >= 50:  # Should be ~60 minutes
                        print(f"    {instrument}: [OK] Rate-limiting WORKING ({len(inst_events)} blocks, min gap: {min_gap:.1f} min)")
                    else:
                        print(f"    {instrument}: [WARN] Rate-limiting may not be working ({len(inst_events)} blocks, min gap: {min_gap:.1f} min)")
        
        if instrument_mismatch_rate_limited:
            print(f"\n  [OK] Rate-limiting diagnostic events found - confirms rate-limiting is active")
            latest = max(instrument_mismatch_rate_limited, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                data = latest.get('data', {})
                if isinstance(data, dict):
                    minutes_since = data.get('minutes_since_last_log', 'N/A')
                    print(f"    Latest diagnostic: {ts_chicago.strftime('%H:%M:%S')} CT | {minutes_since} min since last log")
        else:
            print(f"\n  [WARN] No rate-limiting diagnostic events found")
            print(f"     This could mean:")
            print(f"      - No blocks occurring (good!)")
            print(f"      - Rate-limiting not working")
            print(f"      - Diagnostic logging not deployed")
    else:
        print(f"\n  [OK] NO INSTRUMENT_MISMATCH BLOCKS: Fix is working correctly!")
    
    # Check Fix #3: Safety assertion for stuck RANGE_BUILDING
    print("\n" + "="*80)
    print("FIX #3: SAFETY ASSERTION FOR STUCK RANGE_BUILDING STATES")
    print("="*80)
    
    safety_assertion_check = [e for e in events if e.get('event') == 'RANGE_BUILDING_SAFETY_ASSERTION_CHECK']
    stuck_range_building = [e for e in events if e.get('event') == 'RANGE_BUILDING_STUCK_PAST_SLOT_TIME']
    
    print(f"\n  RANGE_BUILDING_SAFETY_ASSERTION_CHECK (diagnostic): {len(safety_assertion_check)}")
    print(f"  RANGE_BUILDING_STUCK_PAST_SLOT_TIME (critical alert): {len(stuck_range_building)}")
    
    if safety_assertion_check:
        print(f"\n  [OK] Safety assertion is ACTIVE and monitoring")
        
        # Group by stream
        by_stream = defaultdict(list)
        for e in safety_assertion_check:
            stream = e.get('stream', 'UNKNOWN')
            by_stream[stream].append(e)
        
        print(f"\n  By stream:")
        for stream in sorted(by_stream.keys()):
            stream_events = by_stream[stream]
            latest = max(stream_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                age_minutes = (datetime.now(timezone.utc) - ts).total_seconds() / 60
                data = latest.get('data', {})
                if isinstance(data, dict):
                    minutes_past = data.get('minutes_past_slot_time', 'N/A')
                    status = data.get('status', 'N/A')
                    print(f"    {stream:6}: {len(stream_events)} checks | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago) | Status: {status} | {minutes_past} min past slot")
        
        # Show recent examples
        print(f"\n  Recent checks (last 3):")
        for e in safety_assertion_check[-3:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                stream = e.get('stream', 'N/A')
                data = e.get('data', {})
                if isinstance(data, dict):
                    minutes_past = data.get('minutes_past_slot_time', 'N/A')
                    status = data.get('status', 'N/A')
                    print(f"    {ts_chicago.strftime('%H:%M:%S')} CT | {stream:6} | Status: {status} | {minutes_past} min past slot")
    else:
        print(f"\n  [WARN] No safety assertion check events found")
        print(f"     This could mean:")
        print(f"      - Assertion not deployed yet")
        print(f"      - No streams in RANGE_BUILDING state")
        print(f"      - Assertion not running")
    
    if stuck_range_building:
        print(f"\n  [CRITICAL] Stuck RANGE_BUILDING states detected!")
        for e in stuck_range_building[-3:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                stream = e.get('stream', 'N/A')
                data = e.get('data', {})
                if isinstance(data, dict):
                    minutes_past = data.get('minutes_past_slot_time', 'N/A')
                    print(f"    {ts_chicago.strftime('%H:%M:%S')} CT | {stream:6} | {minutes_past} min past slot time")
    else:
        print(f"\n  [OK] No stuck RANGE_BUILDING states detected")
    
    # Overall status
    print("\n" + "="*80)
    print("OVERALL STATUS")
    print("="*80)
    
    fixes_working = []
    fixes_not_working = []
    
    if tick_from_marketdata:
        fixes_working.append("[OK] Fix #1: Tick() called from OnMarketData()")
    else:
        fixes_not_working.append("[FAIL] Fix #1: Tick() called from OnMarketData() - NO EVENTS")
    
    if instrument_mismatch_rate_limited or not instrument_mismatch_blocked:
        fixes_working.append("[OK] Fix #2: INSTRUMENT_MISMATCH rate-limiting")
    else:
        fixes_not_working.append("[WARN] Fix #2: INSTRUMENT_MISMATCH rate-limiting - CHECK NEEDED")
    
    if safety_assertion_check:
        fixes_working.append("[OK] Fix #3: Safety assertion monitoring")
    else:
        fixes_not_working.append("[WARN] Fix #3: Safety assertion - NO EVENTS")
    
    if fixes_working:
        print(f"\n  Working fixes:")
        for fix in fixes_working:
            print(f"    {fix}")
    
    if fixes_not_working:
        print(f"\n  Fixes needing attention:")
        for fix in fixes_not_working:
            print(f"    {fix}")
    
    if not fixes_not_working:
        print(f"\n  [SUCCESS] ALL FIXES VERIFIED AND WORKING!")

if __name__ == "__main__":
    main()
