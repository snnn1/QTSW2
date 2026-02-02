#!/usr/bin/env python3
"""
Check data stall status - verify if bars are arriving and if streams expect them.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
import pytz

def parse_timestamp(ts_str: str):
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
    cutoff = datetime.now(timezone.utc) - timedelta(minutes=30)
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("DATA STALL ANALYSIS")
    print("="*80)
    
    # Load events
    events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True)[:3]:
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
    
    print(f"\nLoaded {len(events):,} events from last 30 minutes\n")
    
    # 1. Check bar events
    print("="*80)
    print("1. BAR EVENTS")
    print("="*80)
    
    bar_received = [e for e in events if e.get('event') == 'BAR_RECEIVED_NO_STREAMS']
    bar_accepted = [e for e in events if e.get('event') == 'BAR_ACCEPTED']
    bar_rejected = [e for e in events if 'BAR' in e.get('event', '') and 'REJECT' in e.get('event', '')]
    data_stall = [e for e in events if e.get('event') == 'DATA_STALL_RECOVERED']
    data_loss = [e for e in events if e.get('event') == 'DATA_LOSS_DETECTED']
    
    print(f"  BAR_RECEIVED_NO_STREAMS: {len(bar_received)}")
    print(f"  BAR_ACCEPTED: {len(bar_accepted)}")
    print(f"  BAR_REJECTED: {len(bar_rejected)}")
    print(f"  DATA_STALL_RECOVERED: {len(data_stall)}")
    print(f"  DATA_LOSS_DETECTED: {len(data_loss)}")
    
    # Check latest bar received
    if bar_received:
        latest = max(bar_received, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            instrument = latest.get('instrument', 'N/A')
            print(f"\n  Latest BAR_RECEIVED_NO_STREAMS:")
            print(f"    Time: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            print(f"    Instrument: {instrument}")
            if age > 300:  # 5 minutes
                print(f"    [WARN] No bars received for {age:.0f}s - possible data stall")
            else:
                print(f"    [OK] Bars arriving normally")
    
    # Check latest bar accepted
    if bar_accepted:
        latest = max(bar_accepted, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            instrument = latest.get('instrument', 'N/A')
            print(f"\n  Latest BAR_ACCEPTED:")
            print(f"    Time: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            print(f"    Instrument: {instrument}")
    
    # 2. Check stream states
    print("\n" + "="*80)
    print("2. STREAM STATES")
    print("="*80)
    
    stream_transitions = [e for e in events if e.get('event') == 'STREAM_STATE_TRANSITION']
    streams_by_state = {}
    
    for e in stream_transitions:
        data = e.get('data', {})
        new_state = data.get('new_state', 'UNKNOWN')
        stream = e.get('stream', 'UNKNOWN')
        if new_state not in streams_by_state:
            streams_by_state[new_state] = []
        if stream not in streams_by_state[new_state]:
            streams_by_state[new_state].append(stream)
    
    print(f"  Stream state transitions: {len(stream_transitions)}")
    print(f"\n  Streams by state:")
    bar_dependent_states = ['PRE_HYDRATION', 'ARMED', 'RANGE_BUILDING', 'RANGE_LOCKED']
    for state in sorted(streams_by_state.keys()):
        count = len(streams_by_state[state])
        is_bar_dependent = state in bar_dependent_states
        marker = "[EXPECTS BARS]" if is_bar_dependent else ""
        print(f"    {state:20} {count:3} streams {marker}")
    
    # Check if any streams are in bar-dependent states
    streams_expecting_bars = sum(len(streams_by_state.get(state, [])) for state in bar_dependent_states)
    print(f"\n  Streams expecting bars: {streams_expecting_bars}")
    
    # 3. Check instruments receiving bars
    print("\n" + "="*80)
    print("3. INSTRUMENTS RECEIVING BARS")
    print("="*80)
    
    instruments = {}
    for e in bar_received[-100:]:  # Last 100
        inst = e.get('instrument', 'UNKNOWN')
        ts = parse_timestamp(e.get('ts_utc', ''))
        if inst:
            if inst not in instruments:
                instruments[inst] = []
            if ts:
                instruments[inst].append(ts)
    
    print(f"  Instruments receiving bars (last 100 events):")
    for inst in sorted(instruments.keys()):
        timestamps = instruments[inst]
        if timestamps:
            latest = max(timestamps)
            age = (datetime.now(timezone.utc) - latest).total_seconds()
            ts_chicago = latest.astimezone(chicago_tz)
            print(f"    {inst:15} Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago) | {len(timestamps)} events")
    
    # 4. Check data stall detection
    print("\n" + "="*80)
    print("4. DATA STALL DETECTION")
    print("="*80)
    
    if data_stall:
        latest = max(data_stall, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            print(f"  Latest DATA_STALL_RECOVERED: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            print(f"  [INFO] Data stalls were detected but have recovered")
    
    if data_loss:
        latest = max(data_loss, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest.get('ts_utc', ''))
        if ts:
            ts_chicago = ts.astimezone(chicago_tz)
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            data = latest.get('data', {})
            note = data.get('note', '')[:80] if data.get('note') else ''
            print(f"  Latest DATA_LOSS_DETECTED: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago)")
            print(f"  Note: {note}")
    
    # 5. Summary
    print("\n" + "="*80)
    print("5. DATA STALL SUMMARY")
    print("="*80)
    
    # Determine if data is actually stalled
    is_stalled = False
    reasons = []
    
    if bar_received:
        latest_bar = max(bar_received, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts_bar = parse_timestamp(latest_bar.get('ts_utc', ''))
        if ts_bar:
            age_bar = (datetime.now(timezone.utc) - ts_bar).total_seconds()
            if age_bar > 300:  # 5 minutes
                is_stalled = True
                reasons.append(f"No bars received for {age_bar:.0f}s")
    
    if streams_expecting_bars > 0:
        if not bar_received or (bar_received and parse_timestamp(bar_received[-1].get('ts_utc', '')) and 
                                (datetime.now(timezone.utc) - parse_timestamp(bar_received[-1].get('ts_utc', ''))).total_seconds() > 300):
            is_stalled = True
            reasons.append(f"{streams_expecting_bars} stream(s) expecting bars but none received")
    
    if is_stalled:
        print("\n  [WARN] DATA STALL DETECTED")
        for reason in reasons:
            print(f"    - {reason}")
    else:
        print("\n  [OK] Data is flowing normally")
        if streams_expecting_bars == 0:
            print(f"    Note: No streams are currently expecting bars (ranges haven't formed yet)")
        else:
            print(f"    Note: {streams_expecting_bars} stream(s) expecting bars, bars arriving normally")
    
    print("="*80)

if __name__ == "__main__":
    main()
