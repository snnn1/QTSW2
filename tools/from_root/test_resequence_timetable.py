#!/usr/bin/env python3
"""
Test what happens to timetable when resequencing.
"""

import sys
from pathlib import Path
import pandas as pd

# Add project root to path
project_root = Path(__file__).parent
sys.path.insert(0, str(project_root))

from modules.matrix.file_manager import load_existing_matrix
from modules.matrix.master_matrix import MasterMatrix
import json

def main():
    """Test resequence timetable generation."""
    print("=" * 80)
    print("TESTING RESEQUENCE TIMETABLE GENERATION")
    print("=" * 80)
    
    # Check current timetable
    timetable_path = Path("data/timetable/timetable_current.json")
    if timetable_path.exists():
        with open(timetable_path, 'r') as f:
            timetable_before = json.load(f)
        
        nq2_before = next((s for s in timetable_before.get('streams', []) if s.get('stream') == 'NQ2'), None)
        print(f"\n1. Timetable BEFORE resequence:")
        if nq2_before:
            print(f"   NQ2 slot_time: {nq2_before.get('slot_time')}")
            print(f"   NQ2 enabled: {nq2_before.get('enabled')}")
        print(f"   as_of: {timetable_before.get('as_of')}")
    
    # Load master matrix
    master_df = load_existing_matrix("data/master_matrix")
    print(f"\n2. Master matrix loaded: {len(master_df)} rows")
    
    # Check Monday's NQ2
    monday_date = pd.to_datetime('2026-01-26').date()
    monday_nq2 = master_df[(master_df['Stream'] == 'NQ2') & (master_df['trade_date'].dt.date == monday_date)]
    if not monday_nq2.empty:
        row = monday_nq2.iloc[0]
        print(f"\n3. Monday's NQ2 in master matrix:")
        print(f"   Time: {row.get('Time')}")
        print(f"   Time Change: '{row.get('Time Change', '')}'")
        print(f"   Result: {row.get('Result')}")
    
    # Simulate what save_master_matrix does
    print("\n4. Simulating save_master_matrix (what resequence calls):")
    from modules.matrix.file_manager import save_master_matrix
    
    # This should trigger timetable generation
    try:
        # Save a copy (this will trigger timetable generation)
        # But we don't want to actually modify the file, so let's just check the logic
        print("   Would call: save_master_matrix(df, output_dir, None, stream_filters)")
        print("   Which calls: engine.write_execution_timetable_from_master_matrix()")
        print("   This should use the FIXED logic (previous day's row)")
    except Exception as e:
        print(f"   Error: {e}")
    
    # Check timetable after (if it was regenerated)
    if timetable_path.exists():
        with open(timetable_path, 'r') as f:
            timetable_after = json.load(f)
        
        nq2_after = next((s for s in timetable_after.get('streams', []) if s.get('stream') == 'NQ2'), None)
        print(f"\n5. Timetable AFTER (if regenerated):")
        if nq2_after:
            print(f"   NQ2 slot_time: {nq2_after.get('slot_time')}")
            print(f"   NQ2 enabled: {nq2_after.get('enabled')}")
        print(f"   as_of: {timetable_after.get('as_of')}")

if __name__ == "__main__":
    main()
