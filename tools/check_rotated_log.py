"""Check rotated log for ENGINE_START and health monitor events"""
import json
from pathlib import Path
from datetime import datetime

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

rotated_log = Path('logs/robot/robot_ENGINE_20260123_021621.jsonl')

print("="*80)
print("CHECKING ROTATED LOG FOR START EVENTS")
print("="*80)

events = []
with open(rotated_log, 'r', encoding='utf-8-sig') as f:
    for line in f:
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except:
                pass

print(f"\nTotal events in rotated log: {len(events)}")

# Find ENGINE_START
engine_starts = [e for e in events if e.get('event') == 'ENGINE_START']
print(f"\nENGINE_START events: {len(engine_starts)}")

if engine_starts:
    print("\n[ENGINE_START EVENTS]")
    for e in engine_starts[-5:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        print(f"  {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'} | run_id: {e.get('run_id', 'N/A')[:32]}...")

# Find health monitor events
health_events = []
for e in events:
    event_name = e.get('event', '')
    if any(x in event_name.upper() for x in ['HEALTH', 'PUSHOVER', 'CRITICAL', 'NOTIFICATION']):
        health_events.append(e)

print(f"\nHealth/Notification events: {len(health_events)}")

if health_events:
    print("\n[HEALTH MONITOR EVENTS]")
    for e in health_events[-20:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        event_name = e.get('event', 'N/A')
        run_id = str(e.get('run_id', ''))[:16] + '...' if e.get('run_id') else 'N/A'
        print(f"  {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'} | {event_name:45} | run_id: {run_id}")

# Check first few events
print(f"\n[FIRST 10 EVENTS IN ROTATED LOG]")
for e in events[:10]:
    ts = parse_timestamp(e.get('ts_utc', ''))
    event_name = e.get('event', 'N/A')
    run_id = str(e.get('run_id', ''))[:16] + '...' if e.get('run_id') else 'N/A'
    print(f"  {ts.strftime('%Y-%m-%d %H:%M:%S UTC') if ts else 'N/A'} | {event_name:45} | run_id: {run_id}")
