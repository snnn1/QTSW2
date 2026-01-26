"""Check timer diagnostic logs"""
import json
from pathlib import Path
from datetime import datetime, timezone

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    log_file = Path("logs/robot/robot_ENGINE.jsonl")
    
    events = []
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except Exception as e:
        print(f"Error reading log file: {e}")
        return
    
    # Get recent events (last 200)
    recent = events[-200:] if len(events) > 200 else events
    
    print("="*80)
    print("TIMER DIAGNOSTIC LOGS")
    print("="*80)
    
    # Find timer-related logs
    timer_lifecycle = [e for e in recent if 'TIMER_LIFECYCLE' in str(e.get('data', {}).get('payload', ''))]
    timer_gate = [e for e in recent if 'TIMER_GATE' in str(e.get('data', {}).get('payload', ''))]
    timer_meta = [e for e in recent if 'TIMER_META_HEARTBEAT' in str(e.get('data', {}).get('payload', ''))]
    timer_exception = [e for e in recent if 'TIMER_CALLBACK_EXCEPTION' in str(e.get('data', {}).get('payload', ''))]
    heartbeat_audit = [e for e in recent if e.get('event') == 'ENGINE_TICK_HEARTBEAT_AUDIT']
    heartbeats = [e for e in recent if e.get('event') == 'ENGINE_TICK_HEARTBEAT']
    
    print(f"\n[TIMER LIFECYCLE LOGS]")
    print(f"  Found: {len(timer_lifecycle)}")
    for e in timer_lifecycle[-10:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        payload = str(e.get('data', {}).get('payload', ''))
        print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A':19} | {payload[:100]}")
    
    print(f"\n[TIMER GATE LOGS (Early Returns)]")
    print(f"  Found: {len(timer_gate)}")
    for e in timer_gate[-10:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        payload = str(e.get('data', {}).get('payload', ''))
        print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A':19} | {payload[:100]}")
    
    print(f"\n[TIMER META HEARTBEAT]")
    print(f"  Found: {len(timer_meta)}")
    for e in timer_meta[-5:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        payload = str(e.get('data', {}).get('payload', ''))
        print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A':19} | {payload[:120]}")
    
    print(f"\n[TIMER CALLBACK EXCEPTIONS]")
    print(f"  Found: {len(timer_exception)}")
    for e in timer_exception[-5:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        payload = str(e.get('data', {}).get('payload', ''))
        print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A':19} | {payload[:150]}")
    
    print(f"\n[HEARTBEAT AUDIT LOGS]")
    print(f"  Found: {len(heartbeat_audit)}")
    for e in heartbeat_audit[-5:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        data = e.get('data', {})
        payload = data.get('payload', {})
        heartbeat_due = payload.get('heartbeat_due', 'N/A')
        will_emit = payload.get('will_emit', 'N/A')
        print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A':19} | Due: {heartbeat_due}, Will emit: {will_emit}")
    
    print(f"\n[ENGINE_TICK_HEARTBEAT EVENTS]")
    print(f"  Found: {len(heartbeats)}")
    for e in heartbeats[-5:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A':19}")
    
    # Check for ENGINE_START
    starts = [e for e in recent if e.get('event') == 'ENGINE_START']
    if starts:
        latest_start = starts[-1]
        start_time = parse_timestamp(latest_start.get('ts_utc', ''))
        if start_time:
            now_utc = datetime.now(timezone.utc)
            seconds_ago = (now_utc - start_time.replace(tzinfo=timezone.utc)).total_seconds()
            print(f"\n[ENGINE_START]")
            print(f"  Time: {start_time.strftime('%Y-%m-%d %H:%M:%S UTC')}")
            print(f"  Seconds ago: {seconds_ago:.1f}")
    
    # Summary
    print("\n" + "="*80)
    print("DIAGNOSIS SUMMARY")
    print("="*80)
    
    if len(timer_lifecycle) == 0:
        print("\n[CRITICAL] No timer lifecycle logs found!")
        print("  This means StartTickTimer() or StopTickTimer() are not being called")
        print("  OR the logs are not being written to robot_ENGINE.jsonl")
    
    if len(timer_meta) == 0 and len(heartbeats) == 0:
        print("\n[CRITICAL] No timer meta-heartbeat or engine heartbeats found!")
        print("  This means the timer callback is either:")
        print("    1. Not being called at all")
        print("    2. Returning early (check TIMER_GATE logs)")
        print("    3. Throwing exceptions (check TIMER_CALLBACK_EXCEPTION logs)")
    
    if len(timer_gate) > 0:
        print(f"\n[WARNING] Timer callback is returning early ({len(timer_gate)} times)")
        print("  Check TIMER_GATE logs above to see why")
    
    if len(timer_exception) > 0:
        print(f"\n[ERROR] Timer callback is throwing exceptions ({len(timer_exception)} times)")
        print("  Check TIMER_CALLBACK_EXCEPTION logs above")

if __name__ == '__main__':
    main()
