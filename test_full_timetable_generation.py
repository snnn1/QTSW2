"""Test full timetable generation for 2026-01-05"""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))

from modules.timetable.timetable_engine import TimetableEngine
import pandas as pd

print("=" * 80)
print("TESTING FULL TIMETABLE GENERATION FOR 2026-01-05")
print("=" * 80)

engine = TimetableEngine()

# Generate timetable
timetable_df = engine.generate_timetable(trade_date="2026-01-05")

print(f"\nGenerated timetable entries: {len(timetable_df)}")

if not timetable_df.empty:
    print(f"\nAll entries:")
    for idx, row in timetable_df.iterrows():
        print(f"  {row['stream_id']} {row['session']}: time={row['selected_time']}, allowed={row['allowed']}, reason={row['reason']}")
    
    # Check GC1 specifically
    gc1_entries = timetable_df[timetable_df['stream_id'] == 'GC1']
    print(f"\nGC1 entries: {len(gc1_entries)}")
    if len(gc1_entries) > 0:
        for idx, row in gc1_entries.iterrows():
            print(f"  GC1 {row['session']}: time={row['selected_time']}, allowed={row['allowed']}, reason={row['reason']}")
    else:
        print("  [X] GC1 NOT IN GENERATED TIMETABLE")
    
    # Check which streams are allowed
    allowed = timetable_df[timetable_df['allowed'] == True]
    print(f"\nAllowed streams: {sorted(allowed['stream_id'].unique())}")
    
    # Check which streams are in the execution timetable
    print("\n" + "=" * 80)
    print("CHECKING EXECUTION TIMETABLE FILE")
    print("=" * 80)
    import json
    with open("data/timetable/timetable_current.json", 'r') as f:
        current_timetable = json.load(f)
    
    current_streams = [s['stream'] for s in current_timetable.get('streams', [])]
    print(f"Streams in current timetable file: {sorted(current_streams)}")
    
    generated_allowed = sorted(allowed['stream_id'].unique())
    print(f"Streams that should be in timetable (allowed=True): {generated_allowed}")
    
    missing = [s for s in generated_allowed if s not in current_streams]
    if missing:
        print(f"\n[X] MISSING FROM EXECUTION FILE: {missing}")
    else:
        print("\n[OK] All allowed streams are in execution file")
else:
    print("[X] Timetable generation returned empty DataFrame")
