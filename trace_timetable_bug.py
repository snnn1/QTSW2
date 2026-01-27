#!/usr/bin/env python3
"""
Trace exactly what happens when timetable is generated.
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
import logging

# Enable detailed logging
logging.basicConfig(level=logging.DEBUG, format='%(asctime)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

def main():
    """Trace timetable generation."""
    print("=" * 80)
    print("TRACING TIMETABLE GENERATION")
    print("=" * 80)
    
    # Load master matrix
    master_df = load_existing_matrix("data/master_matrix")
    print(f"\n1. Master matrix: {len(master_df)} rows")
    
    # Check latest date
    latest_date = master_df['trade_date'].max()
    print(f"\n2. Latest date in matrix: {latest_date}")
    print(f"   Latest date type: {type(latest_date)}")
    if hasattr(latest_date, 'date'):
        print(f"   Latest date.date(): {latest_date.date()}")
    
    # Check NQ2 for latest date
    latest_date_obj = latest_date.date() if hasattr(latest_date, 'date') else pd.to_datetime(latest_date).date()
    latest_nq2 = master_df[(master_df['Stream'] == 'NQ2') & (master_df['trade_date'].dt.date == latest_date_obj)]
    print(f"\n3. NQ2 for latest date ({latest_date_obj}):")
    if latest_nq2.empty:
        print("   No NQ2 data for latest date")
    else:
        for idx, row in latest_nq2.iterrows():
            print(f"   Time: {row.get('Time')}")
            print(f"   Time Change: '{row.get('Time Change', '')}'")
            print(f"   Result: {row.get('Result')}")
    
    # Check previous day
    previous_date_obj = latest_date_obj - timedelta(days=1)
    print(f"\n4. Previous date: {previous_date_obj}")
    prev_nq2 = master_df[(master_df['Stream'] == 'NQ2') & (master_df['trade_date'].dt.date == previous_date_obj)]
    print(f"   NQ2 for previous date:")
    if prev_nq2.empty:
        print("   No NQ2 data for previous date!")
    else:
        for idx, row in prev_nq2.iterrows():
            print(f"   Time: {row.get('Time')}")
            print(f"   Time Change: '{row.get('Time Change', '')}'")
            print(f"   Result: {row.get('Result')}")
    
    # Now call the actual function with None (like resequence does)
    print("\n" + "=" * 80)
    print("CALLING write_execution_timetable_from_master_matrix with trade_date=None")
    print("=" * 80)
    
    engine = TimetableEngine()
    
    # Add detailed logging by monkey-patching
    original_write = engine._write_execution_timetable_file
    
    def logged_write(streams, trade_date):
        print(f"\n5. _write_execution_timetable_file called:")
        print(f"   trade_date: {trade_date}")
        print(f"   streams count: {len(streams)}")
        nq2_stream = next((s for s in streams if s.get('stream') == 'NQ2'), None)
        if nq2_stream:
            print(f"   NQ2 slot_time: {nq2_stream.get('slot_time')}")
            print(f"   NQ2 decision_time: {nq2_stream.get('decision_time')}")
        return original_write(streams, trade_date)
    
    engine._write_execution_timetable_file = logged_write
    
    try:
        engine.write_execution_timetable_from_master_matrix(
            master_df,
            trade_date=None,  # This is what resequence passes
            stream_filters=None
        )
        print("\n6. Timetable generation completed")
    except Exception as e:
        print(f"\n6. ERROR: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    main()
