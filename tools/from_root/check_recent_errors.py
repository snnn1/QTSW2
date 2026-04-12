import json
from datetime import datetime, timedelta
from collections import Counter

print("=" * 80)
print("RECENT ERROR CHECK")
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

# Check for errors in last 30 minutes
now = datetime.utcnow()
thirty_min_ago = now - timedelta(minutes=30)

recent_errors = []
for e in events:
    try:
        ts_str = str(e.get('ts_utc', ''))
        if ts_str:
            ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00').replace('+00:00', ''))
            if ts.tzinfo:
                ts = ts.replace(tzinfo=None)
            if ts >= thirty_min_ago:
                inst = str(e.get('data', {}).get('instrument', '') or e.get('instrument', ''))
                if any(x in inst.upper() for x in micro_contracts):
                    event = str(e.get('event', '')).upper()
                    if any(kw in event for kw in ['ERROR', 'FAIL', 'REJECT', 'BLOCKED']):
                        recent_errors.append(e)
    except:
        pass

print(f"\n{'='*80}")
print(f"ERRORS IN LAST 30 MINUTES: {len(recent_errors)}")
print("="*80)

if recent_errors:
    error_types = Counter([e.get('event', 'UNKNOWN') for e in recent_errors])
    print("\nError types:")
    for err_type, count in error_types.most_common():
        print(f"  {err_type}: {count}")
    
    print("\nMost recent errors:")
    for e in recent_errors[-20:]:
        ts = str(e.get('ts_utc', ''))[:19]
        event = e.get('event', '')
        inst = e.get('data', {}).get('instrument', 'N/A')
        error_msg = str(e.get('data', {}).get('error', ''))[:150]
        print(f"  {ts} | {event} | Inst: {inst}")
        if error_msg:
            print(f"    Error: {error_msg}")
else:
    print("\n  No errors found in last 30 minutes!")

# Check specifically for CreateOrder errors
createorder_errors = [e for e in recent_errors if 'CREATEORDER' in str(e.get('data', {}).get('error', '')).upper() or 'ORDER_SUBMIT_FAIL' in str(e.get('event', '')).upper()]
print(f"\n{'='*80}")
print(f"CreateOrder API ERRORS (last 30 min): {len(createorder_errors)}")
print("="*80)

if createorder_errors:
    print("These errors indicate CreateOrder API issues:")
    for e in createorder_errors[-10:]:
        ts = str(e.get('ts_utc', ''))[:19]
        inst = e.get('data', {}).get('instrument', 'N/A')
        error_msg = str(e.get('data', {}).get('error', ''))
        print(f"  {ts} | Inst: {inst} | {error_msg}")
else:
    print("  No CreateOrder errors found in last 30 minutes!")

# Check per-instrument status
print(f"\n{'='*80}")
print("PER-INSTRUMENT STATUS (last 30 min)")
print("="*80)

for inst in micro_contracts:
    inst_errors = [e for e in recent_errors if e.get('data', {}).get('instrument', '') == inst]
    if inst_errors:
        print(f"\n{inst}: {len(inst_errors)} errors")
        latest = inst_errors[-1]
        error_msg = str(latest.get('data', {}).get('error', ''))[:100]
        print(f"  Latest: {str(latest.get('ts_utc', ''))[:19]} | {latest.get('event', '')}")
        if error_msg:
            print(f"  Error: {error_msg}")
    else:
        print(f"\n{inst}: No errors")

print("\n" + "="*80)
print("CHECK COMPLETE")
print("="*80)
