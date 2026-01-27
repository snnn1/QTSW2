#!/usr/bin/env python3
"""Comprehensive pipeline diagnostic using binary truth events"""
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
print("THREE-PIPELINE DIAGNOSTIC CHECK")
print("="*80)

# Get today's events
today_events = [e for e in events if e.get('ts_utc', '').startswith('2026-01-26')]

# Read timetable to find disabled streams
disabled_streams = set()
tt_path = Path("data/timetable/timetable_current.json")
if tt_path.exists():
    try:
        tt = json.loads(tt_path.read_text())
        for s in tt.get('streams', []):
            if isinstance(s, dict) and not s.get('enabled', True):
                stream_id = s.get('stream', '')
                if stream_id:
                    disabled_streams.add(stream_id)
    except:
        pass

# Get all enabled streams from STREAMS_CREATED or from any stream events
enabled_streams = set()
streams_info = {}

# First try STREAMS_CREATED
for e in today_events:
    if e.get('event') == 'STREAMS_CREATED':
        data = e.get('data', {})
        if isinstance(data, dict):
            streams = data.get('streams', [])
            for s in streams:
                if isinstance(s, dict):
                    stream_id = s.get('stream_id', '')
                    if stream_id:
                        enabled_streams.add(stream_id)
                        streams_info[stream_id] = {
                            'instrument': s.get('instrument', 'N/A'),
                            'execution_instrument': s.get('execution_instrument', 'N/A'),
                            'canonical_instrument': s.get('canonical_instrument', 'N/A'),
                            'session': s.get('session', 'N/A'),
                            'slot_time': s.get('slot_time', 'N/A'),
                            'committed': s.get('committed', False)
                        }

# If no streams found, discover from stream events
# But exclude disabled streams from timetable
if len(enabled_streams) == 0:
    for e in today_events:
        stream_id = e.get('stream', '')
        if stream_id and stream_id != '__engine__' and stream_id not in enabled_streams and stream_id not in disabled_streams:
            enabled_streams.add(stream_id)
            # Try to get info from HYDRATION_SUMMARY or other events
            stream_events = [ev for ev in today_events if ev.get('stream') == stream_id]
            for se in stream_events:
                if se.get('event') == 'HYDRATION_SUMMARY':
                    data = se.get('data', {})
                    if isinstance(data, dict):
                        streams_info[stream_id] = {
                            'instrument': data.get('instrument', 'N/A'),
                            'execution_instrument': data.get('instrument', 'N/A'),
                            'canonical_instrument': data.get('canonical_instrument', 'N/A'),
                            'session': data.get('session', 'N/A'),
                            'slot_time': data.get('slot_time', 'N/A'),
                            'committed': False
                        }
                        break
            # If no info found, use defaults
            if stream_id not in streams_info:
                streams_info[stream_id] = {
                    'instrument': 'N/A',
                    'execution_instrument': 'N/A',
                    'canonical_instrument': 'N/A',
                    'session': 'N/A',
                    'slot_time': 'N/A',
                    'committed': False
                }

# Filter out disabled streams from timetable
enabled_streams = {s for s in enabled_streams if s not in disabled_streams}

print(f"\n  Enabled streams: {len(enabled_streams)}")
print(f"  Streams: {', '.join(sorted(enabled_streams))}")
if disabled_streams:
    print(f"  (Excluded disabled from timetable: {', '.join(sorted(disabled_streams))})")

# Get all execution instruments that need BarsRequest
execution_instruments = set()
for stream_id, info in streams_info.items():
    exec_inst = info.get('execution_instrument', '')
    if exec_inst and exec_inst != 'N/A':
        execution_instruments.add(exec_inst)

print(f"\n  Execution instruments needing BarsRequest: {', '.join(sorted(execution_instruments))}")

# Pipeline A: BarsRequest (Provider)
print(f"\n{'='*80}")
print("PIPELINE A: BARSREQUEST (Provider)")
print(f"{'='*80}")

