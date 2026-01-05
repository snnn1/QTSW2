"""Root cause analysis for missing streams in timetable"""
import pandas as pd
from pathlib import Path

print("=" * 80)
print("ROOT CAUSE ANALYSIS - Why GC1 and others are missing")
print("=" * 80)

# Check analyzer data directory
analyzer_dir = Path("data/analyzed")
print(f"\n1. ANALYZER DATA CHECK:")
print(f"   Directory exists: {analyzer_dir.exists()}")

if analyzer_dir.exists():
    subdirs = [d for d in analyzer_dir.iterdir() if d.is_dir()]
    print(f"   Stream directories found: {len(subdirs)}")
    if subdirs:
        print(f"   Streams: {sorted([d.name for d in subdirs])}")
        
        # Check GC1 specifically
        gc1_dir = analyzer_dir / "GC1"
        if gc1_dir.exists():
            parquet_files = list(gc1_dir.rglob("*.parquet"))
            print(f"\n   GC1 parquet files: {len(parquet_files)}")
            if parquet_files:
                print(f"   Example: {parquet_files[0].name}")
        else:
            print(f"\n   [X] GC1 directory NOT FOUND")
    else:
        print(f"   [X] NO STREAM DIRECTORIES FOUND")
else:
    print(f"   [X] ANALYZER DIRECTORY DOES NOT EXIST")

# Check master matrix
print(f"\n2. MASTER MATRIX CHECK:")
matrix_dir = Path("data/master_matrix")
matrix_files = sorted(matrix_dir.glob("*.parquet"), key=lambda p: p.stat().st_mtime, reverse=True)
if matrix_files:
    df = pd.read_parquet(matrix_files[0])
    df['Date'] = pd.to_datetime(df['Date']).dt.date
    latest_date = df['Date'].max()
    print(f"   Latest date: {latest_date}")
    print(f"   Latest date is Friday (2026-01-02)")
    print(f"   Timetable date is Monday (2026-01-05)")
    print(f"   [X] NO DATA FOR 2026-01-05 IN MASTER MATRIX")

# Check timetable
print(f"\n3. TIMETABLE CHECK:")
import json
with open("data/timetable/timetable_current.json", 'r') as f:
    timetable = json.load(f)

timetable_date = timetable.get('trading_date')
timetable_source = timetable.get('source')
timetable_streams = [s['stream'] for s in timetable.get('streams', [])]

print(f"   Date: {timetable_date}")
print(f"   Source: {timetable_source}")
print(f"   Streams: {sorted(timetable_streams)}")

expected_streams = ["ES1", "ES2", "GC1", "GC2", "CL1", "CL2", "NQ1", "NQ2", "NG1", "NG2", "YM1", "YM2"]
missing = [s for s in expected_streams if s not in timetable_streams]
print(f"\n   Missing streams: {missing}")

print("\n" + "=" * 80)
print("ROOT CAUSE SUMMARY")
print("=" * 80)
print("""
The timetable for 2026-01-05 (Monday) was generated using generate_timetable()
method, which requires analyzer data to calculate RS (Rolling Sum) values.

PROBLEM:
1. Master matrix only has data up to 2026-01-02 (Friday)
2. No analyzer data exists in data/analyzed/ directory
3. generate_timetable() tries to calculate RS from analyzer data
4. When calculate_rs_for_stream() finds no data, it returns empty dict
5. select_best_time() returns None when RS dict is empty
6. Streams with selected_time=None are skipped (line 328-329)

MISSING STREAMS:
- GC1, CL1, NQ1, NG1, NG2, YM2

These streams are missing because they don't have analyzer data to calculate
RS values from, so select_best_time() returns None and they get skipped.

SOLUTION:
1. Generate analyzer data for all streams, OR
2. Update master matrix to include 2026-01-05, OR  
3. Modify generate_timetable() to use default times when RS can't be calculated
""")
