#!/usr/bin/env python3
"""
Check why ES1 bars are being rejected and range isn't locking.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

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
    
    print("="*80)
    print("ES1 BAR REJECTION ANALYSIS")
    print("="*80)
    
    # Load ES1 events
    es1_events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            if e.get('stream') == 'ES1':
                                ts = parse_timestamp(e.get('ts_utc', ''))
                                if ts and ts >= cutoff:
                                    es1_events.append(e)
                        except:
                            pass
        except:
            pass
    
    es1_events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    print(f"\nLoaded {len(es1_events):,} ES1 events from last 6 hours\n")
    
    # Check bar rejection reasons
    print("="*80)
    print("BAR REJECTION ANALYSIS:")
    print("="*80)
    
    bar_rejected = [e for e in es1_events if e.get('event') == 'BAR_BUFFER_REJECTED']
    bar_admission_proof = [e for e in es1_events if e.get('event') == 'BAR_ADMISSION_PROOF']
    bar_admission_to_commit = [e for e in es1_events if e.get('event') == 'BAR_ADMISSION_TO_COMMIT_DECISION']
    
    print(f"\n  BAR_BUFFER_REJECTED: {len(bar_rejected)}")
    print(f"  BAR_ADMISSION_PROOF: {len(bar_admission_proof)}")
    print(f"  BAR_ADMISSION_TO_COMMIT_DECISION: {len(bar_admission_to_commit)}")
    
    # Analyze rejection reasons
    if bar_rejected:
        print(f"\n  Latest BAR_BUFFER_REJECTED events:")
        for e in bar_rejected[-10:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            data = e.get('data', {})
            if isinstance(data, dict):
                reason = data.get('reason', 'N/A')
                bar_time = data.get('bar_time', 'N/A')
                print(f"    {ts}: reason={reason}, bar_time={bar_time}")
    
    # Check bar admission proof for rejection reasons
    if bar_admission_proof:
        print(f"\n  Latest BAR_ADMISSION_PROOF events:")
        for e in bar_admission_proof[-10:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            data = e.get('data', {})
            if isinstance(data, dict):
                admitted = data.get('admitted', 'N/A')
                reason = data.get('reason', 'N/A')
                bar_time = data.get('bar_time', 'N/A')
                print(f"    {ts}: admitted={admitted}, reason={reason}, bar_time={bar_time}")
    
    # Check range building window
    print("\n" + "="*80)
    print("RANGE BUILDING WINDOW:")
    print("="*80)
    
    range_build_start = [e for e in es1_events if e.get('event') in ['RANGE_BUILD_START', 'RANGE_BUILDING_START']]
    
    if range_build_start:
        latest = range_build_start[-1]
        ts = latest.get('ts_utc', '')[:19] if len(latest.get('ts_utc', '')) > 19 else latest.get('ts_utc', '')
        print(f"\n  Latest RANGE_BUILD_START: {ts}")
        data = latest.get('data', {})
        if isinstance(data, dict):
            range_start_chicago = data.get('range_start_chicago', 'N/A')
            bar_count = data.get('bar_count', 'N/A')
            print(f"    Range Start (Chicago): {range_start_chicago}")
            print(f"    Bar Count at start: {bar_count}")
    
    # Check current bar buffer count
    print("\n" + "="*80)
    print("CURRENT BAR BUFFER STATUS:")
    print("="*80)
    
    bar_buffer_added = [e for e in es1_events if e.get('event') == 'BAR_BUFFER_ADD_COMMITTED']
    bar_buffer_count = [e for e in es1_events if 'BAR_BUFFER_COUNT' in e.get('event', '') or 'bar_count' in str(e.get('data', {})).lower()]
    
    print(f"\n  BAR_BUFFER_ADD_COMMITTED: {len(bar_buffer_added)}")
    
    if bar_buffer_added:
        latest = bar_buffer_added[-1]
        ts = latest.get('ts_utc', '')[:19] if len(latest.get('ts_utc', '')) > 19 else latest.get('ts_utc', '')
        print(f"    Latest bar added: {ts}")
    
    # Check for range window audit
    print("\n" + "="*80)
    print("RANGE WINDOW AUDIT:")
    print("="*80)
    
    range_window_audit = [e for e in es1_events if e.get('event') == 'RANGE_WINDOW_AUDIT']
    
    if range_window_audit:
        print(f"\n  Found {len(range_window_audit)} RANGE_WINDOW_AUDIT events:")
        for e in range_window_audit[-5:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            data = e.get('data', {})
            if isinstance(data, dict):
                print(f"    {ts}:")
                for key, value in data.items():
                    print(f"      {key}: {value}")
    else:
        print("\n  [INFO] No RANGE_WINDOW_AUDIT events found")
    
    # Check for time window issues
    print("\n" + "="*80)
    print("TIME WINDOW & BAR TIMING:")
    print("="*80)
    
    bar_time_mismatch = [e for e in es1_events if 'BAR_TIME' in e.get('event', '') and 'MISMATCH' in e.get('event', '')]
    bar_time_interpretation = [e for e in es1_events if 'BAR_TIME_INTERPRETATION' in e.get('event', '')]
    
    print(f"\n  BAR_TIME_MISMATCH: {len(bar_time_mismatch)}")
    print(f"  BAR_TIME_INTERPRETATION: {len(bar_time_interpretation)}")
    
    # Check session/slot time
    print("\n" + "="*80)
    print("SESSION & SLOT TIME:")
    print("="*80)
    
    session_start_time = [e for e in es1_events if e.get('event') == 'SESSION_START_TIME_SET']
    slot_time = [e for e in es1_events if 'SLOT_TIME' in str(e.get('data', {})).upper()]
    
    print(f"\n  SESSION_START_TIME_SET: {len(session_start_time)}")
    if session_start_time:
        latest = session_start_time[-1]
        ts = latest.get('ts_utc', '')[:19] if len(latest.get('ts_utc', '')) > 19 else latest.get('ts_utc', '')
        data = latest.get('data', {})
        if isinstance(data, dict):
            slot_time_val = data.get('slot_time', 'N/A')
            session = data.get('session', 'N/A')
            print(f"    Latest: {ts}, slot_time={slot_time_val}, session={session}")
    
    # Summary
    print("\n" + "="*80)
    print("SUMMARY & DIAGNOSIS:")
    print("="*80)
    
    issues = []
    
    if len(bar_buffer_added) < 30:
        issues.append(f"Very few bars in buffer ({len(bar_buffer_added)}) - range needs ~30-60 bars to lock")
    
    if len(bar_rejected) > len(bar_buffer_added) * 0.5:
        issues.append(f"High rejection rate ({len(bar_rejected)} rejected vs {len(bar_buffer_added)} added)")
    
    if not range_window_audit:
        issues.append("No RANGE_WINDOW_AUDIT events - may indicate range window not being checked")
    
    if issues:
        print("\n  [WARN] Potential issues:")
        for issue in issues:
            print(f"    - {issue}")
        
        print("\n  [DIAGNOSIS]")
        print("    ES1 is in RANGE_BUILDING state but hasn't locked because:")
        print("    1. Not enough bars accumulated in buffer (needs ~30-60 bars)")
        print("    2. Bars may be outside the range building window")
        print("    3. Bars may be rejected due to timing/validation issues")
        print("\n    Range will lock when:")
        print("    - Sufficient bars are accumulated")
        print("    - Range validation passes")
        print("    - Breakout levels are computed")
    else:
        print("\n  [OK] No obvious issues - range building in progress")

if __name__ == "__main__":
    main()
