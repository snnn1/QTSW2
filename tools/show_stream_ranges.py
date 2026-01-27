#!/usr/bin/env python3
"""Show stream ranges and time slots"""
import json
from pathlib import Path

qtsw2_root = Path(__file__).parent.parent
timetable_path = qtsw2_root / "data" / "timetable" / "timetable_current.json"
spec_path = qtsw2_root / "configs" / "analyzer_robot_parity.json"

with open(timetable_path) as f:
    timetable = json.load(f)

with open(spec_path) as f:
    spec = json.load(f)

sessions = spec['sessions']
enabled_streams = [s for s in timetable['streams'] if s['enabled']]
enabled_streams.sort(key=lambda x: (x['session'], x['slot_time']))

print("=" * 70)
print("STREAM RANGES AND TIME SLOTS")
print("=" * 70)
print(f"\nTrading Date: {timetable['trading_date']}")
print(f"Timezone: {spec['timezone']}")
print(f"\nTotal Enabled Streams: {len(enabled_streams)}")
print("\n" + "=" * 70)

for session_id in ['S1', 'S2']:
    session_streams = [s for s in enabled_streams if s['session'] == session_id]
    if not session_streams:
        continue
    
    range_start = sessions[session_id]['range_start_time']
    allowed_slots = sessions[session_id]['slot_end_times']
    
    print(f"\n{session_id} SESSION")
    print("-" * 70)
    print(f"Range Start Time: {range_start} CT")
    print(f"Allowed Slot Times: {', '.join(allowed_slots)} CT")
    print(f"\nActive Streams ({len(session_streams)}):")
    print()
    
    for stream in sorted(session_streams, key=lambda x: x['slot_time']):
        slot_time = stream['slot_time']
        range_window = f"[{range_start}, {slot_time})"
        print(f"  {stream['stream']:6} ({stream['instrument']:3})")
        print(f"    Slot Time:     {slot_time:5} CT")
        print(f"    Range Window:  {range_window:20} CT")
        print()

print("=" * 70)
print("\nNOTES:")
print("- Range window is [range_start, slot_time) - inclusive start, exclusive end")
print("- Range locks at slot_time, then breakout detection begins")
print("- Entry cutoff: 16:00 CT (market close)")
print("=" * 70)
