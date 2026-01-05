"""Test GC1 RS calculation directly"""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))

from modules.timetable.timetable_engine import TimetableEngine

print("=" * 80)
print("TESTING GC1 RS CALCULATION")
print("=" * 80)

engine = TimetableEngine()

# Test GC1 S1
print("\n1. Testing GC1 S1:")
rs_values = engine.calculate_rs_for_stream("GC1", "S1")
print(f"   RS values returned: {rs_values}")

if rs_values:
    print("   [OK] RS calculation worked!")
    for time, rs in rs_values.items():
        print(f"     {time}: RS = {rs}")
else:
    print("   [X] RS calculation returned empty dict")
    print("   This is why GC1 S1 gets skipped!")

# Test GC1 S2
print("\n2. Testing GC1 S2:")
rs_values_s2 = engine.calculate_rs_for_stream("GC1", "S2")
print(f"   RS values returned: {rs_values_s2}")

# Test select_best_time
print("\n3. Testing select_best_time for GC1 S1:")
selected_time, reason = engine.select_best_time("GC1", "S1")
print(f"   Selected time: {selected_time}")
print(f"   Reason: {reason}")

if selected_time is None:
    print("   [X] select_best_time returned None - this causes GC1 S1 to be skipped!")

# Test other missing streams
print("\n4. Testing other missing streams:")
missing_streams = ["CL1", "NQ1", "NG1", "NG2", "YM2"]
for stream_id in missing_streams:
    session = "S1" if stream_id.endswith("1") else "S2"
    rs = engine.calculate_rs_for_stream(stream_id, session)
    selected, reason = engine.select_best_time(stream_id, session)
    status = "OK" if selected else "MISSING"
    print(f"   {stream_id} {session}: RS={len(rs)} slots, selected={selected}, reason={reason} [{status}]")
