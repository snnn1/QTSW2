#!/usr/bin/env python3
"""Comprehensive diagnostic for streams without ranges"""
import json
from pathlib import Path
from collections import defaultdict
from datetime import datetime

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

print("="*80)
print("COMPREHENSIVE DIAGNOSTIC: STREAMS WITHOUT RANGES")
print("="*80)

# Get all streams and their status
all_streams = set()
for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        stream = e.get('stream', '')
        if stream and stream != '__engine__':
            all_streams.add(stream)

# Check which streams have ranges
streams_with_ranges = set()
for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        if e.get('event') == 'RANGE_INITIALIZED_FROM_HISTORY':
            stream = e.get('stream', '')
            if stream:
                streams_with_ranges.add(stream)
        elif e.get('event') == 'RANGE_LOCKED_INCREMENTAL':
            stream = e.get('stream', '')
            if stream:
                streams_with_ranges.add(stream)

streams_without_ranges = sorted(all_streams - streams_with_ranges)

print(f"\n  Total streams: {len(all_streams)}")
print(f"  Streams with ranges: {len(streams_with_ranges)}")
print(f"  Streams without ranges: {len(streams_without_ranges)}")
print(f"\n  Streams without ranges: {', '.join(streams_without_ranges)}")

# Check BarsRequest at engine level
print(f"\n{'='*80}")
print("BARSREQUEST STATUS (Engine Level):")
print(f"{'='*80}")

barsrequest_by_instrument = defaultdict(lambda: {'requested': 0, 'executed': 0, 'failed': 0, 'skipped': 0, 'bars_returned': 0})
barsrequest_loaded = defaultdict(int)

for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        event_type = e.get('event', '')
        data = e.get('data', {})
        
        if isinstance(data, dict):
            instrument = data.get('instrument', '')
            
            if event_type == 'BARSREQUEST_REQUESTED':
                barsrequest_by_instrument[instrument]['requested'] += 1
            elif event_type == 'BARSREQUEST_EXECUTED':
                barsrequest_by_instrument[instrument]['executed'] += 1
                bars_returned = data.get('bars_returned', 0)
                try:
                    barsrequest_by_instrument[instrument]['bars_returned'] += int(bars_returned)
                except (ValueError, TypeError):
                    pass
            elif event_type == 'BARSREQUEST_FAILED':
                barsrequest_by_instrument[instrument]['failed'] += 1
            elif event_type == 'BARSREQUEST_SKIPPED':
                barsrequest_by_instrument[instrument]['skipped'] += 1
            elif event_type == 'PRE_HYDRATION_BARS_LOADED':
                inst = data.get('instrument', '')
                if inst:
                    barsrequest_loaded[inst] += data.get('bar_count', 0)

print(f"\n  BarsRequest by instrument:")
for inst in sorted(barsrequest_by_instrument.keys()):
    stats = barsrequest_by_instrument[inst]
    bars_loaded = barsrequest_loaded.get(inst, 0)
    print(f"    {inst}:")
    print(f"      Requested: {stats['requested']}, Executed: {stats['executed']}, Failed: {stats['failed']}, Skipped: {stats['skipped']}")
    print(f"      Bars returned: {stats['bars_returned']}, Bars loaded: {bars_loaded}")

# Detailed analysis for each stream without range
print(f"\n{'='*80}")
print("DETAILED ANALYSIS FOR STREAMS WITHOUT RANGES:")
print(f"{'='*80}")

