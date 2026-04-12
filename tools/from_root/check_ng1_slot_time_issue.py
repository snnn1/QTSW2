#!/usr/bin/env python3
"""
Check NG1 slot time issue - why did it trigger at 07:30 instead of 09:00?
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timedelta
import pytz

def parse_timestamp(ts_str):
    """Parse timestamp string to datetime."""
    if not ts_str:
        return None
    try:
        if 'T' in ts_str or '+' in ts_str or 'Z' in ts_str:
            return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
        for fmt in ['%Y-%m-%d %H:%M:%S.%f', '%Y-%m-%d %H:%M:%S']:
            try:
                return datetime.strptime(ts_str[:19], fmt).replace(tzinfo=pytz.UTC)
            except:
                continue
    except:
        pass
    return None

def main():
    print("="*80)
    print("NG1 SLOT TIME INVESTIGATION")
    print("="*80)
    
    # Check timetable
    timetable_paths = [
        Path("data/timetable/timetable_current.json"),
        Path("configs/timetable.json")
    ]
    
    timetable = None
    timetable_file = None
    for tp in timetable_paths:
        if tp.exists():
            try:
                timetable = json.load(open(tp, 'r', encoding='utf-8'))
                timetable_file = tp
                break
            except:
                pass
    
    if timetable:
        print(f"\nTimetable file: {timetable_file}")
        print("\nNG1 Configuration:")
        streams = timetable.get('streams', [])
        ng1_config = None
        for s in streams:
            if isinstance(s, dict) and s.get('stream') == 'NG1':
                ng1_config = s
                break
        
        if ng1_config:
            print(f"  Enabled: {ng1_config.get('enabled')}")
            print(f"  Slot time: {ng1_config.get('slot_time')}")
            print(f"  Session: {ng1_config.get('session')}")
            print(f"  Instrument: {ng1_config.get('instrument')}")
        else:
            print("  [ERROR] NG1 not found in timetable!")
    else:
        print("\n[ERROR] Timetable file not found!")
    
    # Check spec
    spec_path = Path("configs/analyzer_robot_parity.json")
    if spec_path.exists():
        try:
            spec = json.load(open(spec_path, 'r', encoding='utf-8'))
            print("\nSpec Configuration:")
            sessions = spec.get('sessions', {})
            s1_slots = sessions.get('S1', {}).get('slot_end_times', [])
            s2_slots = sessions.get('S2', {}).get('slot_end_times', [])
            print(f"  S1 slot_end_times: {s1_slots}")
            print(f"  S2 slot_end_times: {s2_slots}")
        except Exception as e:
            print(f"\n[ERROR] Failed to read spec: {e}")
    
    # Check logs for NG1
    log_dir = Path("logs/robot")
    if not log_dir.exists():
        print("\n[ERROR] Log directory not found!")
        return 1
    
    print("\n" + "="*80)
    print("NG1 LOG EVENTS (Last 24 hours)")
    print("="*80)
    
    cutoff = datetime.now(pytz.UTC) - timedelta(hours=24)
    ng1_events = []
    
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                        stream = event.get('stream', event.get('stream_id', ''))
                        if stream == 'NG1':
                            ts = parse_timestamp(event.get('timestamp', event.get('ts_utc', '')))
                            if ts and ts >= cutoff:
                                ng1_events.append((ts, event))
                    except:
                        pass
        except:
            pass
    
    ng1_events.sort(key=lambda x: x[0])
    
    # Find range lock and order submission events
    print("\nKey Events:")
    range_locked_events = []
    order_submit_events = []
    
    for ts, event in ng1_events:
        event_type = event.get('event_type', event.get('EventType', ''))
        if 'RANGE_LOCKED' in event_type:
            range_locked_events.append((ts, event))
        if 'STOP_BRACKETS_SUBMIT' in event_type or 'ORDER_SUBMIT' in event_type:
            order_submit_events.append((ts, event))
    
    print(f"\nRange Lock Events: {len(range_locked_events)}")
    for ts, event in range_locked_events[-5:]:
        slot_time = event.get('slot_time_chicago', event.get('SlotTimeChicago', ''))
        print(f"  {ts.strftime('%Y-%m-%d %H:%M:%S UTC')} | Slot time: {slot_time} | State: {event.get('state', '')}")
    
    print(f"\nOrder Submit Events: {len(order_submit_events)}")
    for ts, event in order_submit_events[-5:]:
        slot_time = event.get('slot_time_chicago', event.get('SlotTimeChicago', ''))
        print(f"  {ts.strftime('%Y-%m-%d %H:%M:%S UTC')} | Slot time: {slot_time} | Event: {event_type}")
    
    # Check for slot time initialization
    print("\n" + "="*80)
    print("Slot Time Initialization Events")
    print("="*80)
    
    slot_init_events = []
    for ts, event in ng1_events:
        event_type = event.get('event_type', event.get('EventType', ''))
        if 'SLOT_TIME' in event_type or 'RANGE_START_INITIALIZED' in event_type:
            slot_init_events.append((ts, event))
    
    for ts, event in slot_init_events[-10:]:
        slot_time = event.get('slot_time_chicago', event.get('SlotTimeChicago', ''))
        slot_time_utc = event.get('slot_time_utc', '')
        data = event.get('data', {})
        slot_from_data = data.get('slot_time_chicago', '') if isinstance(data, dict) else ''
        print(f"  {ts.strftime('%Y-%m-%d %H:%M:%S UTC')} | Slot: {slot_time or slot_from_data} | Type: {event_type}")
    
    # Check timetable loading events
    print("\n" + "="*80)
    print("Timetable Loading Events")
    print("="*80)
    
    timetable_events = []
    for log_file in log_dir.glob("robot_ENGINE*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                        event_type = event.get('event_type', event.get('EventType', ''))
                        if 'TIMETABLE' in event_type.upper() or 'SLOT_TIME' in event_type:
                            ts = parse_timestamp(event.get('timestamp', event.get('ts_utc', '')))
                            if ts and ts >= cutoff:
                                timetable_events.append((ts, event))
                    except:
                        pass
        except:
            pass
    
    timetable_events.sort(key=lambda x: x[0])
    for ts, event in timetable_events[-10:]:
        event_type = event.get('event_type', event.get('EventType', ''))
        data = event.get('data', {})
        print(f"  {ts.strftime('%Y-%m-%d %H:%M:%S UTC')} | {event_type}")
        if isinstance(data, dict):
            if 'stream' in data:
                print(f"    Stream: {data.get('stream')} | Slot: {data.get('slot_time', '')}")
    
    return 0

if __name__ == '__main__':
    sys.exit(main())
