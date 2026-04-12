#!/usr/bin/env python3
"""
Standalone eligibility builder for 18:00 CT.

Reads timetable_current.json and writes eligibility_<trading_date>.json.
Never overwrites existing eligibility (immutable per trading_date).
Can be scheduled via Windows Task Scheduler for 18:00 CT.

Usage:
  python scripts/build_eligibility_18ct.py [--timetable-dir data/timetable] [--date YYYY-MM-DD]

If --date is omitted, uses trading_date from timetable_current.json.
"""

import argparse
import json
import logging
import sys
from pathlib import Path

# Add project root for imports
sys.path.insert(0, str(Path(__file__).parent.parent))

from modules.timetable.eligibility_writer import write_eligibility_file

logging.basicConfig(level=logging.INFO, format="%(asctime)s - %(levelname)s - %(message)s")
logger = logging.getLogger(__name__)


def main():
    parser = argparse.ArgumentParser(description="Build eligibility file from timetable")
    parser.add_argument(
        "--timetable-dir",
        type=str,
        default="data/timetable",
        help="Directory containing timetable_current.json",
    )
    parser.add_argument(
        "--date",
        type=str,
        default=None,
        help="Trading date (YYYY-MM-DD). If omitted, uses trading_date from timetable.",
    )
    args = parser.parse_args()

    timetable_dir = Path(args.timetable_dir)
    timetable_path = timetable_dir / "timetable_current.json"

    if not timetable_path.exists():
        logger.error("Timetable file not found: %s", timetable_path)
        sys.exit(1)

    with open(timetable_path, "r", encoding="utf-8") as f:
        timetable = json.load(f)

    trading_date = args.date or timetable.get("trading_date")
    if not trading_date:
        logger.error("No trading_date in timetable and --date not provided")
        sys.exit(1)

    streams = timetable.get("streams", [])
    if not streams:
        logger.error("No streams in timetable")
        sys.exit(1)

    # Convert timetable streams to eligibility format (stream, enabled, block_reason)
    eligibility_streams = []
    for s in streams:
        stream_key = s.get("stream", "").strip()
        if not stream_key:
            continue
        enabled = s.get("enabled", False)
        reason = s.get("block_reason") if not enabled else None
        eligibility_streams.append({
            "stream": stream_key,
            "enabled": enabled,
            "block_reason": reason,
        })

    result = write_eligibility_file(
        streams=eligibility_streams,
        session_trading_date=trading_date,
        output_dir=str(timetable_dir),
        source_matrix_hash=None,
    )

    if result:
        logger.info("Eligibility written: %s", result)
    else:
        logger.info("Eligibility already exists for %s (skipped, immutable)", trading_date)


if __name__ == "__main__":
    main()
