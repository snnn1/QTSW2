"""
Matrix Copy - Full rebuild to data/_copy/ only. Zero production touch.

Writes ONLY to:
  - data/_copy/master_matrix/
  - data/_copy/timetable/timetable_copy.json

Never references: data/master_matrix/, data/timetable/, timetable_current.json
"""

import sys
from pathlib import Path

# Hardcoded output paths - must contain _copy
MATRIX_OUTPUT_DIR = "data/_copy/master_matrix"
TIMETABLE_OUTPUT_DIR = "data/_copy/timetable"
ANALYZER_RUNS_DIR = "data/analyzed"
TIMETABLE_FILENAME = "timetable_copy.json"


def _safety_abort():
    """Fail-closed: abort if any path could touch production."""
    # Matrix: must contain _copy
    if "master_matrix" in MATRIX_OUTPUT_DIR and "_copy" not in MATRIX_OUTPUT_DIR:
        print("SAFETY ABORT: matrix_output_dir would write to production path", file=sys.stderr)
        sys.exit(1)
    # Timetable dir: must contain _copy, must NOT be data/timetable
    if MATRIX_OUTPUT_DIR == "data/master_matrix":
        print("SAFETY ABORT: matrix_output_dir is production path", file=sys.stderr)
        sys.exit(1)
    if TIMETABLE_OUTPUT_DIR == "data/timetable":
        print("SAFETY ABORT: timetable_output_dir is production path", file=sys.stderr)
        sys.exit(1)
    if "_copy" not in TIMETABLE_OUTPUT_DIR:
        print("SAFETY ABORT: timetable_output_dir must contain _copy", file=sys.stderr)
        sys.exit(1)
    # Filename: must NOT be timetable_current.json
    if TIMETABLE_FILENAME == "timetable_current.json":
        print("SAFETY ABORT: timetable filename must not be timetable_current.json", file=sys.stderr)
        sys.exit(1)


def main():
    _safety_abort()

    # Add project root to path
    project_root = Path(__file__).resolve().parents[2]
    sys.path.insert(0, str(project_root))

    import argparse
    from modules.matrix.master_matrix import MasterMatrix

    parser = argparse.ArgumentParser(
        description="Matrix Copy - Full rebuild to data/_copy/ only. Zero production touch.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python run_matrix_copy.py
  python run_matrix_copy.py --start-date 2024-01-01 --end-date 2024-12-31
        """
    )
    parser.add_argument("--start-date", type=str, help="Start date (YYYY-MM-DD)")
    parser.add_argument("--end-date", type=str, help="End date (YYYY-MM-DD)")
    args = parser.parse_args()

    print()
    print("=" * 80)
    print("MATRIX COPY - FULL REBUILD - WRITES ONLY TO data/_copy")
    print("=" * 80)
    print(f"  Matrix output:    {MATRIX_OUTPUT_DIR}")
    print(f"  Timetable output: {TIMETABLE_OUTPUT_DIR}/{TIMETABLE_FILENAME}")
    print(f"  Analyzer input:  {ANALYZER_RUNS_DIR}")
    print("=" * 80)
    print()

    matrix = MasterMatrix(analyzer_runs_dir=ANALYZER_RUNS_DIR)
    master_df = matrix.build_master_matrix(
        start_date=args.start_date,
        end_date=args.end_date,
        output_dir=MATRIX_OUTPUT_DIR,
        timetable_output_dir=TIMETABLE_OUTPUT_DIR,
    )

    if master_df.empty:
        print("WARNING: Master matrix is empty!")
    else:
        print(f"\nMaster Matrix Copy Summary:")
        print(f"  Total trades: {len(master_df)}")
        print(f"  Date range: {master_df['trade_date'].min()} to {master_df['trade_date'].max()}")
        print(f"  Streams: {sorted(master_df['Stream'].unique())}")
        print(f"  Allowed trades: {master_df['final_allowed'].sum()} / {len(master_df)}")
        print(f"\nOutputs written to data/_copy/ only.")

    print()
    print("=" * 80)
    print("COMPLETE")
    print("=" * 80)


if __name__ == "__main__":
    main()
