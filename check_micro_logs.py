import json
from collections import Counter
from datetime import datetime

# Read the ENGINE log
log_file = r'logs\robot\robot_ENGINE.jsonl'
micro_contracts = ['MES', 'MNQ', 'MYM', 'M2K', 'MGC', 'MNG', 'MCL']

print("=" * 80)
print("MICRO FUTURES LOG ANALYSIS")
print("=" * 80)

# Read last 10000 lines
with open(log_file, 'r', encoding='utf-8') as f:
    lines = f.readlines()[-10000:]

events = []
for line in lines:
    if line.strip():
        try:
            events.append(json.loads(line))
        except:
            pass

print(f"\nTotal events analyzed: {len(events)}")

# Filter micro contract events
micro_events = []
for e in events:
    inst = str(e.get('data', {}).get('instrument', '') or e.get('instrument', ''))
    if any(x in inst.upper() for x in micro_contracts):
        micro_events.append(e)

print(f"Micro contract events found: {len(micro_events)}")

# Event type counts
event_types = Counter([e.get('event', 'UNKNOWN') for e in micro_events])
print("\n" + "=" * 80)
print("EVENT TYPE COUNTS (Micro Contracts)")
print("=" * 80)
for event_type, count in event_types.most_common(30):
    print(f"  {event_type}: {count}")

# Find errors/failures/rejections
errors = []
for e in micro_events:
    event = str(e.get('event', '')).upper()
    if any(x in event for x in ['ERROR', 'FAIL', 'REJECT', 'BLOCKED', 'EXECUTION_BLOCKED']):
        errors.append(e)

print("\n" + "=" * 80)
print(f"ERROR/FAIL/REJECT/BLOCKED EVENTS: {len(errors)}")
print("=" * 80)
for e in errors[:20]:
    ts = e.get('ts_utc', '')[:19]
    event = e.get('event', '')
    inst = e.get('data', {}).get('instrument', 'N/A')
    data = e.get('data', {})
    print(f"\n{ts} | {event} | Inst: {inst}")
    if data:
        print(f"  {json.dumps(data, indent=2)[:600]}")

# Find execution-related events
exec_events = []
for e in micro_events:
    event = str(e.get('event', '')).upper()
    if any(x in event for x in ['ORDER', 'EXECUTION', 'ENTRY', 'STOP', 'TARGET', 'SUBMIT', 'FILL', 'INTENT', 'PROTECTIVE']):
        exec_events.append(e)

print("\n" + "=" * 80)
print(f"EXECUTION-RELATED EVENTS: {len(exec_events)}")
print("=" * 80)
for e in exec_events[-30:]:
    ts = str(e.get('ts_utc', ''))[:19]
    event = e.get('event', '')
    inst = e.get('data', {}).get('instrument', 'N/A')
    intent_id = str(e.get('intent_id', ''))
    if intent_id:
        intent_id = intent_id[:8]
    print(f"{ts} | {event} | Inst: {inst} | Intent: {intent_id}")

# Check for stream state events
stream_events = []
for e in micro_events:
    event = str(e.get('event', '')).upper()
    if any(x in event for x in ['STREAM', 'ARMED', 'RANGE', 'LOCKED', 'BRACKET', 'POSITION']):
        stream_events.append(e)

print("\n" + "=" * 80)
print(f"STREAM STATE EVENTS: {len(stream_events)}")
print("=" * 80)
from collections import Counter
inst_counts = Counter([e.get('data', {}).get('instrument', 'UNKNOWN') for e in stream_events])
print('\nStream events by instrument:')
for inst, count in inst_counts.most_common():
    print(f'  {inst}: {count}')

print('\nRecent stream state events:')
for e in stream_events[-30:]:
    ts = str(e.get('ts_utc', ''))[:19]
    event = e.get('event', '')
    inst = e.get('data', {}).get('instrument', 'N/A')
    stream = e.get('data', {}).get('stream', 'N/A')
    state = e.get('data', {}).get('state', 'N/A')
    print(f"{ts} | {event} | Inst: {inst} | Stream: {stream} | State: {state}")

# Check for BARSREQUEST_FAILED
failed_requests = [e for e in micro_events if e.get('event') == 'BARSREQUEST_FAILED']
print("\n" + "=" * 80)
print(f"FAILED BAR REQUESTS: {len(failed_requests)}")
print("=" * 80)
for e in failed_requests[:10]:
    ts = e.get('ts_utc', '')[:19]
    inst = e.get('data', {}).get('instrument', 'N/A')
    data = e.get('data', {})
    print(f"\n{ts} | Inst: {inst}")
    print(f"  {json.dumps(data, indent=2)[:400]}")