pipeline_a_status = {}
for inst in sorted(execution_instruments):
    status = {
        'requested': False,
        'callback_received': False,
        'bars_count_received': 0,
        'first_close_time_utc': None,
        'last_close_time_utc': None,
        'error': None
    }
    
    # Check BARSREQUEST_CLOSE_TIME_BOUNDARIES (requested)
    boundaries = [e for e in today_events 
                 if e.get('event') == 'BARSREQUEST_CLOSE_TIME_BOUNDARIES' and
                 e.get('data', {}).get('instrument') == inst]
    if boundaries:
        status['requested'] = True
    
    # Check BARSREQUEST_CALLBACK_RECEIVED (binary truth event)
    callback_received = [e for e in today_events 
                        if e.get('event') == 'BARSREQUEST_CALLBACK_RECEIVED' and
                        e.get('data', {}).get('execution_instrument') == inst]
    if callback_received:
        latest = callback_received[-1]
        data = latest.get('data', {})
        status['callback_received'] = True
        status['bars_count_received'] = data.get('bars_count_received', 0)
        status['first_close_time_utc'] = data.get('first_close_time_utc')
        status['last_close_time_utc'] = data.get('last_close_time_utc')
        if data.get('exception'):
            status['error'] = data.get('exception')
        elif data.get('error_code') != 'NoError':
            status['error'] = f"{data.get('error_code')}: {data.get('error_message', '')}"
    
    pipeline_a_status[inst] = status
    
    print(f"\n  {inst}:")
    print(f"    Requested: {status['requested']}")
    print(f"    Callback received: {status['callback_received']}")
    if status['callback_received']:
        print(f"    Bars received: {status['bars_count_received']}")
        if status['first_close_time_utc']:
            print(f"    First bar: {status['first_close_time_utc']}")
        if status['last_close_time_utc']:
            print(f"    Last bar: {status['last_close_time_utc']}")
        if status['error']:
            print(f"    ERROR: {status['error']}")

# Pipeline B: Routing (Engine)
print(f"\n{'='*80}")
print("PIPELINE B: ROUTING (Engine)")
print(f"{'='*80}")

pipeline_b_status = {}
for inst in sorted(execution_instruments):
    status = {
        'loadprehydration_entered': False,
        'bars_count_input': 0,
        'streams_matched_count': 0,
        'canonical_of_instrument': None
    }
    
    # Check LOADPREHYDRATIONBARS_ENTERED (binary truth event)
    entered = [e for e in today_events 
              if e.get('event') == 'LOADPREHYDRATIONBARS_ENTERED' and
              e.get('data', {}).get('instrumentName') == inst]
    if entered:
        latest = entered[-1]
        data = latest.get('data', {})
        status['loadprehydration_entered'] = True
        status['bars_count_input'] = data.get('bars_count_input', 0)
        status['streams_matched_count'] = data.get('streams_matched_count', 0)
        status['canonical_of_instrument'] = data.get('canonical_of_instrument', 'N/A')
    
    pipeline_b_status[inst] = status
    
    print(f"\n  {inst}:")
    print(f"    LoadPreHydrationBars entered: {status['loadprehydration_entered']}")
    if status['loadprehydration_entered']:
        print(f"    Bars input: {status['bars_count_input']}")
        print(f"    Streams matched: {status['streams_matched_count']}")
        print(f"    Canonical: {status['canonical_of_instrument']}")
        if status['streams_matched_count'] == 0:
            print(f"    ⚠️  WARNING: No streams matched - identity problem!")

# Pipeline C: Admission/Buffer (State Machine)
print(f"\n{'='*80}")
print("PIPELINE C: ADMISSION/BUFFER (State Machine)")
print(f"{'='*80}")

pipeline_c_status = {}
for stream_id in sorted(enabled_streams):
    status = {
        'admission_proof': 0,
        'admission_to_commit_decision': 0,
        'buffer_add_attempt': 0,
        'buffer_add_committed': 0,
        'buffer_rejected': 0,
        'will_commit_false_count': 0,
        'will_commit_false_reasons': defaultdict(int)
    }
    
    stream_events = [e for e in today_events if e.get('stream') == stream_id]
    
    # Count events
    for e in stream_events:
        event_type = e.get('event', '')
        if event_type == 'BAR_ADMISSION_PROOF':
            status['admission_proof'] += 1
        elif event_type == 'BAR_ADMISSION_TO_COMMIT_DECISION':
            status['admission_to_commit_decision'] += 1
            data = e.get('data', {})
            if isinstance(data, dict) and not data.get('will_commit', True):
                status['will_commit_false_count'] += 1
                reason = data.get('reason', 'UNKNOWN')
                status['will_commit_false_reasons'][reason] += 1
        elif event_type == 'BAR_BUFFER_ADD_ATTEMPT':
            status['buffer_add_attempt'] += 1
        elif event_type == 'BAR_BUFFER_ADD_COMMITTED':
            status['buffer_add_committed'] += 1
        elif event_type == 'BAR_BUFFER_REJECTED':
            status['buffer_rejected'] += 1
    
    pipeline_c_status[stream_id] = status
    
    info = streams_info.get(stream_id, {})
    exec_inst = info.get('execution_instrument', 'N/A')
    canonical_inst = info.get('canonical_instrument', 'N/A')
    
    print(f"\n  {stream_id} ({exec_inst} -> {canonical_inst}):")
    print(f"    BAR_ADMISSION_PROOF: {status['admission_proof']}")
    print(f"    BAR_ADMISSION_TO_COMMIT_DECISION: {status['admission_to_commit_decision']}")
    print(f"    BAR_BUFFER_ADD_ATTEMPT: {status['buffer_add_attempt']}")
    print(f"    BAR_BUFFER_ADD_COMMITTED: {status['buffer_add_committed']}")
    print(f"    BAR_BUFFER_REJECTED: {status['buffer_rejected']}")
    
    if status['will_commit_false_count'] > 0:
        print(f"    ⚠️  WARNING: {status['will_commit_false_count']} bars blocked from commit:")
        for reason, count in sorted(status['will_commit_false_reasons'].items(), key=lambda x: x[1], reverse=True):
            print(f"      - {reason}: {count}")

