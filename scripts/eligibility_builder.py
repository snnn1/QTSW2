#!/usr/bin/env python3
"""
Session Eligibility Builder — Standalone script for 18:00 CT freeze.

Writes eligibility_<trading_date>.json from master matrix. Never overwrites.
Invoke via cron or pipeline at 18:00 CT. Robot fails closed without eligibility.

Usage:
  python scripts/eligibility_builder.py [--date YYYY-MM-DD] [--force]
  --date: Target trading date (default: next day if run at/after 18:00 CT, else today)
  --force: Overwrite existing eligibility (for testing only; violates invariant)
"""

import argparse
import hashlib
import json
import logging
import sys
from pathlib import Path
from datetime import datetime, timezone

import pandas as pd

# Add project root for imports
sys.path.insert(0, str(Path(__file__).parent.parent))

from modules.matrix.file_manager import load_existing_matrix
from modules.timetable.timetable_engine import TimetableEngine
from modules.timetable.eligibility_writer import write_eligibility_file
from modules.timetable.cme_session import get_trading_date_cme

logging.basicConfig(level=logging.INFO, format="%(asctime)s - %(levelname)s - %(message)s")
logger = logging.getLogger(__name__)


def main() -> int:
    parser = argparse.ArgumentParser(description="Session Eligibility Builder (18:00 CT freeze)")
    parser.add_argument(
        "--date",
        type=str,
        help="Trading date YYYY-MM-DD (default: today or next day if 18:00+)",
    )
    parser.add_argument(
        "--output-dir",
        type=str,
        default="data/timetable",
        help="Output directory for eligibility files",
    )
    parser.add_argument(
        "--master-matrix-dir",
        type=str,
        default="data/master_matrix",
        help="Directory containing master matrix files",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="Overwrite existing eligibility (testing only; violates invariant)",
    )
    args = parser.parse_args()

    # CME rule: UTC → Chicago, >= 18:00 CT → next day
    trading_date = args.date or get_trading_date_cme(datetime.now(timezone.utc))
    logger.info(f"Eligibility builder: target trading_date={trading_date}")

    output_path = Path(args.output_dir)
    output_path.mkdir(parents=True, exist_ok=True)
    eligibility_path = output_path / f"eligibility_{trading_date}.json"

    # If file exists, exit immediately (do not overwrite)
    if eligibility_path.exists() and not args.force:
        logger.info(
            f"SESSION_ELIGIBILITY_SKIP: eligibility_{trading_date}.json already exists, "
            "never overwrite (immutable per trading_date). Use --force for testing."
        )
        return 0

    if args.force and eligibility_path.exists():
        logger.warning(f"FORCE: overwriting existing eligibility_{trading_date}.json (testing only)")
        eligibility_path.unlink()

    # Load master matrix
    matrix_df = load_existing_matrix(args.master_matrix_dir)
    if matrix_df.empty:
        logger.warning("Master matrix is empty; will write all streams disabled (MATRIX_DATE_MISSING)")

    engine = TimetableEngine(
        master_matrix_dir=args.master_matrix_dir,
        analyzer_runs_dir="data/analyzed",
    )
    # Build from matrix (execution_mode=False) — eligibility builder derives from matrix, not artifact
    streams = engine.build_streams_from_master_matrix(
        matrix_df, trade_date=trading_date, execution_mode=False
    )

    if not streams:
        logger.error("No streams built; cannot write eligibility")
        return 1

    # Compute matrix hash for audit
    matrix_hash = None
    if not matrix_df.empty:
        try:
            matrix_hash = hashlib.sha256(
                pd.util.hash_pandas_object(matrix_df).values.tobytes()
            ).hexdigest()[:16]
        except Exception:
            pass

    result = write_eligibility_file(
        streams=streams,
        trading_date=trading_date,
        output_dir=str(output_path),
        source_matrix_hash=matrix_hash,
    )
    if result:
        eligible_count = sum(1 for s in streams if s.get("enabled"))
        logger.info(
            f"SESSION_ELIGIBILITY_FROZEN: trading_date={trading_date}, "
            f"freeze_time_utc={datetime.now(timezone.utc).isoformat()}, "
            f"matrix_hash={matrix_hash or 'none'}, eligible_stream_count={eligible_count}"
        )
        return 0
    return 0  # Already existed (skipped)


if __name__ == "__main__":
    sys.exit(main())
