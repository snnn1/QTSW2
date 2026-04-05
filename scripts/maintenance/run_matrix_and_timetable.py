"""
Combined script to run Master Matrix and Timetable Engine
"""

import sys
from pathlib import Path
from datetime import date, datetime
import argparse

# Add project root to path
project_root = Path(__file__).resolve().parents[2]  # maintenance -> scripts -> repo root
sys.path.insert(0, str(project_root))

from modules.matrix.master_matrix import MasterMatrix
from modules.timetable.timetable_engine import TimetableEngine


def main():
    parser = argparse.ArgumentParser(
        description='Run Master Matrix and Timetable Engine',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Rolling resequence (default for daily updates - fast)
  python run_matrix_and_timetable.py --resequence
  
  # Build master matrix for all data (full rebuild)
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
    parser.add_argument('--analyzer-runs-dir', type=str, default='data/analyzed',
                       help='Directory containing analyzer output files')
    parser.add_argument('--matrix-output-dir', type=str, default='data/master_matrix',
                       help='Output directory for master matrix files')
    parser.add_argument('--timetable-output-dir', type=str, default='data/timetable',
                       help='Output directory for timetable files')
    parser.add_argument('--scf-threshold', type=float, default=0.5,
                       help='SCF threshold for blocking trades')
    parser.add_argument('--resequence', action='store_true',
                       help='Use rolling resequence instead of full rebuild (faster for daily updates)')
    parser.add_argument('--resequence-days', type=int, default=40,
                       help='Number of trading days to resequence when --resequence is used (default 40)')
    
    args = parser.parse_args()
    
    # Determine what to run
    run_matrix = not args.timetable_only
    run_timetable = not args.matrix_only
    master_df = None  # Set when run_matrix; used for matrix-first timetable path
    
    # Build master matrix (resequence by default for incremental, full rebuild with --matrix-only)
    if run_matrix:
        print("=" * 80)
        if args.resequence:
            print("ROLLING RESEQUENCE (last %d trading days)" % args.resequence_days)
        else:
            print("BUILDING MASTER MATRIX")
        print("=" * 80)
        
        matrix = MasterMatrix(analyzer_runs_dir=args.analyzer_runs_dir)
        if args.resequence:
            master_df, summary = matrix.build_master_matrix_rolling_resequence(
                resequence_days=args.resequence_days,
                output_dir=args.matrix_output_dir,
                stream_filters=None,
                analyzer_runs_dir=args.analyzer_runs_dir
            )
            if "error" in summary:
                print("ERROR:", summary["error"])
                sys.exit(1)
            print(f"  Preserved: {summary.get('rows_preserved', 0)}, Resequenced: {summary.get('rows_resequenced', 0)}")
        else:
            master_df = matrix.build_master_matrix(
                start_date=args.start_date,
                end_date=args.end_date,
                specific_date=args.date,
                output_dir=args.matrix_output_dir,
                analyzer_runs_dir=args.analyzer_runs_dir
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
        
        timetable_df = None

        if run_matrix and master_df is not None and not master_df.empty:
            engine = TimetableEngine(
                master_matrix_dir=args.matrix_output_dir,
                analyzer_runs_dir=args.analyzer_runs_dir,
                project_root=str(project_root),
            )
            engine.scf_threshold = args.scf_threshold
            from modules.matrix.file_manager import set_current_master_matrix_df

            set_current_master_matrix_df(master_df)
            engine.write_execution_timetable_from_master_matrix(
                master_df,
                trade_date=args.date,
                execution_mode=True,
                publish_context={
                    "source": "cli",
                    "reason": "run_matrix_and_timetable",
                    "caller": "scripts/maintenance/run_matrix_and_timetable.py",
                    "matrix_source": "in_memory",
                },
            )
            timetable_df = engine.build_timetable_dataframe_from_master_matrix(
                master_df, trade_date=args.date, execution_mode=True
            )
        elif not run_matrix:
            from modules.matrix.file_manager import load_existing_matrix
            loaded_df = load_existing_matrix(args.matrix_output_dir)
            if loaded_df.empty:
                print(
                    "ERROR: No master matrix on disk — legacy analyzer timetable path is disabled.",
                    file=sys.stderr,
                )
                sys.exit(1)
            engine = TimetableEngine(
                master_matrix_dir=args.matrix_output_dir,
                analyzer_runs_dir=args.analyzer_runs_dir,
                project_root=str(project_root),
            )
            engine.scf_threshold = args.scf_threshold
            print(
                "  --timetable-only: building dataframe + timestamped parquet/JSON only. "
                "Live timetable_current.json requires running both matrix + timetable "
                "(execution_mode=True) so eligibility aligns with the publish dir."
            )
            timetable_df = engine.build_timetable_dataframe_from_master_matrix(
                loaded_df, trade_date=args.date, execution_mode=False
            )
        else:
            print(
                "ERROR: Master matrix is empty — cannot publish live timetable_current.json. "
                "Build matrix data first.",
                file=sys.stderr,
            )
            sys.exit(1)
        
        if timetable_df is None or timetable_df.empty:
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
