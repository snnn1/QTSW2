"""Diagnostic script to check why GC1 and other streams are missing from timetable"""
import pandas as pd
from pathlib import Path
from datetime import datetime

# Check master matrix files
matrix_dir = Path("data/master_matrix")
matrix_files = sorted(matrix_dir.glob("*.parquet"), key=lambda p: p.stat().st_mtime, reverse=True)

print("=" * 80)
print("TIMETABLE DIAGNOSTIC - Checking why GC1 is missing")
print("=" * 80)

if not matrix_files:
    print("ERROR: No master matrix files found!")
    exit(1)

# Load latest master matrix
latest_file = matrix_files[0]
print(f"\nLatest master matrix file: {latest_file.name}")
df = pd.read_parquet(latest_file)

print(f"Date range in matrix: {df['Date'].min()} to {df['Date'].max()}")
print(f"Total rows: {len(df)}")

# Check for 2026-01-05
target_date = pd.to_datetime("2026-01-05").date()
df['Date'] = pd.to_datetime(df['Date']).dt.date

jan5_data = df[df['Date'] == target_date]
print(f"\nRows for 2026-01-05: {len(jan5_data)}")

if len(jan5_data) > 0:
    print(f"\nStreams present for 2026-01-05:")
    streams_present = sorted(jan5_data['Stream'].unique())
    print(f"  {streams_present}")
    
    # Check GC1 specifically
    gc1_data = jan5_data[jan5_data['Stream'] == 'GC1']
    print(f"\nGC1 data for 2026-01-05: {len(gc1_data)} rows")
    
    if len(gc1_data) > 0:
        print("\nGC1 details:")
        for idx, row in gc1_data.iterrows():
            print(f"  Stream: {row.get('Stream', 'N/A')}")
            print(f"  Time: {row.get('Time', 'N/A')}")
            print(f"  Time Change: {row.get('Time Change', 'N/A')}")
            print(f"  final_allowed: {row.get('final_allowed', 'N/A')}")
            print(f"  Session: {row.get('Session', 'N/A')}")
            print(f"  Result: {row.get('Result', 'N/A')}")
            print()
    else:
        print("  GC1 has NO data for 2026-01-05 in master matrix")
    
    # Check all streams and their final_allowed status
    print("\nAll streams for 2026-01-05 with final_allowed status:")
    for stream in streams_present:
        stream_data = jan5_data[jan5_data['Stream'] == stream]
        if len(stream_data) > 0:
            final_allowed = stream_data.iloc[0].get('final_allowed', 'N/A')
            time_val = stream_data.iloc[0].get('Time', 'N/A')
            session = stream_data.iloc[0].get('Session', 'N/A')
            print(f"  {stream}: final_allowed={final_allowed}, Time={time_val}, Session={session}")
    
    # Check expected streams
    expected_streams = ["ES1", "ES2", "GC1", "GC2", "CL1", "CL2", "NQ1", "NQ2", "NG1", "NG2", "YM1", "YM2"]
    missing_streams = [s for s in expected_streams if s not in streams_present]
    print(f"\nMissing streams: {missing_streams}")
    
    for missing in missing_streams:
        print(f"\n  {missing}: Not found in master matrix for 2026-01-05")
else:
    print(f"\nWARNING: No data found for 2026-01-05 in master matrix!")
    print("The master matrix may not have been updated for this date.")
    
    # Check what dates are available
    available_dates = sorted(df['Date'].unique())
    print(f"\nAvailable dates in master matrix (last 10):")
    for d in available_dates[-10:]:
        print(f"  {d}")

# Check timetable current file
print("\n" + "=" * 80)
print("CURRENT TIMETABLE FILE")
print("=" * 80)

timetable_file = Path("data/timetable/timetable_current.json")
if timetable_file.exists():
    import json
    with open(timetable_file, 'r') as f:
        timetable = json.load(f)
    
    print(f"Trading date: {timetable.get('trading_date')}")
    print(f"Source: {timetable.get('source')}")
    print(f"Streams in timetable: {len(timetable.get('streams', []))}")
    
    timetable_streams = [s['stream'] for s in timetable.get('streams', [])]
    print(f"  {sorted(timetable_streams)}")
    
    expected_streams = ["ES1", "ES2", "GC1", "GC2", "CL1", "CL2", "NQ1", "NQ2", "NG1", "NG2", "YM1", "YM2"]
    missing_from_timetable = [s for s in expected_streams if s not in timetable_streams]
    print(f"\nMissing from timetable: {missing_from_timetable}")
else:
    print("Timetable file not found!")
