#!/usr/bin/env python3
"""Check initialization success and stream creation"""
import json
from pathlib import Path
from datetime import datetime, timedelta

def main():
    print("=" * 80)
    print("INITIALIZATION SUCCESS CHECK")
    print("=" * 80)
    print()
    
    # Load recent events (last hour)
    events = []
    log_dir = Path('logs/robot')
    files = sorted(log_dir.glob('*.jsonl'), key=lambda p: p.stat().st_mtime, reverse=True)
    
    cutoff = datetime.now() - timedelta(hours=1)
    
    for log_file in files[:5]:
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                        ts_utc = event.get('ts_utc', '')
                        if ts_utc:
                            try:
                                event_time = datetime.fromisoformat(ts_utc.replace('Z', '+00:00'))
                                if event_time >= cutoff:
                                    events.append(event)
                            except:
                                pass
                    except:
                        continue
        except Exception as e:
            print(f"  Error reading {log_file}: {e}")
    
    print(f"Total events in last hour: {len(events)}\n")
    
    # Check for critical errors
    critical_errors = []
    for e in events:
        event_type = e.get('event_type', '')
        message = str(e.get('message', ''))
        payload = str(e.get('payload', ''))
        full_text = f"{event_type} {message} {payload}".upper()
        
        if 'PHASE 3 ASSERTION FAILED' in full_text:
            critical_errors.append(e)
        elif 'EXECUTION_POLICY_VALIDATION_FAILED' in full_text:
            critical_errors.append(e)
        elif 'INIT_FAILED' in full_text and 'TRUE' in full_text:
            critical_errors.append(e)
    
    print(f"[CRITICAL ERRORS]")
    print(f"  Found: {len(critical_errors)}")
    if critical_errors:
        for e in critical_errors[-5:]:
            ts = e.get('ts_utc', '')[:19] if e.get('ts_utc') else 'N/A'
            event_type = e.get('event_type', 'N/A')
            print(f"    {ts} | {event_type}")
    else:
        print("  ✅ No critical initialization errors found!")
    
    print()
    
    # Check for STREAM_CREATED events
    stream_created = [e for e in events if e.get('event_type') == 'STREAM_CREATED']
    print(f"[STREAM CREATION]")
    print(f"  Found: {len(stream_created)} STREAM_CREATED events")
    if stream_created:
        print("  Recent streams:")
        for e in stream_created[-5:]:
            ts = e.get('ts_utc', '')[:19] if e.get('ts_utc') else 'N/A'
            payload = e.get('payload', {})
            exec_inst = payload.get('execution_instrument', 'N/A')
            canon_inst = payload.get('canonical_instrument', 'N/A')
            stream_id = payload.get('stream_id', 'N/A')
            print(f"    {ts} | {stream_id} | Exec={exec_inst} | Canon={canon_inst}")
    else:
        print("  ⚠️  No STREAM_CREATED events found in last hour")
    
    print()
    
    # Check for ENGINE_START
    engine_start = [e for e in events if e.get('event_type') == 'ENGINE_START']
    print(f"[ENGINE START]")
    print(f"  Found: {len(engine_start)} ENGINE_START events")
    if engine_start:
        for e in engine_start[-3:]:
            ts = e.get('ts_utc', '')[:19] if e.get('ts_utc') else 'N/A'
            print(f"    {ts}")
    
    print()
    print("=" * 80)
    print("SUMMARY")
    print("=" * 80)
    if len(critical_errors) == 0:
        print("✅ INITIALIZATION: SUCCESS - No critical errors")
    else:
        print("❌ INITIALIZATION: FAILED - Critical errors found")
    
    if len(stream_created) > 0:
        print(f"✅ STREAMS: Created {len(stream_created)} streams")
    else:
        print("⚠️  STREAMS: No stream creation events found")

if __name__ == '__main__':
    main()
