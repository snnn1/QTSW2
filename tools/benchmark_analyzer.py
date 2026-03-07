#!/usr/bin/env python3
"""
Benchmark analyzer performance. Run before/after optimizations to measure speedup.
"""

import sys
import time
from pathlib import Path

QTSW2_ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(QTSW2_ROOT / "modules" / "analyzer"))

import pandas as pd
from breakout_core.engine import run_strategy
from logic.config_logic import RunParams, ConfigManager


REQUIRED_COLUMNS = ["timestamp", "open", "high", "low", "close", "instrument"]

def load_data(folder: Path, instrument: str) -> pd.DataFrame:
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
                parts.append(pd.read_parquet(f, columns=REQUIRED_COLUMNS))
    if not parts:
        raise FileNotFoundError(f"No parquet files in {instrument_dir}")
    df = pd.concat(parts, ignore_index=True)
    if "instrument" not in df.columns:
        df["instrument"] = instrument.upper()
    return df


def benchmark(
    data_folder: Path = None,
    instrument: str = "ES",
    runs: int = 3,
    show_progress: bool = False,
) -> dict:
    """Run analyzer benchmark. Returns timing stats."""
    if data_folder is None:
        data_folder = QTSW2_ROOT / "data" / "translated"

    print(f"Loading data from {data_folder} for {instrument}...")
    df = load_data(data_folder, instrument)
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

    times = []
    for i in range(runs):
        print(f"Run {i + 1}/{runs}...", end=" ", flush=True)
        start = time.perf_counter()
        results = run_strategy(df, rp, debug=False, show_progress=show_progress)
        elapsed = time.perf_counter() - start
        times.append(elapsed)
        print(f"{elapsed:.2f}s ({len(results)} results)")

    return {
        "runs": runs,
        "times": times,
        "mean": sum(times) / len(times),
        "min": min(times),
        "max": max(times),
        "rows": len(df),
        "results": len(results),
    }


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="Benchmark analyzer performance")
    parser.add_argument("--folder", type=Path, default=QTSW2_ROOT / "data" / "translated")
    parser.add_argument("--instrument", type=str, default="ES")
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--progress", action="store_true")
    args = parser.parse_args()

    stats = benchmark(args.folder, args.instrument, args.runs, args.progress)
    print(f"\nBenchmark summary:")
    print(f"  Mean: {stats['mean']:.2f}s")
    print(f"  Min:  {stats['min']:.2f}s")
    print(f"  Max:  {stats['max']:.2f}s")
    print(f"  Data: {stats['rows']:,} rows -> {stats['results']} results")
