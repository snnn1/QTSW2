import json
from collections import Counter
from datetime import datetime, timedelta

print("=" * 80)
print("COMPREHENSIVE MICRO FUTURES STATUS CHECK")
print("=" * 80)

# Read ENGINE log
log_file = r'logs\robot\robot_ENGINE.jsonl'
micro_contracts = ['MES', 'MNQ', 'MYM', 'M2K', 'MGC', 'MNG', 'MCL']

with open(log_file, 'r', encoding='utf-8') as f:
    lines = f.readlines()[-5000:]

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

print(f"Micro contract events: {len(micro_events)}")

# Check for recent errors (last hour)
now = datetime.utcnow()
one_hour_ago = now - timedelta(hours=1)

recent_errors = []
for e in micro_events:
    try:
        ts_str = str(e.get('ts_utc', ''))
        if ts_str:
            ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00').replace('+00:00', ''))
            if ts.tzinfo:
                ts = ts.replace(tzinfo=None)
            if ts >= one_hour_ago:
                event = str(e.get('event', '')).upper()
                if any(kw in event for kw in ['ERROR', 'FAIL', 'REJECT', 'BLOCKED']):
                    recent_errors.append(e)
    except:
        pass

print(f"\n{'='*80}")
print(f"RECENT ERRORS (Last Hour): {len(recent_errors)}")
print("="*80)

if recent_errors:
    error_types = Counter([e.get('event', 'UNKNOWN') for e in recent_errors])
    print("\nError types:")
    for err_type, count in error_types.most_common():
        print(f"  {err_type}: {count}")
    
    print("\nMost recent errors:")
    for e in recent_errors[-10:]:
        ts = str(e.get('ts_utc', ''))[:19]
        event = e.get('event', '')
        inst = e.get('data', {}).get('instrument', 'N/A')
        error_msg = str(e.get('data', {}).get('error', ''))[:150]
        print(f"  {ts} | {event} | Inst: {inst}")
        if error_msg:
            print(f"    Error: {error_msg}")
else:
    print("  ✓ No recent errors found!")

# Check execution events
exec_keywords = ['ORDER', 'EXECUTION', 'ENTRY', 'STOP', 'TARGET', 'SUBMIT', 'FILL']
exec_events = []
for e in micro_events:
    event = str(e.get('event', '')).upper()
    if any(kw in event for kw in exec_keywords):
        exec_events.append(e)

print(f"\n{'='*80}")
print(f"EXECUTION EVENTS: {len(exec_events)}")
print("="*80)

if exec_events:
    exec_types = Counter([e.get('event', 'UNKNOWN') for e in exec_events])
    print("\nExecution event types:")
    for exec_type, count in exec_types.most_common(15):
        print(f"  {exec_type}: {count}")
    
    print("\nRecent execution events:")
    for e in exec_events[-20:]:
        ts = str(e.get('ts_utc', ''))[:19]
        event = e.get('event', '')
        inst = e.get('data', {}).get('instrument', 'N/A')
        intent_id = str(e.get('intent_id', ''))
        if intent_id:
            intent_id = intent_id[:8]
        print(f"  {ts} | {event} | Inst: {inst} | Intent: {intent_id}")
else:
    print("  No execution events found")

# Check per-instrument status
print(f"\n{'='*80}")
print("PER-INSTRUMENT STATUS")
print("="*80)

for inst in micro_contracts:
    inst_events = [e for e in micro_events if e.get('data', {}).get('instrument', '') == inst]
    if not inst_events:
        continue
    
    errors = [e for e in inst_events if 'ERROR' in str(e.get('event', '')).upper() or 'FAIL' in str(e.get('event', '')).upper()]
    execs = [e for e in inst_events if any(kw in str(e.get('event', '')).upper() for kw in exec_keywords)]
    
    print(f"\n{inst}:")
    print(f"  Total events: {len(inst_events)}")
    print(f"  Errors/Failures: {len(errors)}")
    print(f"  Execution events: {len(execs)}")
    
    if errors:
        latest_error = errors[-1]
        error_msg = str(latest_error.get('data', {}).get('error', ''))[:100]
        print(f"  Latest error: {error_msg}")
    
    if execs:
        latest_exec = execs[-1]
        print(f"  Latest exec: {latest_exec.get('event', '')} at {str(latest_exec.get('ts_utc', ''))[:19]}")

# Check for CreateOrder specific errors
createorder_errors = [e for e in recent_errors if 'CREATEORDER' in str(e.get('data', {}).get('error', '')).upper()]
print(f"\n{'='*80}")
print(f"CreateOrder API ERRORS: {len(createorder_errors)}")
print("="*80)

if createorder_errors:
    print("These errors indicate the CreateOrder API fix may not be working:")
    for e in createorder_errors[-5:]:
        ts = str(e.get('ts_utc', ''))[:19]
        inst = e.get('data', {}).get('instrument', 'N/A')
        error_msg = str(e.get('data', {}).get('error', ''))
        print(f"  {ts} | Inst: {inst} | {error_msg}")
else:
    print("  ✓ No CreateOrder errors found in recent logs!")

print("\n" + "="*80)
print("CHECK COMPLETE")
print("="*80)
