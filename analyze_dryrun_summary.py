#!/usr/bin/env python3
"""Analyze DRYRUN test logs and generate summary."""

import json
from collections import Counter
from datetime import datetime

def analyze_logs():
    log_file = "logs/robot/robot_skeleton.jsonl"
    
    events = []
    with open(log_file, 'r', encoding='utf-8') as f:
        for line_num, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                event = json.loads(line)
                events.append(event)
            except json.JSONDecodeError as e:
                print(f"Warning: Skipping invalid JSON on line {line_num}: {e}")
                continue
    
    print("=" * 60)
    print("DRYRUN TEST SUMMARY")
    print("=" * 60)
    print(f"\nTotal Events: {len(events):,}")
    
    # Event types
    event_types = Counter(e.get('event_type', 'UNKNOWN') for e in events)
    print(f"\nUnique Event Types: {len(event_types)}")
    print("\nTop 15 Event Types:")
    for et, count in event_types.most_common(15):
        print(f"  {et:40s} {count:6,}")
    
    # Trading dates
    trading_dates = sorted(set(e.get('trading_date', '') for e in events if e.get('trading_date')))
    print(f"\nTrading Days Processed: {len(trading_dates)}")
    if trading_dates:
        print(f"  First: {trading_dates[0]}")
        print(f"  Last:  {trading_dates[-1]}")
        if len(trading_dates) <= 10:
            print(f"  All: {', '.join(trading_dates)}")
        else:
            print(f"  Sample: {', '.join(trading_dates[:5])} ... {', '.join(trading_dates[-5:])}")
    
    # Streams
    streams = set(e.get('stream', '') for e in events if e.get('stream') and e.get('stream') != '__engine__')
    print(f"\nStreams Processed: {len(streams)}")
    if streams:
        print(f"  Stream names: {', '.join(sorted(streams)[:10])}")
    
    # State transitions
    state_transitions = [e for e in events if e.get('event_type') == 'STATE_TRANSITION']
    print(f"\nState Transitions: {len(state_transitions)}")
    if state_transitions:
        transitions = Counter()
        for e in state_transitions:
            data = e.get('data', {})
            if isinstance(data, dict):
                from_state = data.get('from', 'UNKNOWN')
                to_state = data.get('to', 'UNKNOWN')
                transitions[(from_state, to_state)] += 1
        print("  Top 10 Transitions:")
        for (f, t), count in transitions.most_common(10):
            print(f"    {f:20s} -> {t:20s} {count:4,}")
    
    # Key events
    key_events = {
        'ENGINE_START': 'Engine Started',
        'ENGINE_STOP': 'Engine Stopped',
        'PRE_HYDRATION_COMPLETE': 'Pre-Hydration Complete',
        'RANGE_LOCKED': 'Range Locked',
        'HYDRATION_SUMMARY': 'Hydration Summary',
        'RANGE_INTENT_ASSERT': 'Range Intent Assert',
        'TRADING_DAY_ROLLOVER': 'Trading Day Rollover',
    }
    print("\nKey Events:")
    for event_type, description in key_events.items():
        count = len([e for e in events if e.get('event_type') == event_type])
        print(f"  {description:30s} {count:6,}")
    
    # Bar events
    bar_events = [e for e in events if 'BAR' in e.get('event_type', '')]
    print(f"\nBar-Related Events: {len(bar_events):,}")
    if bar_events:
        bar_types = Counter(e.get('event_type', '') for e in bar_events)
        print("  Breakdown:")
        for et, count in bar_types.most_common(10):
            print(f"    {et:40s} {count:6,}")
    
    # Errors and warnings
    errors = [e for e in events if 'ERROR' in e.get('event_type', '') or 'INVARIANT' in e.get('event_type', '')]
    warnings = [e for e in events if 'WARNING' in e.get('event_type', '') or 'WARN' in e.get('event_type', '')]
    print(f"\nErrors/Invariants: {len(errors)}")
    print(f"Warnings: {len(warnings)}")
    
    # Time range
    timestamps = [e.get('ts_utc', '') for e in events if e.get('ts_utc')]
    if timestamps:
        try:
            first_ts = min(timestamps)
            last_ts = max(timestamps)
            print(f"\nTime Range:")
            print(f"  Start: {first_ts}")
            print(f"  End:   {last_ts}")
        except:
            pass
    
    # Instruments
    instruments = set(e.get('instrument', '') for e in events if e.get('instrument'))
    print(f"\nInstruments: {len(instruments)}")
    if instruments:
        print(f"  {', '.join(sorted(instruments))}")
    
    print("\n" + "=" * 60)
    print("SUMMARY COMPLETE")
    print("=" * 60)

if __name__ == '__main__':
    analyze_logs()
