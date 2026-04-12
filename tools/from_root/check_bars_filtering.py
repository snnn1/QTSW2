#!/usr/bin/env python3
"""Check why bars are being filtered out"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
events = []

for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                if line.strip():
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except:
        pass

print("="*80)
print("BARS FILTERING ANALYSIS:")
print("="*80)

# Check BARSREQUEST_FILTER_SUMMARY for MYM
mym_filter = [e for e in events 
             if e.get('ts_utc', '').startswith('2026-01-26') and
             e.get('event') == 'BARSREQUEST_FILTER_SUMMARY' and
             e.get('data', {}).get('instrument') == 'MYM']

print(f"\n  MYM BARSREQUEST_FILTER_SUMMARY events: {len(mym_filter)}")
for e in mym_filter[-5:]:  # Latest 5
    data = e.get('data', {})
    ts = e.get('ts_utc', '')[:19]
    print(f"    {ts}:")
    print(f"      Raw bars: {data.get('raw_bar_count', 'N/A')}")
    print(f"      Accepted bars: {data.get('accepted_bar_count', 'N/A')}")
    print(f"      Filtered future: {data.get('filtered_future_count', 'N/A')}")
    print(f"      Filtered partial: {data.get('filtered_partial_count', 'N/A')}")

# Check PRE_HYDRATION_BARS_LOADED for MYM
mym_loaded = [e for e in events 
             if e.get('ts_utc', '').startswith('2026-01-26') and
             e.get('event') == 'PRE_HYDRATION_BARS_LOADED' and
             e.get('data', {}).get('instrument') == 'MYM']

print(f"\n  MYM PRE_HYDRATION_BARS_LOADED events: {len(mym_loaded)}")
for e in mym_loaded[-5:]:  # Latest 5
    data = e.get('data', {})
    ts = e.get('ts_utc', '')[:19]
    print(f"    {ts}:")
    print(f"      Bar count: {data.get('bar_count', 'N/A')}")
    print(f"      Streams fed: {data.get('streams_fed', 'N/A')}")
    print(f"      Filtered future: {data.get('filtered_future', 'N/A')}")
    print(f"      Filtered partial: {data.get('filtered_partial', 'N/A')}")

# Check if streams are matching MYM
print(f"\n{'='*80}")
print("CHECKING STREAM MATCHING:")
print(f"{'='*80}")

# Check for events that show stream matching
for stream in ['YM1', 'YM2']:
    print(f"\n  {stream}:")
    
    # Check BAR_ADMISSION_PROOF to see what instrument bars are from
    admission = [e for e in events 
                if e.get('ts_utc', '').startswith('2026-01-26') and
                e.get('stream') == stream and
                e.get('event') == 'BAR_ADMISSION_PROOF']
    
    if admission:
        instruments = set()
        for e in admission:
            data = e.get('data', {})
            if isinstance(data, dict):
                inst = data.get('instrument', '')
                if inst:
                    instruments.add(inst)
        print(f"    Instruments in BAR_ADMISSION_PROOF: {', '.join(sorted(instruments))}")
    else:
        print(f"    No BAR_ADMISSION_PROOF events")
    
    # Check HYDRATION_SUMMARY
    hydration = [e for e in events 
                if e.get('ts_utc', '').startswith('2026-01-26') and
                e.get('stream') == stream and
                e.get('event') == 'HYDRATION_SUMMARY']
    
    if hydration:
        latest = hydration[-1]
        data = latest.get('data', {})
        print(f"    HYDRATION_SUMMARY:")
        print(f"      Instrument: {data.get('instrument', 'N/A')}")
        print(f"      Canonical: {data.get('canonical_instrument', 'N/A')}")
        print(f"      Loaded bars: {data.get('loaded_bars', 'N/A')}")

# Check MCL
print(f"\n{'='*80}")
print("MCL ANALYSIS:")
print(f"{'='*80}")

mcl_failed = [e for e in events 
             if e.get('ts_utc', '').startswith('2026-01-26') and
             e.get('event') == 'BARSREQUEST_FAILED' and
             e.get('data', {}).get('instrument') == 'MCL']

print(f"\n  MCL BARSREQUEST_FAILED: {len(mcl_failed)}")
if mcl_failed:
    latest = mcl_failed[-1]
    data = latest.get('data', {})
    ts = latest.get('ts_utc', '')[:19]
    print(f"    Latest ({ts}):")
    print(f"      Reason: {data.get('reason', 'N/A')}")
    print(f"      Error: {data.get('error', 'N/A')}")

mcl_skipped = [e for e in events 
              if e.get('ts_utc', '').startswith('2026-01-26') and
              e.get('event') == 'BARSREQUEST_SKIPPED' and
              e.get('data', {}).get('instrument') == 'MCL']

print(f"\n  MCL BARSREQUEST_SKIPPED: {len(mcl_skipped)}")
if mcl_skipped:
    latest = mcl_skipped[-1]
    data = latest.get('data', {})
    ts = latest.get('ts_utc', '')[:19]
    print(f"    Latest ({ts}):")
    print(f"      Reason: {data.get('reason', 'N/A')}")

print(f"\n{'='*80}")
