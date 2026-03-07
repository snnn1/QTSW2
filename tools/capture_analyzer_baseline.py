#!/usr/bin/env python3
"""
Capture analyzer baseline for performance optimization regression testing.
Run before any optimizations; compare after each phase to ensure identical output.
"""

import re
import sys
import json
import hashlib
from pathlib import Path
from datetime import datetime

QTSW2_ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(QTSW2_ROOT / "modules" / "analyzer"))

import pandas as pd
from breakout_core.engine import run_strategy
from logic.config_logic import RunParams, ConfigManager


def hash_dataframe(df: pd.DataFrame) -> str:
    """Create a hash of DataFrame contents for comparison"""
    if df.empty:
        return hashlib.md5(b"empty").hexdigest()
    if "Date" not in df.columns or "Time" not in df.columns:
        df_str = df.to_string(index=False)
        return hashlib.md5(df_str.encode()).hexdigest()
    df_sorted = df.sort_values(["Date", "Time"]).reset_index(drop=True)
    df_str = df_sorted.to_string(index=False)
    return hashlib.md5(df_str.encode()).hexdigest()


REQUIRED_COLUMNS = ["timestamp", "open", "high", "low", "close", "instrument"]
_DATE_FROM_FILENAME = re.compile(r"_(\d{4})-(\d{2})-(\d{2})\.parquet$")

def _parse_date_from_filename(file_path: Path):
    m = _DATE_FROM_FILENAME.search(file_path.name)
    return (int(m.group(1)), int(m.group(2)), int(m.group(3))) if m else None

def load_data(folder: Path, instrument: str, start_date=None, end_date=None) -> pd.DataFrame:
    """Load parquet data from folder for instrument (column-selective for memory)."""
    parts = []
    instrument_dir = folder / instrument.upper() / "1m"
    if not instrument_dir.exists():
        raise FileNotFoundError(f"Data folder not found: {instrument_dir}")
    for year_dir in sorted(instrument_dir.iterdir()):
        if not year_dir.is_dir() or not year_dir.name.isdigit():
            continue
        for month_dir in sorted(year_dir.iterdir()):
            if not month_dir.is_dir():
                continue
            for f in sorted(month_dir.glob("*.parquet")):
                if start_date or end_date:
                    parsed = _parse_date_from_filename(f)
                    if parsed:
                        try:
                            from datetime import date
                            fd = date(parsed[0], parsed[1], parsed[2])
                            if start_date and fd < start_date:
                                continue
                            if end_date and fd > end_date:
                                continue
                        except ValueError:
                            pass
                parts.append(pd.read_parquet(f, columns=REQUIRED_COLUMNS))
    if not parts:
        raise FileNotFoundError(f"No parquet files in {instrument_dir}")
    df = pd.concat(parts, ignore_index=True)
    req = {"timestamp", "open", "high", "low", "close", "instrument"}
    if "instrument" not in df.columns:
        df["instrument"] = instrument.upper()
    missing = req - set(df.columns)
    if missing:
        raise ValueError(f"Missing columns: {missing}")
    return df


def capture_baseline(
    data_folder: Path,
    instrument: str = "ES",
    output_file: Path = None,
    show_progress: bool = False,
    start_date=None,
    end_date=None,
) -> dict:
    """Capture baseline results from current analyzer."""
    if output_file is None:
        output_file = QTSW2_ROOT / "modules" / "analyzer" / "tests" / "baseline_results.json"

    print(f"Loading data from {data_folder} for {instrument}...")
    df = load_data(data_folder, instrument, start_date=start_date, end_date=end_date)
    print(f"Loaded {len(df):,} rows")

    config = ConfigManager()
    rp = RunParams(
        instrument=instrument,
        enabled_sessions=["S1", "S2"],
        enabled_slots={
            "S1": config.get_slot_ends("S1"),
            "S2": config.get_slot_ends("S2"),
        },
        trade_days=[0, 1, 2, 3, 4],
        same_bar_priority="STOP_FIRST",
        write_setup_rows=False,
        write_no_trade_rows=True,
    )

    print("Running analyzer...")
    results = run_strategy(df, rp, debug=False, show_progress=show_progress)
    print(f"Generated {len(results)} result rows")

    baseline = {
        "timestamp": datetime.now().isoformat(),
        "data_folder": str(data_folder),
        "instrument": instrument,
        "total_rows": len(results),
        "hash": hash_dataframe(results),
        "columns": list(results.columns),
        "sample_data": results.head(20).to_dict("records") if not results.empty else [],
        "summary": {
            "wins": int((results["Result"] == "Win").sum()) if not results.empty else 0,
            "losses": int((results["Result"] == "Loss").sum()) if not results.empty else 0,
            "be": int((results["Result"] == "BE").sum()) if not results.empty else 0,
            "notrade": int((results["Result"] == "NoTrade").sum()) if not results.empty else 0,
            "total_profit": float(results["Profit"].sum()) if not results.empty else 0.0,
        },
    }

    output_file.parent.mkdir(parents=True, exist_ok=True)
    with open(output_file, "w") as f:
        json.dump(baseline, f, indent=2, default=str)

    results_parquet = output_file.with_suffix(".parquet")
    results.to_parquet(results_parquet, index=False)

    print(f"Baseline saved: {output_file}")
    print(f"  Hash: {baseline['hash'][:16]}...")
    print(f"  Rows: {baseline['total_rows']}")
    return baseline


def compare_with_baseline(
    current_results: pd.DataFrame,
    baseline_file: Path,
) -> dict:
    """Compare current results with baseline. Returns dict with match status."""
    with open(baseline_file) as f:
        baseline = json.load(f)
    current_hash = hash_dataframe(current_results)
    return {
        "hash_match": current_hash == baseline["hash"],
        "row_count_match": len(current_results) == baseline["total_rows"],
        "current_hash": current_hash,
        "baseline_hash": baseline["hash"],
    }


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="Capture analyzer baseline")
    parser.add_argument("--folder", type=Path, default=QTSW2_ROOT / "data" / "translated")
    parser.add_argument("--instrument", type=str, default="ES")
    parser.add_argument("--output", type=Path, default=None)
    parser.add_argument("--progress", action="store_true", help="Show progress")
    parser.add_argument("--start-date", type=str, default=None, help="Start date YYYY-MM-DD (skip files before)")
    parser.add_argument("--end-date", type=str, default=None, help="End date YYYY-MM-DD (skip files after)")
    args = parser.parse_args()
    try:
        start_date = datetime.strptime(args.start_date, "%Y-%m-%d").date() if args.start_date else None
        end_date = datetime.strptime(args.end_date, "%Y-%m-%d").date() if args.end_date else None
    except ValueError as e:
        raise SystemExit(f"Invalid date format (use YYYY-MM-DD): {e}")
    capture_baseline(args.folder, args.instrument, args.output, args.progress,
                    start_date=start_date, end_date=end_date)
