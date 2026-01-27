#!/usr/bin/env python3
"""
Deep dive into timetable generation to find the bug.
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
    """Deep dive into timetable generation."""
    print("=" * 80)
    print("DEEP DIVE: Timetable Generation Debug")
    print("=" * 80)
    
    # Load master matrix
    master_df = load_existing_matrix("data/master_matrix")
    print(f"\n1. Master Matrix loaded: {len(master_df)} rows")
    
    if master_df.empty:
        print("ERROR: Master matrix is empty!")
        return
    
    # Check NQ2 data
    nq2_df = master_df[master_df['Stream'] == 'NQ2'].copy()
    print(f"\n2. NQ2 rows in master matrix: {len(nq2_df)}")
    
    # Get all dates for NQ2
    nq2_dates = sorted(nq2_df['trade_date'].dt.date.unique())
    print(f"\n3. NQ2 dates (last 10):")
    for d in nq2_dates[-10:]:
        print(f"   {d}")
    
    # Target date is Tuesday 2026-01-27
    target_date = pd.to_datetime('2026-01-27').date()
    print(f"\n4. Target date (Tuesday): {target_date}")
    
    # Previous day should be Monday 2026-01-26
    previous_date = target_date - timedelta(days=1)
    print(f"5. Previous date (Monday): {previous_date}")
    
    # Check Monday's NQ2 data
    monday_nq2 = nq2_df[nq2_df['trade_date'].dt.date == previous_date].copy()
    print(f"\n6. Monday's NQ2 data:")
    if monday_nq2.empty:
        print("   ERROR: No NQ2 data found for Monday!")
        print(f"   Available dates: {nq2_dates[-5:]}")
    else:
        for idx, row in monday_nq2.iterrows():
            print(f"   Row {idx}:")
            print(f"     Time: {row.get('Time', 'N/A')}")
            print(f"     Time Change: '{row.get('Time Change', '')}'")
            print(f"     Result: {row.get('Result', 'N/A')}")
            print(f"     Session: {row.get('Session', 'N/A')}")
    
    # Check Tuesday's NQ2 data
    tuesday_nq2 = nq2_df[nq2_df['trade_date'].dt.date == target_date].copy()
    print(f"\n7. Tuesday's NQ2 data:")
    if tuesday_nq2.empty:
        print("   No NQ2 data found for Tuesday (expected - timetable is for future)")
    else:
        for idx, row in tuesday_nq2.iterrows():
            print(f"   Row {idx}:")
            print(f"     Time: {row.get('Time', 'N/A')}")
            print(f"     Time Change: '{row.get('Time Change', '')}'")
            print(f"     Result: {row.get('Result', 'N/A')}")
    
    # Now simulate what timetable_engine does
    print("\n" + "=" * 80)
    print("SIMULATING TIMETABLE ENGINE LOGIC:")
    print("=" * 80)
    
    trade_date_obj = target_date
    previous_date_obj = trade_date_obj - timedelta(days=1)
    
    print(f"\n8. Timetable engine logic:")
    print(f"   trade_date_obj: {trade_date_obj}")
    print(f"   previous_date_obj: {previous_date_obj}")
    
    # Filter to previous day
    previous_df = master_df[master_df['trade_date'].dt.date == previous_date_obj].copy()
    print(f"\n9. Previous day DataFrame:")
    print(f"   Rows: {len(previous_df)}")
    print(f"   Streams: {sorted(previous_df['Stream'].unique())}")
    
    if previous_df.empty:
        print("   ERROR: Previous day DataFrame is empty!")
        print("   This would trigger fallback to current date logic")
    else:
        # Find NQ2 in previous day
        nq2_prev = previous_df[previous_df['Stream'] == 'NQ2']
        if nq2_prev.empty:
            print("   ERROR: NQ2 not found in previous day DataFrame!")
        else:
            row = nq2_prev.iloc[0]
            print(f"\n10. NQ2 row from previous day:")
            print(f"    Time: {row.get('Time', 'N/A')}")
            print(f"    Time Change: '{row.get('Time Change', '')}'")
            
            # Simulate the logic
            time = row.get('Time', '')
            time_change = row.get('Time Change', '')
            
            print(f"\n11. Logic simulation:")
            print(f"    Initial time: {time}")
            print(f"    Time Change: '{time_change}'")
            print(f"    Time Change type: {type(time_change)}")
            print(f"    Time Change is None: {time_change is None}")
            print(f"    Time Change == '': {time_change == ''}")
            print(f"    Time Change bool: {bool(time_change)}")
            
            if time_change and str(time_change).strip():
                time_change_str = str(time_change).strip()
                print(f"    -> Time Change exists: '{time_change_str}'")
                if '->' in time_change_str:
                    parts = time_change_str.split('->')
                    if len(parts) == 2:
                        final_time = parts[1].strip()
                        print(f"    -> Parsed from 'old -> new' format: {final_time}")
                    else:
                        final_time = time_change_str
                        print(f"    -> Using Time Change directly: {final_time}")
                else:
                    final_time = time_change_str
                    print(f"    -> Using Time Change directly: {final_time}")
            else:
                final_time = time
                print(f"    -> Time Change is empty, using Time: {final_time}")
            
            print(f"\n12. FINAL TIME FOR TUESDAY: {final_time}")
    
    # Now actually call the timetable engine
    print("\n" + "=" * 80)
    print("CALLING ACTUAL TIMETABLE ENGINE:")
    print("=" * 80)
    
    engine = TimetableEngine()
    
    # Load stream filters if they exist
    stream_filters = None
    try:
        import json
        filters_path = Path("configs/stream_filters.json")
        if filters_path.exists():
            with open(filters_path, 'r') as f:
                stream_filters = json.load(f)
            print(f"\n13. Loaded stream filters from {filters_path}")
    except Exception as e:
        print(f"\n13. No stream filters found: {e}")
    
    try:
        engine.write_execution_timetable_from_master_matrix(
            master_df,
            trade_date='2026-01-27',
            stream_filters=stream_filters
        )
        print("\n14. Timetable engine completed successfully")
        
        # Read the generated file
        timetable_path = Path("data/timetable/timetable_current.json")
        if timetable_path.exists():
            import json
            with open(timetable_path, 'r') as f:
                timetable_data = json.load(f)
            
            print("\n15. Generated timetable file:")
            for stream in timetable_data.get('streams', []):
                if stream.get('stream') == 'NQ2':
                    print(f"    NQ2:")
                    print(f"      slot_time: {stream.get('slot_time')}")
                    print(f"      decision_time: {stream.get('decision_time')}")
                    print(f"      enabled: {stream.get('enabled')}")
    except Exception as e:
        print(f"\n14. ERROR calling timetable engine: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    main()
