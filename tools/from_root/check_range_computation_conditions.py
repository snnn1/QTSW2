#!/usr/bin/env python3
"""Check why ranges aren't computing for some streams"""
import json
from pathlib import Path
from datetime import datetime
from collections import defaultdict

log_dir = Path("logs/robot")
events = []

# Read all robot log files
for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except Exception as e:
        pass

# Focus on streams without ranges
streams_without_ranges = ['CL2', 'YM2']

print("="*80)
print("INVESTIGATING STREAMS WITHOUT RANGES:")
print("="*80)

for stream in streams_without_ranges:
    print(f"\n  Stream: {stream}")
    print(f"  {'-'*76}")
    
    # Get all events for this stream
    stream_events = [e for e in events 
                     if e.get('ts_utc', '').startswith('2026-01-26') and 
                     e.get('stream') == stream]
    
    # Check for hydration summary
    hydration_events = [e for e in stream_events if e.get('event') == 'HYDRATION_SUMMARY']
    if hydration_events:
        latest_hydration = hydration_events[-1]
        h_data = latest_hydration.get('data', {})
        if isinstance(h_data, dict):
            expected = h_data.get('expected_bars', 'N/A')
            loaded = h_data.get('loaded_bars', 'N/A')
            completeness = h_data.get('completeness_pct', 'N/A')
            print(f"    Hydration: {loaded}/{expected} bars ({completeness}%)")
            
            # Check if range was computed in hydration
            range_high = h_data.get('range_high')
            range_low = h_data.get('range_low')
            if range_high is None or range_low is None:
                print(f"    Range NOT computed in hydration summary")
            else:
                print(f"    Range computed: High={range_high}, Low={range_low}")
    
    # Check for bars committed
    bars_committed = [e for e in stream_events if e.get('event') == 'BAR_BUFFER_ADD_COMMITTED']
    print(f"    Bars committed: {len(bars_committed)}")
    
    # Check for range initialization events
    range_init = [e for e in stream_events if e.get('event') == 'RANGE_INITIALIZED_FROM_HISTORY']
    print(f"    RANGE_INITIALIZED_FROM_HISTORY events: {len(range_init)}")
    
    # Check for range locked events
    range_locked = [e for e in stream_events if e.get('event') in ['RANGE_LOCKED_INCREMENTAL', 'RANGE_LOCK_SNAPSHOT']]
    print(f"    RANGE_LOCKED events: {len(range_locked)}")
    
    # Check for errors
    errors = [e for e in stream_events if 'ERROR' in e.get('event', '') or 'FAILED' in e.get('event', '')]
    if errors:
        print(f"    Errors found: {len(errors)}")
        for err in errors[:3]:  # Show first 3
            print(f"      - {err.get('event')}: {err.get('data', {}).get('error', 'N/A') if isinstance(err.get('data'), dict) else 'N/A'}")
    
    # Check for BarsRequest events
    barsrequest = [e for e in stream_events if 'BARSREQUEST' in e.get('event', '')]
    if barsrequest:
        print(f"    BarsRequest events: {len(barsrequest)}")
        for br in barsrequest[:3]:  # Show first 3
            br_data = br.get('data', {})
            if isinstance(br_data, dict):
                reason = br_data.get('reason', 'N/A')
                print(f"      - {br.get('event')}: {reason}")
    
    # Check latest event types
    if stream_events:
        latest_events = sorted(stream_events, key=lambda x: x.get('ts_utc', ''), reverse=True)[:5]
        print(f"    Latest events:")
        for e in latest_events:
            print(f"      - {e.get('event')} at {e.get('ts_utc', '')[:19]}")

print(f"\n{'='*80}")
print("COMPARING WITH STREAMS THAT HAVE RANGES:")
print(f"{'='*80}")

# Compare with NQ2 which has a range
nq2_events = [e for e in events 
              if e.get('ts_utc', '').startswith('2026-01-26') and 
              e.get('stream') == 'NQ2']

nq2_hydration = [e for e in nq2_events if e.get('event') == 'HYDRATION_SUMMARY']
nq2_range_init = [e for e in nq2_events if e.get('event') == 'RANGE_INITIALIZED_FROM_HISTORY']
nq2_bars = [e for e in nq2_events if e.get('event') == 'BAR_BUFFER_ADD_COMMITTED']

print(f"\n  NQ2 (has range):")
print(f"    Hydration events: {len(nq2_hydration)}")
if nq2_hydration:
    latest = nq2_hydration[-1]
    h_data = latest.get('data', {})
    if isinstance(h_data, dict):
        print(f"    Latest hydration: {h_data.get('loaded_bars', 'N/A')}/{h_data.get('expected_bars', 'N/A')} bars")
print(f"    RANGE_INITIALIZED events: {len(nq2_range_init)}")
print(f"    Bars committed: {len(nq2_bars)}")

print(f"\n{'='*80}")
