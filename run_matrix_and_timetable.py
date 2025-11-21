"""
Combined script to run Master Matrix and Timetable Engine
"""

import sys
from pathlib import Path
from datetime import date, datetime
import argparse

# Add project root to path
project_root = Path(__file__).parent
sys.path.insert(0, str(project_root))

from master_matrix.master_matrix import MasterMatrix
from timetable_engine.timetable_engine import TimetableEngine


def main():
    parser = argparse.ArgumentParser(
        description='Run Master Matrix and Timetable Engine',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Build master matrix for all data
  python run_matrix_and_timetable.py --matrix-only
  
  # Build master matrix for specific date range
  python run_matrix_and_timetable.py --matrix-only --start-date 2024-01-01 --end-date 2024-12-31
  
  # Generate timetable for today
  python run_matrix_and_timetable.py --timetable-only
  
  # Generate timetable for specific date
  python run_matrix_and_timetable.py --timetable-only --date 2024-11-21
  
  # Run both (build matrix then generate timetable)
  python run_matrix_and_timetable.py --date 2024-11-21
        """
    )
    
    parser.add_argument('--matrix-only', action='store_true',
                       help='Only build master matrix')
    parser.add_argument('--timetable-only', action='store_true',
                       help='Only generate timetable')
    parser.add_argument('--start-date', type=str,
                       help='Start date for master matrix (YYYY-MM-DD)')
    parser.add_argument('--end-date', type=str,
                       help='End date for master matrix (YYYY-MM-DD)')
    parser.add_argument('--date', type=str,
                       help='Specific date for matrix/timetable (YYYY-MM-DD)')
    parser.add_argument('--sequencer-runs-dir', type=str, default='data/sequencer_runs',
                       help='Directory containing sequential processor output files')
    parser.add_argument('--matrix-output-dir', type=str, default='data/master_matrix',
                       help='Output directory for master matrix files')
    parser.add_argument('--timetable-output-dir', type=str, default='data/timetable',
                       help='Output directory for timetable files')
    parser.add_argument('--scf-threshold', type=float, default=0.5,
                       help='SCF threshold for blocking trades')
    
    args = parser.parse_args()
    
    # Determine what to run
    run_matrix = not args.timetable_only
    run_timetable = not args.matrix_only
    
    # Build master matrix
    if run_matrix:
        print("=" * 80)
        print("BUILDING MASTER MATRIX")
        print("=" * 80)
        
        matrix = MasterMatrix(sequencer_runs_dir=args.sequencer_runs_dir)
        master_df = matrix.build_master_matrix(
            start_date=args.start_date,
            end_date=args.end_date,
            specific_date=args.date,
            output_dir=args.matrix_output_dir,
            sequencer_runs_dir=args.sequencer_runs_dir
        )
        
        if master_df.empty:
            print("WARNING: Master matrix is empty!")
        else:
            print(f"\nMaster Matrix Summary:")
            print(f"  Total trades: {len(master_df)}")
            print(f"  Date range: {master_df['trade_date'].min()} to {master_df['trade_date'].max()}")
            print(f"  Streams: {sorted(master_df['Stream'].unique())}")
            print(f"  Instruments: {sorted(master_df['Instrument'].unique())}")
            print(f"  Allowed trades: {master_df['final_allowed'].sum()} / {len(master_df)}")
    
    # Generate timetable
    if run_timetable:
        print("\n" + "=" * 80)
        print("GENERATING TIMETABLE")
        print("=" * 80)
        
        engine = TimetableEngine(
            master_matrix_dir=args.matrix_output_dir,
            analyzer_runs_dir=args.analyzer_runs_dir
        )
        engine.scf_threshold = args.scf_threshold
        
        timetable_df = engine.generate_timetable(trade_date=args.date)
        
        if timetable_df.empty:
            print("WARNING: Timetable is empty!")
        else:
            parquet_file, json_file = engine.save_timetable(
                timetable_df, args.timetable_output_dir
            )
            
            print(f"\nTimetable Summary:")
            print(f"  Date: {timetable_df['trade_date'].iloc[0]}")
            print(f"  Total entries: {len(timetable_df)}")
            print(f"  Allowed trades: {timetable_df['allowed'].sum()}")
            print(f"\nTimetable Preview:")
            print(timetable_df[['symbol', 'stream_id', 'session', 'selected_time', 
                               'allowed', 'reason']].to_string(index=False))
            print(f"\nFiles saved:")
            print(f"  - {parquet_file}")
            print(f"  - {json_file}")
    
    print("\n" + "=" * 80)
    print("COMPLETE")
    print("=" * 80)


if __name__ == "__main__":
    main()

