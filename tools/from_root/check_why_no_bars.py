#!/usr/bin/env python3
"""Check why CL2 and YM2 have no bars"""
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

# Focus on CL2 and YM2
for stream in ['CL2', 'YM2']:
    print("="*80)
    print(f"INVESTIGATING {stream}:")
    print("="*80)
    
    stream_events = [e for e in events 
                    if e.get('ts_utc', '').startswith('2026-01-26') and 
                    e.get('stream') == stream]
    
    # Check BarsRequest events
    barsrequest_events = [e for e in stream_events if 'BARSREQUEST' in e.get('event', '')]
    print(f"\n  BarsRequest events: {len(barsrequest_events)}")
    for br in barsrequest_events:
        br_data = br.get('data', {})
        if isinstance(br_data, dict):
            reason = br_data.get('reason', 'N/A')
            event_type = br.get('event', '')
            print(f"    - {event_type}: {reason}")
    
    # Check bar admission events
    bar_admission = [e for e in stream_events if e.get('event') == 'BAR_ADMISSION_PROOF']
    print(f"\n  BAR_ADMISSION_PROOF events: {len(bar_admission)}")
    
    # Check bar buffer events
    bar_buffer_attempt = [e for e in stream_events if e.get('event') == 'BAR_BUFFER_ADD_ATTEMPT']
    bar_buffer_rejected = [e for e in stream_events if e.get('event') == 'BAR_BUFFER_REJECTED']
    bar_buffer_committed = [e for e in stream_events if e.get('event') == 'BAR_BUFFER_ADD_COMMITTED']
    
    print(f"  BAR_BUFFER_ADD_ATTEMPT: {len(bar_buffer_attempt)}")
    print(f"  BAR_BUFFER_REJECTED: {len(bar_buffer_rejected)}")
    print(f"  BAR_BUFFER_ADD_COMMITTED: {len(bar_buffer_committed)}")
    
    # Check rejection reasons
    if bar_buffer_rejected:
        rejection_reasons = defaultdict(int)
        for e in bar_buffer_rejected:
            e_data = e.get('data', {})
            if isinstance(e_data, dict):
                reason = e_data.get('rejection_reason', 'N/A')
                rejection_reasons[reason] += 1
        print(f"\n  Rejection reasons:")
        for reason, count in sorted(rejection_reasons.items(), key=lambda x: x[1], reverse=True):
            print(f"    - {reason}: {count}")
    
    # Check for live bars
    live_bars = [e for e in stream_events if e.get('event') == 'BAR_RECEIVED_DIAGNOSTIC']
    print(f"\n  BAR_RECEIVED_DIAGNOSTIC (live bars): {len(live_bars)}")
    
    # Check instrument mapping
    hydration = [e for e in stream_events if e.get('event') == 'HYDRATION_SUMMARY']
    if hydration:
        h_data = hydration[-1].get('data', {})
        if isinstance(h_data, dict):
            canonical = h_data.get('canonical_instrument', 'N/A')
            instrument = h_data.get('instrument', 'N/A')
            print(f"\n  Instrument mapping:")
            print(f"    Execution instrument: {instrument}")
            print(f"    Canonical instrument: {canonical}")
    
    # Check for errors
    errors = [e for e in stream_events if 'ERROR' in e.get('event', '') or 'FAILED' in e.get('event', '')]
    if errors:
        print(f"\n  Errors: {len(errors)}")
        for err in errors[:5]:
            err_data = err.get('data', {})
            if isinstance(err_data, dict):
                error_msg = err_data.get('error', err_data.get('reason', 'N/A'))
                print(f"    - {err.get('event')}: {error_msg}")

print(f"\n{'='*80}")