for stream in streams_without_ranges:
    print(f"\n  Stream: {stream}")
    print(f"  {'-'*76}")
    
    stream_events = [e for e in events 
                    if e.get('ts_utc', '').startswith('2026-01-26') and 
                    e.get('stream') == stream]
    
    # Get execution and canonical instruments
    exec_inst = None
    canonical_inst = None
    for e in stream_events:
        if e.get('event') == 'HYDRATION_SUMMARY':
            data = e.get('data', {})
            if isinstance(data, dict):
                exec_inst = data.get('instrument', '')
                canonical_inst = data.get('canonical_instrument', '')
                break
    
    print(f"    Execution Instrument: {exec_inst or 'N/A'}")
    print(f"    Canonical Instrument: {canonical_inst or 'N/A'}")
    
    # Check if BarsRequest was called for this instrument
    if exec_inst:
        inst_stats = barsrequest_by_instrument.get(exec_inst, {})
        bars_loaded = barsrequest_loaded.get(exec_inst, 0)
        print(f"    BarsRequest for {exec_inst}:")
        print(f"      Requested: {inst_stats.get('requested', 0)}")
        print(f"      Executed: {inst_stats.get('executed', 0)}")
        print(f"      Bars returned: {inst_stats.get('bars_returned', 0)}")
        print(f"      Bars loaded: {bars_loaded}")
        
        # Check if micro future should be requested
        if exec_inst in ['YM', 'CL']:
            micro_map = {'YM': 'MYM', 'CL': 'MCL'}
            micro_inst = micro_map.get(exec_inst)
            if micro_inst:
                micro_stats = barsrequest_by_instrument.get(micro_inst, {})
                print(f"    BarsRequest for {micro_inst} (micro future):")
                print(f"      Requested: {micro_stats.get('requested', 0)}")
                print(f"      Executed: {micro_stats.get('executed', 0)}")
                print(f"      Bars returned: {micro_stats.get('bars_returned', 0)}")
    
    # Bar buffer status
    attempts = len([e for e in stream_events if e.get('event') == 'BAR_BUFFER_ADD_ATTEMPT'])
    rejected = len([e for e in stream_events if e.get('event') == 'BAR_BUFFER_REJECTED'])
    committed = len([e for e in stream_events if e.get('event') == 'BAR_BUFFER_ADD_COMMITTED'])
    
    print(f"    Bar Buffer:")
    print(f"      Attempts: {attempts}")
    print(f"      Rejected: {rejected}")
    print(f"      Committed: {committed}")
    
    # Check rejection reasons
    if rejected > 0:
        rejection_reasons = defaultdict(int)
        for e in stream_events:
            if e.get('event') == 'BAR_BUFFER_REJECTED':
                data = e.get('data', {})
                if isinstance(data, dict):
                    reason = data.get('rejection_reason', 'N/A')
                    rejection_reasons[reason] += 1
        print(f"      Rejection reasons:")
        for reason, count in sorted(rejection_reasons.items(), key=lambda x: x[1], reverse=True):
            print(f"        - {reason}: {count}")
    
    # Hydration summary
    hydration = [e for e in stream_events if e.get('event') == 'HYDRATION_SUMMARY']
    if hydration:
        latest_h = hydration[-1]
        h_data = latest_h.get('data', {})
        print(f"    Hydration Summary:")
        print(f"      Loaded bars: {h_data.get('loaded_bars', 'N/A')}")
        print(f"      Expected bars: {h_data.get('expected_bars', 'N/A')}")
        completeness = h_data.get('completeness_pct', 'N/A')
        if isinstance(completeness, (int, float)):
            print(f"      Completeness: {completeness:.1f}%")
        else:
            print(f"      Completeness: {completeness}")
        print(f"      Range high: {h_data.get('range_high', 'N/A')}")
        print(f"      Range low: {h_data.get('range_low', 'N/A')}")
    
    # Check for range computation attempts
    range_init = [e for e in stream_events if e.get('event') == 'RANGE_INITIALIZED_FROM_HISTORY']
    range_locked = [e for e in stream_events if e.get('event') == 'RANGE_LOCKED_INCREMENTAL']
    print(f"    Range Events:")
    print(f"      RANGE_INITIALIZED_FROM_HISTORY: {len(range_init)}")
    print(f"      RANGE_LOCKED_INCREMENTAL: {len(range_locked)}")
    
    # Check state transitions
    states = defaultdict(int)
    for e in stream_events:
        state = e.get('state', '')
        if state:
            states[state] += 1
    
    if states:
        print(f"    States observed:")
        for state, count in sorted(states.items(), key=lambda x: x[1], reverse=True):
            print(f"      {state}: {count} events")
    
    # Check for errors
    errors = [e for e in stream_events 
             if 'ERROR' in e.get('event', '') or 
             'FAILED' in e.get('event', '') or
             'SKIPPED' in e.get('event', '')]
    
    if errors:
        print(f"    Errors/Warnings ({len(errors)}):")
        error_types = defaultdict(int)
        for e in errors:
            error_types[e.get('event', 'UNKNOWN')] += 1
        for err_type, count in sorted(error_types.items(), key=lambda x: x[1], reverse=True)[:5]:
            print(f"      {err_type}: {count}")
    
    # Check latest events
    latest_events = sorted(stream_events, key=lambda x: x.get('ts_utc', ''), reverse=True)[:5]
    print(f"    Latest events:")
    for e in latest_events:
        print(f"      {e.get('ts_utc', '')[:19]} - {e.get('event', 'N/A')} - {e.get('state', 'N/A')}")

print(f"\n{'='*80}")