# Range computation status
print(f"\n{'='*80}")
print("RANGE COMPUTATION STATUS")
print(f"{'='*80}")

for stream_id in sorted(enabled_streams):
    stream_events = [e for e in today_events if e.get('stream') == stream_id]
    
    range_init = [e for e in stream_events if e.get('event') == 'RANGE_INITIALIZED_FROM_HISTORY']
    range_locked = [e for e in stream_events if e.get('event') == 'RANGE_LOCKED_INCREMENTAL']
    range_failed = [e for e in stream_events if e.get('event') == 'RANGE_COMPUTE_FAILED']
    
    info = streams_info.get(stream_id, {})
    exec_inst = info.get('execution_instrument', 'N/A')
    canonical_inst = info.get('canonical_instrument', 'N/A')
    
    print(f"\n  {stream_id} ({exec_inst} -> {canonical_inst}):")
    print(f"    RANGE_INITIALIZED_FROM_HISTORY: {len(range_init)}")
    print(f"    RANGE_LOCKED_INCREMENTAL: {len(range_locked)}")
    print(f"    RANGE_COMPUTE_FAILED: {len(range_failed)}")
    
    if range_failed:
        latest = range_failed[-1]
        data = latest.get('data', {})
        print(f"      Reason: {data.get('reason', 'N/A')}")
        print(f"      Error: {data.get('error', 'N/A')}")
        if data.get('bar_count'):
            print(f"      Bar count: {data.get('bar_count')}")
    
    # Check hydration summary
    hydration = [e for e in stream_events if e.get('event') == 'HYDRATION_SUMMARY']
    if hydration:
        latest = hydration[-1]
        h_data = latest.get('data', {})
        loaded_bars = h_data.get('loaded_bars', 'N/A')
        committed_bars = pipeline_c_status.get(stream_id, {}).get('buffer_add_committed', 0)
        print(f"    HYDRATION_SUMMARY:")
        print(f"      Loaded bars: {loaded_bars}")
        print(f"      Committed bars: {committed_bars}")
        if isinstance(loaded_bars, (int, float)) and loaded_bars > 0 and committed_bars == 0:
            print(f"      ⚠️  WARNING: Bars loaded but not committed!")

# Summary: Pipeline breaks
print(f"\n{'='*80}")
print("PIPELINE BREAK SUMMARY")
print(f"{'='*80}")

for inst in sorted(execution_instruments):
    a_status = pipeline_a_status.get(inst, {})
    b_status = pipeline_b_status.get(inst, {})
    
    breaks = []
    if not a_status.get('requested'):
        breaks.append("A1: Not requested")
    elif not a_status.get('callback_received'):
        breaks.append("A2: Callback not received")
    elif a_status.get('bars_count_received', 0) == 0:
        breaks.append("A3: Zero bars received")
    
    if not b_status.get('loadprehydration_entered'):
        breaks.append("B1: LoadPreHydrationBars not entered")
    elif b_status.get('streams_matched_count', 0) == 0:
        breaks.append("B2: No streams matched (identity problem)")
    
    if breaks:
        print(f"\n  {inst}: {' | '.join(breaks)}")

for stream_id in sorted(enabled_streams):
    c_status = pipeline_c_status.get(stream_id, {})
    breaks = []
    
    if c_status.get('admission_proof', 0) > 0:
        if c_status.get('admission_to_commit_decision', 0) == 0:
            breaks.append("C1: Admission proof but no commit decision")
        elif c_status.get('buffer_add_attempt', 0) == 0:
            breaks.append("C2: Commit decision but no buffer attempt")
        elif c_status.get('buffer_add_committed', 0) == 0:
            breaks.append("C3: Buffer attempts but none committed")
    
    if breaks:
        info = streams_info.get(stream_id, {})
        exec_inst = info.get('execution_instrument', 'N/A')
        print(f"\n  {stream_id} ({exec_inst}): {' | '.join(breaks)}")

print(f"\n{'='*80}")
