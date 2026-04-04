#!/usr/bin/env python3
"""
Trace timetable generation from master matrix (read-only / scratch artifacts only).

Does not write data/timetable/timetable_current.json.
"""

import sys
from pathlib import Path

project_root = Path(__file__).parent
sys.path.insert(0, str(project_root))

from modules.matrix.file_manager import load_existing_matrix
from modules.timetable.timetable_engine import TimetableEngine
import logging

logging.basicConfig(level=logging.DEBUG, format="%(asctime)s - %(levelname)s - %(message)s")


def main():
    print("=" * 80)
    print("TRACING TIMETABLE GENERATION (non-live)")
    print("=" * 80)

    master_df = load_existing_matrix("data/master_matrix")
    print(f"\n1. Master matrix: {len(master_df)} rows")

    latest_date = master_df["trade_date"].max()
    print(f"\n2. Latest date in matrix: {latest_date}")

    scratch = project_root / "data" / "_scratch" / "trace_timetable"
    scratch.mkdir(parents=True, exist_ok=True)
    engine = TimetableEngine(project_root=str(project_root))

    td = latest_date.strftime("%Y-%m-%d") if hasattr(latest_date, "strftime") else str(latest_date)[:10]
    df = engine.build_timetable_dataframe_from_master_matrix(
        master_df,
        trade_date=td,
        stream_filters=None,
        execution_mode=False,
    )
    pq, js = engine.save_timetable(df, str(scratch))
    print(f"\n3. Saved scratch artifacts:\n   {pq}\n   {js}")


if __name__ == "__main__":
    main()
