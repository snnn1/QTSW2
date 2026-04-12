#!/usr/bin/env python3
"""
Regenerate timetable **artifacts** from the existing master matrix (parquet + JSON under output dir).

Does **not** write live ``data/timetable/timetable_current.json``. For that, run::

  python scripts/maintenance/run_matrix_and_timetable.py --resequence
"""

import sys
from pathlib import Path

project_root = Path(__file__).parent.resolve()
sys.path.insert(0, str(project_root))

from modules.matrix.file_manager import load_existing_matrix
from modules.timetable.timetable_engine import TimetableEngine
import logging

logging.basicConfig(level=logging.INFO, format="%(asctime)s - %(levelname)s - %(message)s")
logger = logging.getLogger(__name__)

SCRATCH_OUT = "data/_scratch/timetable_regen"


def main():
    logger.info("=" * 80)
    logger.info("REGENERATE TIMETABLE ARTIFACTS (non-live)")
    logger.info("=" * 80)

    master_matrix_dir = "data/master_matrix"
    logger.info("Loading master matrix from %s...", master_matrix_dir)
    master_df = load_existing_matrix(master_matrix_dir)
    if master_df.empty:
        logger.error("No master matrix found. Build matrix first.")
        return 1
    if "trade_date" not in master_df.columns:
        logger.error("Master matrix missing trade_date column.")
        return 1

    latest_date = master_df["trade_date"].max()
    td = latest_date.strftime("%Y-%m-%d") if hasattr(latest_date, "strftime") else str(latest_date)[:10]
    logger.info("Using matrix date: %s", td)

    stream_filters = None
    try:
        import json
        filters_path = Path("configs/stream_filters.json")
        if filters_path.exists():
            with open(filters_path, "r", encoding="utf-8") as f:
                stream_filters = json.load(f)
    except Exception as e:
        logger.warning("Stream filters not loaded: %s", e)

    out_dir = project_root / SCRATCH_OUT
    out_dir.mkdir(parents=True, exist_ok=True)

    engine = TimetableEngine(project_root=str(project_root))
    df = engine.build_timetable_dataframe_from_master_matrix(
        master_df, trade_date=td, stream_filters=stream_filters, execution_mode=False
    )
    if df.empty:
        logger.error("Built empty timetable dataframe.")
        return 1
    pq, js = engine.save_timetable(df, str(out_dir))
    logger.info("SUCCESS: wrote %s and %s", pq, js)
    logger.info("Live timetable_current.json is not modified — use run_matrix_and_timetable for publish.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
