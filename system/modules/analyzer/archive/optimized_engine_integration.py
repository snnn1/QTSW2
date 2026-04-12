"""
Research Runner Integration (Quant-Safe)

This module integrates safe data-layer utilities (caching + preprocessing)
with the canonical Analyzer WITHOUT introducing alternative trading logic paths.

Hard rules:
- Always call breakout_core.engine.run_strategy as the single source of truth
- No parallel trade simulation
- No algorithm substitution (no "optimized entry/range/MFE")
- Benchmarking measures performance only; correctness is defined by the canonical engine
"""

from __future__ import annotations

import sys
import time
from pathlib import Path
from typing import Dict, List, Optional, Any, Tuple

import pandas as pd

# Add project root to PYTHONPATH (dev convenience; prefer proper packaging long-term)
ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.append(str(ROOT))

from breakout_core.engine import run_strategy
from logic.config_logic import RunParams

# SAFE utilities only
from optimizations.caching_optimizer import CachingOptimizer
from optimizations.data_processing_optimizer import DataProcessingOptimizer


class ResearchRunner:
    """
    Quant-safe runner for the canonical Analyzer.

    This class can:
    - load data with caching
    - apply safe preprocessing (dtype + time fields)
    - run the canonical analyzer
    - benchmark timing

    It cannot:
    - alter trading semantics
    - run alternative engines
    """

    def __init__(
        self,
        enable_preprocessing: bool = True,
        enable_caching: bool = True,
        downcast_price_columns: bool = False,
    ):
        self.enable_preprocessing = enable_preprocessing
        self.enable_caching = enable_caching

        self.preprocessor = (
            DataProcessingOptimizer(
                enable_memory_optimization=enable_preprocessing,
                downcast_price_columns=downcast_price_columns,
            )
            if enable_preprocessing
            else None
        )

        self.cache = (
            CachingOptimizer(enable_caching=enable_caching)
            if enable_caching
            else None
        )

    # -------------------------
    # Data Access (Safe)
    # -------------------------

    def load_data(self, file_path: str, filters: Optional[Dict] = None) -> pd.DataFrame:
        """
        Load data from parquet, optionally using caching layer.

        Args:
            file_path: parquet path
            filters: optional filters (passed to caching layer)

        Returns:
            DataFrame
        """
        if self.cache is None:
            return pd.read_parquet(file_path)
        return self.cache.load_data_with_cache(file_path, filters)

    def smart_year_filtering(self, file_paths: List[str], target_years: List[int]) -> List[str]:
        """
        Filter files based on year metadata via caching layer.

        Returns:
            subset of file_paths
        """
        if self.cache is None:
            return file_paths
        return [str(p) for p in self.cache.smart_year_filtering(file_paths, target_years)]

    # -------------------------
    # Preprocessing (Safe)
    # -------------------------

    def preprocess(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Apply safe preprocessing only:
        - dtype optimization (guarded)
        - timestamp-derived time fields
        - sorting by timestamp (for fast slicing)

        No price-derived indicators are created here.
        """
        if self.preprocessor is None:
            return df

        df2 = self.preprocessor.optimize_dataframe_memory(df)
        df2 = self.preprocessor.precompute_time_fields(df2)
        return df2

    # -------------------------
    # Canonical Run
    # -------------------------

    def run(self, df: pd.DataFrame, rp: RunParams, debug: bool = False, **kwargs) -> pd.DataFrame:
        """
        Run the canonical Analyzer. Always.

        Args:
            df: market data
            rp: run params
            debug: debug flag
            kwargs: forwarded to run_strategy

        Returns:
            Analyzer results DataFrame
        """
        if self.enable_preprocessing:
            df = self.preprocess(df)

        # Single source of truth:
        return run_strategy(df, rp, debug=debug, **kwargs)

    # -------------------------
    # Benchmarking (Timing Only)
    # -------------------------

    def benchmark(self, df: pd.DataFrame, rp: RunParams, debug: bool = False, repeats: int = 3, **kwargs) -> Dict[str, Any]:
        """
        Benchmark timing of the canonical Analyzer with and without preprocessing.

        Notes:
        - This does NOT validate correctness vs an alternative engine.
        - If you want correctness checks, do row-level equality tests in unit tests.

        Returns:
            timings and counts
        """
        repeats = max(1, int(repeats))

        # Baseline: no preprocessing
        t_base = []
        base_counts = []
        for _ in range(repeats):
            t0 = time.perf_counter()
            out = run_strategy(df, rp, debug=debug, **kwargs)
            t_base.append(time.perf_counter() - t0)
            base_counts.append(len(out))

        # Preprocessed: preprocessing + canonical run
        t_pre = []
        pre_counts = []
        for _ in range(repeats):
            t0 = time.perf_counter()
            out = self.run(df, rp, debug=debug, **kwargs)
            t_pre.append(time.perf_counter() - t0)
            pre_counts.append(len(out))

        base_mean = float(sum(t_base) / len(t_base))
        pre_mean = float(sum(t_pre) / len(t_pre))
        speedup = (base_mean / pre_mean) if pre_mean > 0 else 0.0

        return {
            "repeats": repeats,
            "baseline_mean_sec": base_mean,
            "preprocessed_mean_sec": pre_mean,
            "speedup_factor": speedup,
            "baseline_trade_count_mean": float(sum(base_counts) / len(base_counts)),
            "preprocessed_trade_count_mean": float(sum(pre_counts) / len(pre_counts)),
        }


def example_usage():
    df = pd.read_parquet("data_processed/NQ_2006-2025.parquet").head(10000)

    rp = RunParams(instrument="NQ")
    rp.enabled_sessions = ["S1", "S2"]
    rp.enabled_slots = {"S1": [], "S2": []}
    rp.trade_days = [0, 1, 2, 3, 4]
    rp.same_bar_priority = "STOP_FIRST"
    rp.write_setup_rows = False
    rp.write_no_trade_rows = True

    runner = ResearchRunner(enable_preprocessing=True, enable_caching=False)

    bench = runner.benchmark(df, rp, debug=False, repeats=3)
    print("=== Benchmark ===")
    for k, v in bench.items():
        print(f"{k}: {v}")

    return runner, bench


if __name__ == "__main__":
    example_usage()
    print("Research Runner example completed successfully!")
