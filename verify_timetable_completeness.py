"""Verify timetable contains all 12 streams"""
import json

with open('data/timetable/timetable_current.json', 'r') as f:
    timetable = json.load(f)

print("=" * 80)
print("TIMETABLE COMPLETENESS VERIFICATION")
print("=" * 80)

print(f"\nTrading date: {timetable.get('trading_date')}")
print(f"Total streams in file: {len(timetable.get('streams', []))}")

expected_streams = ["ES1", "ES2", "GC1", "GC2", "CL1", "CL2", "NQ1", "NQ2", "NG1", "NG2", "YM1", "YM2"]
present_streams = [s['stream'] for s in timetable.get('streams', [])]

print(f"\nExpected streams: {len(expected_streams)}")
print(f"Present streams: {len(present_streams)}")

missing = [s for s in expected_streams if s not in present_streams]
if missing:
    print(f"\n[X] MISSING STREAMS: {missing}")
else:
    print("\n[OK] ALL 12 STREAMS PRESENT")

enabled_count = sum(1 for s in timetable.get('streams', []) if s.get('enabled'))
disabled_count = sum(1 for s in timetable.get('streams', []) if not s.get('enabled'))

print(f"\nEnabled: {enabled_count}")
print(f"Disabled: {disabled_count}")

print("\n" + "=" * 80)
print("STREAM DETAILS")
print("=" * 80)

for stream in sorted(timetable.get('streams', []), key=lambda x: x['stream']):
    status = "ENABLED" if stream.get('enabled') else "DISABLED"
    block_reason = stream.get('block_reason', 'N/A')
    slot_time = stream.get('slot_time', 'N/A')
    decision_time = stream.get('decision_time', 'N/A')
    print(f"\n{stream['stream']}: {status}")
    print(f"  Slot time: {slot_time}")
    print(f"  Decision time: {decision_time}")
    if block_reason != 'N/A':
        print(f"  Block reason: {block_reason}")
