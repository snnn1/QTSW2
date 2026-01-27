"""Check execution instruments for ES1/NQ1 streams"""
import json
from pathlib import Path
from datetime import datetime

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    qtsw2_root = Path(__file__).parent.parent
    log_file = qtsw2_root / "logs" / "robot" / "robot_ENGINE.jsonl"
    
    print("="*80)
    print("STREAM EXECUTION INSTRUMENT CHECK")
    print("="*80)
    
    if not log_file.exists():
        print("  No log file found")
        return
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass
    
    # Find STREAMS_CREATED events
    streams_created = [e for e in events if e.get('event') == 'STREAMS_CREATED']
    
    if streams_created:
        print(f"\n[STREAMS CREATED]")
        latest = streams_created[-1]
        ts = parse_timestamp(latest.get('ts_utc', ''))
        data = latest.get('data', {})
        
        print(f"  Time: {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'}")
        print(f"  Streams: {data.get('stream_count', 'N/A')}")
        
        # Check for ES1/NQ1 in the streams
        streams_info = data.get('streams', [])
        if streams_info:
            print(f"\n  Stream Details:")
            for stream_info in streams_info:
                stream_id = stream_info.get('stream', 'N/A')
                if 'ES1' in stream_id or 'NQ1' in stream_id:
                    print(f"\n    {stream_id}:")
                    print(f"      Instrument: {stream_info.get('instrument', 'N/A')}")
                    print(f"      Execution Instrument: {stream_info.get('execution_instrument', 'N/A')}")
                    print(f"      Canonical Instrument: {stream_info.get('canonical_instrument', 'N/A')}")
                    print(f"      Execution Mode: {stream_info.get('execution_mode', 'N/A')}")
    
    # Find EXECUTION_MODE_SET events for ES1/NQ1
    print(f"\n[EXECUTION MODE SET EVENTS]")
    mode_events = [e for e in events if e.get('event') == 'EXECUTION_MODE_SET']
    
    es1_nq1_modes = []
    for e in mode_events:
        event_str = json.dumps(e).upper()
        if 'ES1' in event_str or 'NQ1' in event_str:
            es1_nq1_modes.append(e)
    
    if es1_nq1_modes:
        print(f"\n  Found {len(es1_nq1_modes)} execution mode events for ES1/NQ1:")
        for e in es1_nq1_modes[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            print(f"\n    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}:")
            print(f"      Stream: {data.get('stream', 'N/A')}")
            print(f"      Execution Instrument: {data.get('execution_instrument', 'N/A')}")
            print(f"      Canonical Instrument: {data.get('canonical_instrument', 'N/A')}")
            print(f"      Execution Mode: {data.get('execution_mode', 'N/A')}")
    else:
        print("  No execution mode events found for ES1/NQ1")
    
    # Check for any events that show the execution instrument mapping
    print(f"\n[EXECUTION INSTRUMENT MAPPING]")
    mapping_events = []
    for e in events:
        data = e.get('data', {})
        if 'execution_instrument' in data or 'canonical_instrument' in data:
            event_str = json.dumps(e).upper()
            if 'ES1' in event_str or 'NQ1' in event_str:
                mapping_events.append(e)
    
    if mapping_events:
        print(f"\n  Found {len(mapping_events)} events with instrument mapping:")
        for e in mapping_events[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            print(f"\n    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {e.get('event', 'N/A')}")
            print(f"      Stream: {e.get('stream', data.get('stream', 'N/A'))}")
            print(f"      Execution Instrument: {data.get('execution_instrument', 'N/A')}")
            print(f"      Canonical Instrument: {data.get('canonical_instrument', 'N/A')}")
            print(f"      Instrument: {data.get('instrument', e.get('instrument', 'N/A'))}")

if __name__ == '__main__':
    main()
