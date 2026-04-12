#!/usr/bin/env python3
"""
Debug timetable generation to see what's happening.
"""

import sys
from pathlib import Path
import pandas as pd
from datetime import timedelta

# Add project root to path
project_root = Path(__file__).parent
sys.path.insert(0, str(project_root))

from modules.matrix.file_manager import load_existing_matrix
from modules.timetable.timetable_engine import TimetableEngine

def main():
    """Debug timetable generation."""
    master_df = load_existing_matrix("data/master_matrix")
    
    if master_df.empty:
        print("No master matrix found!")
        return
    
    # Target date is Tuesday 2026-01-27
    trade_date_obj = pd.to_datetime('2026-01-27').date()
    print(f"Target date (Tuesday): {trade_date_obj}")
    
    # Previous day should be Monday 2026-01-26
    previous_date_obj = trade_date_obj - timedelta(days=1)
    print(f"Previous date (Monday): {previous_date_obj}")
    
    # Check NQ2 data
    nq2_df = master_df[master_df['Stream'] == 'NQ2'].copy()
    
    print("\n" + "=" * 80)
    print("NQ2 Data Check:")
    print("=" * 80)
    
    # Check Monday's data
    monday_df = nq2_df[nq2_df['trade_date'].dt.date == previous_date_obj].copy()
    print(f"\nMonday ({previous_date_obj}) data:")
    if monday_df.empty:
        print("  No data found!")
    else:
        for idx, row in monday_df.iterrows():
            print(f"  Time: {row.get('Time', 'N/A')}")
            print(f"  Time Change: '{row.get('Time Change', '')}'")
            print(f"  Result: {row.get('Result', 'N/A')}")
    
    # Check Tuesday's data
    tuesday_df = nq2_df[nq2_df['trade_date'].dt.date == trade_date_obj].copy()
    print(f"\nTuesday ({trade_date_obj}) data:")
    if tuesday_df.empty:
        print("  No data found!")
    else:
        for idx, row in tuesday_df.iterrows():
            print(f"  Time: {row.get('Time', 'N/A')}")
            print(f"  Time Change: '{row.get('Time Change', '')}'")
            print(f"  Result: {row.get('Result', 'N/A')}")
    
    # Now test the actual timetable generation
    print("\n" + "=" * 80)
    print("Testing Timetable Generation:")
    print("=" * 80)
    
    # Simulate what the timetable engine does
    previous_df = master_df[master_df['trade_date'].dt.date == previous_date_obj].copy()
    
    print(f"\nPrevious day DataFrame has {len(previous_df)} rows")
    print(f"Looking for NQ2 in previous day DataFrame...")
    
    nq2_row = previous_df[previous_df['Stream'] == 'NQ2']
    if nq2_row.empty:
        print("  NQ2 not found in previous day DataFrame!")
        print(f"  Available streams: {sorted(previous_df['Stream'].unique())}")
    else:
        row = nq2_row.iloc[0]
        print(f"  Found NQ2 row:")
        print(f"    Time: {row.get('Time', 'N/A')}")
        print(f"    Time Change: '{row.get('Time Change', '')}'")
        
        # Simulate the logic
        time = row.get('Time', '')
        time_change = row.get('Time Change', '')
        
        print(f"\n  Logic simulation:")
        print(f"    Initial time: {time}")
        print(f"    Time Change: '{time_change}'")
        
        if time_change and str(time_change).strip():
            print(f"    -> Using Time Change: {time_change}")
            final_time = time_change.strip()
        else:
            print(f"    -> Time Change is empty, using Time: {time}")
            final_time = time
        
        print(f"\n  Final time for Tuesday: {final_time}")

if __name__ == "__main__":
    main()
