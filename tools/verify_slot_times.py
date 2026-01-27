#!/usr/bin/env python3
"""Verify slot times are valid"""
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
all_streams = timetable['streams']
enabled_streams = [s for s in all_streams if s['enabled']]

print("=" * 70)
print("SLOT TIME VERIFICATION")
print("=" * 70)
print(f"\nTrading Date: {timetable['trading_date']}")
print(f"Total Streams: {len(all_streams)}")
print(f"Enabled Streams: {len(enabled_streams)}")
print("\n" + "=" * 70)

errors = []
warnings = []

for session_id in ['S1', 'S2']:
    session_streams = [s for s in enabled_streams if s['session'] == session_id]
    if not session_streams:
        continue
    
    range_start = sessions[session_id]['range_start_time']
    allowed_slots = sessions[session_id]['slot_end_times']
    
    print(f"\n{session_id} SESSION")
    print("-" * 70)
    print(f"Range Start: {range_start} CT")
    print(f"Allowed Slot Times: {', '.join(allowed_slots)} CT")
    print(f"\nStreams ({len(session_streams)}):")
    
    for stream in sorted(session_streams, key=lambda x: x['slot_time']):
        slot_time = stream['slot_time']
        is_valid = slot_time in allowed_slots
        status = "VALID" if is_valid else "INVALID"
        
        if not is_valid:
            errors.append(f"{stream['stream']} ({session_id}): slot_time '{slot_time}' not in allowed list {allowed_slots}")
        
        print(f"  {stream['stream']:6} ({stream['instrument']:3}) - Slot: {slot_time:5} CT [{status}]")
        print(f"    Range: [{range_start}, {slot_time}) CT")

print("\n" + "=" * 70)
if errors:
    print("\nERRORS FOUND:")
    for error in errors:
        print(f"  [ERROR] {error}")
    print("\n[WARNING] Invalid slot times will cause the robot to fail-closed for those streams!")
else:
    print("\n[OK] All slot times are valid!")
print("=" * 70)
