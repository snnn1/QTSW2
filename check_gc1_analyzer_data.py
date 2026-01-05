"""Check GC1 analyzer data to see why RS calculation fails"""
import pandas as pd
from pathlib import Path

print("=" * 80)
print("CHECKING GC1 ANALYZER DATA")
print("=" * 80)

gc1_dir = Path("data/analyzed/GC1")
parquet_files = sorted(gc1_dir.rglob("*.parquet"), reverse=True)

print(f"\nTotal GC1 parquet files: {len(parquet_files)}")
print(f"Checking last 10 files for RS calculation:")

all_trades = []
for file_path in parquet_files[:10]:
    try:
        df = pd.read_parquet(file_path)
        if df.empty:
            continue
        
        # Check required columns
        if 'Date' not in df.columns:
            print(f"  {file_path.name}: Missing 'Date' column")
            continue
        if 'Result' not in df.columns:
            print(f"  {file_path.name}: Missing 'Result' column")
            continue
        if 'Session' not in df.columns:
            print(f"  {file_path.name}: Missing 'Session' column")
            continue
        if 'Time' not in df.columns:
            print(f"  {file_path.name}: Missing 'Time' column")
            continue
        
        df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
        valid_dates = df['Date'].notna()
        if not valid_dates.any():
            print(f"  {file_path.name}: No valid dates")
            continue
        
        df = df[valid_dates].copy()
        all_trades.append(df)
        print(f"  {file_path.name}: {len(df)} rows, date range: {df['Date'].min().date()} to {df['Date'].max().date()}")
    except Exception as e:
        print(f"  {file_path.name}: Error - {e}")
        continue

if not all_trades:
    print("\n[X] NO VALID TRADES FOUND")
else:
    print(f"\nTotal valid files: {len(all_trades)}")
    
    # Merge and check sessions
    df = pd.concat(all_trades, ignore_index=True)
    print(f"\nTotal rows: {len(df)}")
    print(f"Sessions present: {sorted(df['Session'].unique())}")
    print(f"Times present: {sorted(df['Time'].unique())}")
    
    # Check S1 session specifically
    s1_data = df[df['Session'] == 'S1']
    print(f"\nS1 session rows: {len(s1_data)}")
    
    if len(s1_data) > 0:
        print(f"S1 times: {sorted(s1_data['Time'].unique())}")
        print(f"S1 date range: {s1_data['Date'].min().date()} to {s1_data['Date'].max().date()}")
        
        # Check each time slot
        session_time_slots = {
            "S1": ["07:30", "08:00", "09:00"],
            "S2": ["09:30", "10:00", "10:30", "11:00"],
        }
        
        print(f"\nChecking S1 time slots:")
        for time_slot in session_time_slots.get("S1", []):
            time_trades = s1_data[s1_data['Time'] == time_slot]
            print(f"  {time_slot}: {len(time_trades)} trades")
            if len(time_trades) > 0:
                last_13 = time_trades.tail(13)
                print(f"    Last 13 trades date range: {last_13['Date'].min().date()} to {last_13['Date'].max().date()}")
                results = last_13['Result'].value_counts()
                print(f"    Results: {dict(results)}")
    else:
        print("[X] NO S1 SESSION DATA FOUND")
        print("This is why calculate_rs_for_stream returns empty dict for GC1 S1!")
