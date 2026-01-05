"""Check why GC1 is missing - detailed analysis"""
import pandas as pd
from pathlib import Path

# Load latest master matrix
matrix_dir = Path("data/master_matrix")
matrix_files = sorted(matrix_dir.glob("*.parquet"), key=lambda p: p.stat().st_mtime, reverse=True)
df = pd.read_parquet(matrix_files[0])

print("=" * 80)
print("DETAILED ANALYSIS - Why GC1 is missing")
print("=" * 80)

# Check latest date (2026-01-02)
df['Date'] = pd.to_datetime(df['Date']).dt.date
latest_date = df['Date'].max()
print(f"\nLatest date in master matrix: {latest_date}")

latest_data = df[df['Date'] == latest_date]
print(f"Rows for {latest_date}: {len(latest_data)}")

# Check all streams for latest date
print(f"\nAll streams for {latest_date}:")
streams = sorted(latest_data['Stream'].unique())
print(f"  {streams}")

# Check GC1 specifically
gc1_latest = latest_data[latest_data['Stream'] == 'GC1']
print(f"\nGC1 rows for {latest_date}: {len(gc1_latest)}")

if len(gc1_latest) > 0:
    print("\nGC1 details:")
    for idx, row in gc1_latest.iterrows():
        print(f"  Row {idx}:")
        print(f"    Stream: {row.get('Stream')}")
        print(f"    Session: {row.get('Session')}")
        print(f"    Time: {row.get('Time')}")
        print(f"    Time Change: {row.get('Time Change')}")
        print(f"    final_allowed: {row.get('final_allowed')}")
        print(f"    Result: {row.get('Result')}")
        print(f"    filter_reasons: {row.get('filter_reasons')}")
        print()

# Check what streams are in timetable vs what should be
print("\n" + "=" * 80)
print("COMPARISON: Timetable vs Master Matrix")
print("=" * 80)

import json
with open("data/timetable/timetable_current.json", 'r') as f:
    timetable = json.load(f)

timetable_date = timetable.get('trading_date')
print(f"\nTimetable date: {timetable_date}")
print(f"Master matrix latest date: {latest_date}")

timetable_streams = [s['stream'] for s in timetable.get('streams', [])]
print(f"\nStreams in timetable: {sorted(timetable_streams)}")
print(f"Streams in master matrix for {latest_date}: {sorted(streams)}")

# Check if timetable was generated from a different date
if timetable_date != str(latest_date):
    print(f"\nWARNING: MISMATCH: Timetable is for {timetable_date} but master matrix latest is {latest_date}")
    print("The timetable may have been generated using generate_timetable() method")
    print("which uses RS calculation from analyzer data, not master matrix.")

# Check GC1 in master matrix for latest date
if 'GC1' in streams:
    print("\n[OK] GC1 EXISTS in master matrix for latest date")
    gc1_row = latest_data[latest_data['Stream'] == 'GC1'].iloc[0]
    print(f"   final_allowed: {gc1_row.get('final_allowed')}")
    print(f"   Time: {gc1_row.get('Time')}")
    print(f"   Session: {gc1_row.get('Session')}")
    
    if gc1_row.get('final_allowed') != True:
        print(f"\n[X] GC1 is FILTERED OUT (final_allowed = {gc1_row.get('final_allowed')})")
    if not gc1_row.get('Time'):
        print(f"\n[X] GC1 has NO TIME value")
else:
    print("\n[X] GC1 DOES NOT EXIST in master matrix for latest date")
