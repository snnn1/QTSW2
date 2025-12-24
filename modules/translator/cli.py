#!/usr/bin/env python3
"""
QTSW2 Translator CLI

Deterministic command-line interface for translating raw exporter data
into canonical quant-grade Parquet files.

Responsibilities:
- Argument parsing
- Execution orchestration
- Exit codes

NO business logic lives here.
"""

import sys
import argparse
from pathlib import Path
from datetime import datetime, timedelta, date as Date

# Correct import: CLI talks ONLY to translate_day
from .core import translate_day


# ============================================================
# Helpers
# ============================================================

def parse_date(value: str) -> Date:
    try:
        return datetime.strptime(value, "%Y-%m-%d").date()
    except ValueError:
        raise argparse.ArgumentTypeError(
            f"Invalid date format: {value} (expected YYYY-MM-DD)"
        )


def date_range(start: Date, end: Date):
    current = start
    while current <= end:
        yield current
        current += timedelta(days=1)


# ============================================================
# Commands
# ============================================================

def cmd_rebuild_missing(args: argparse.Namespace) -> int:
    """
    Rebuild all missing translated files in a bounded date range.

    Exit codes:
        0 -> success
        2 -> partial or total failure
    """
    instrument = args.instrument.upper()
    raw_root = Path(args.raw_root)
    output_root = Path(args.output_root)

    start = args.from_date
    end = args.to_date

    failures = 0
    processed = 0

    for day in date_range(start, end):
        try:
            ok = translate_day(
                instrument=instrument,
                day=day,
                raw_root=raw_root,
                output_root=output_root,
                overwrite=False,   # idempotent by design
            )
            if ok:
                processed += 1
        except Exception as e:
            failures += 1
            print(f"[ERROR] {instrument} {day}: {e}", file=sys.stderr)

    if failures > 0:
        print(
            f"[FAILED] {failures} day(s) failed, {processed} succeeded",
            file=sys.stderr,
        )
        return 2

    print(f"[OK] {processed} day(s) processed successfully")
    return 0


# ============================================================
# Main
# ============================================================

def main() -> int:
    parser = argparse.ArgumentParser(
        prog="translator",
        description="QTSW2 Raw Data Translator CLI",
    )

    subparsers = parser.add_subparsers(dest="command", required=True)

    rebuild = subparsers.add_parser(
        "rebuild-missing",
        help="Translate missing days only (safe for automation)",
    )
    rebuild.add_argument("--instrument", required=True, help="Instrument symbol (ES, NQ, CL, etc)")
    rebuild.add_argument("--from", dest="from_date", type=parse_date, required=True)
    rebuild.add_argument("--to", dest="to_date", type=parse_date, required=True)
    rebuild.add_argument("--raw-root", required=True)
    rebuild.add_argument("--output-root", required=True)
    rebuild.set_defaults(func=cmd_rebuild_missing)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
