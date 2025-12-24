"""
Data Processing Utilities (Quant-Safe)

This module provides data-layer performance utilities that can be used
BEFORE the canonical analyzer to improve throughput without changing
any trading decisions.

Hard rules:
- No trade logic (no range highs/lows, no entry detection, no MFE, no profit calc)
- No alternative "optimized strategy" paths
- Only safe transformations: dtype/memory optimization, timestamp-derived fields,
  and deterministic slicing helpers.

Primary utilities:
1) Memory Optimization: dtype downcasting with safety constraints
2) Time Precomputation: timestamp-derived fields only (date/hour/minute/etc.)
3) Fast Time Slicing: binary-search slicing using searchsorted()
4) Multi-Window Slicing: vectorized searchsorted for many windows
"""

from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple

import numpy as np
import pandas as pd


# =========================
# Stats
# =========================

@dataclass(frozen=True)
class OptimizationStats:
    original_time: float
    optimized_time: float
    memory_saved_mb: float
    speedup_factor: float


# =========================
# Optimizer
# =========================

class DataProcessingOptimizer:
    """
    Quant-safe data processing utilities.

    Notes:
    - The optimizer never modifies trading semantics.
    - Any column creation here must be a pure function of existing data.
    """

    def __init__(
        self,
        enable_memory_optimization: bool = True,
        downcast_price_columns: bool = False,
    ):
        """
        Args:
            enable_memory_optimization: downcast numeric/object columns where safe.
            downcast_price_columns: if True, allow float32 downcast of OHLC.
                Default False because float32 can introduce subtle rounding differences.
        """
        self.enable_memory_optimization = enable_memory_optimization
        self.downcast_price_columns = downcast_price_columns
        self.stats: Dict[str, OptimizationStats] = {}

    # =========================
    # Memory Optimization
    # =========================

    def optimize_dataframe_memory(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Optimize DataFrame memory usage via dtype downcasting.

        Quant safety constraints:
        - Never changes values intentionally.
        - By default, does NOT downcast OHLC columns to float32.
        - Converts object columns to category only when it is safe to do so.

        Returns:
            Memory-optimized copy of the DataFrame.
        """
        df_opt = df.copy()

        if not self.enable_memory_optimization or df_opt.empty:
            return df_opt

        # Identify price-like columns; keep float64 unless explicitly allowed.
        price_cols = {"open", "high", "low", "close"}

        # Numeric downcasting
        for col in df_opt.select_dtypes(include=[np.number]).columns:
            series = df_opt[col]

            # Integers: safe downcast based on bounds
            if pd.api.types.is_integer_dtype(series):
                smin, smax = series.min(), series.max()
                if smin >= np.iinfo(np.int8).min and smax <= np.iinfo(np.int8).max:
                    df_opt[col] = series.astype(np.int8)
                elif smin >= np.iinfo(np.int16).min and smax <= np.iinfo(np.int16).max:
                    df_opt[col] = series.astype(np.int16)
                elif smin >= np.iinfo(np.int32).min and smax <= np.iinfo(np.int32).max:
                    df_opt[col] = series.astype(np.int32)
                # else keep as-is (int64)

            # Floats: only downcast if allowed and safe
            elif pd.api.types.is_float_dtype(series):
                if (col in price_cols) and (not self.downcast_price_columns):
                    continue  # keep float64 for OHLC by default

                # Downcast to float32 if within representable range
                smin, smax = float(series.min()), float(series.max())
                if smin >= np.finfo(np.float32).min and smax <= np.finfo(np.float32).max:
                    df_opt[col] = series.astype(np.float32)

        # Object -> category (safe for repeated string values)
        for col in df_opt.select_dtypes(include=["object"]).columns:
            try:
                # Category is safe for equality comparisons and grouping
                df_opt[col] = df_opt[col].astype("category")
            except Exception:
                pass  # leave as object

        return df_opt

    # =========================
    # Time Precomputation
    # =========================

    def precompute_time_fields(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Precompute timestamp-derived fields for faster filtering/grouping.

        Quant safety constraints:
        - Only derives from timestamp.
        - Does not compute any price-derived indicators (no volatility, no ranges).

        Required:
        - df must have a 'timestamp' column of datetime64[ns, tz] or datetime64[ns].

        Returns:
            Copy of df with additional time fields.
        """
        if df.empty:
            return df.copy()

        out = df.copy()

        if "timestamp" not in out.columns:
            raise ValueError("precompute_time_fields requires a 'timestamp' column")

        ts = out["timestamp"]
        if not pd.api.types.is_datetime64_any_dtype(ts):
            raise ValueError("'timestamp' must be datetime-like")

        out["date"] = ts.dt.date
        out["weekday"] = ts.dt.weekday
        out["hour"] = ts.dt.hour
        out["minute"] = ts.dt.minute
        out["year"] = ts.dt.year
        out["month"] = ts.dt.month
        out["day"] = ts.dt.day

        # Ensure sorted for downstream searchsorted slicing
        if not ts.is_monotonic_increasing:
            out = out.sort_values("timestamp").reset_index(drop=True)

        return out

    # =========================
    # Fast Slicing
    # =========================

    def ensure_sorted_by_timestamp(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Return a copy sorted by timestamp if needed.
        """
        if df.empty:
            return df.copy()
        if "timestamp" not in df.columns:
            raise ValueError("ensure_sorted_by_timestamp requires a 'timestamp' column")
        if df["timestamp"].is_monotonic_increasing:
            return df.copy()
        return df.sort_values("timestamp").reset_index(drop=True)

    def slice_time_window(
        self,
        df: pd.DataFrame,
        start_time: pd.Timestamp,
        end_time: pd.Timestamp,
    ) -> pd.DataFrame:
        """
        Deterministic O(log n) slice using searchsorted.

        Assumptions:
        - df is sorted by 'timestamp' (this function will sort if needed)

        Returns:
            Copy of the sliced window.
        """
        if df.empty:
            return df.copy()

        df_sorted = self.ensure_sorted_by_timestamp(df)

        ts = df_sorted["timestamp"]
        start_idx = ts.searchsorted(start_time, side="left")
        end_idx = ts.searchsorted(end_time, side="right")

        if start_idx >= end_idx:
            return df_sorted.iloc[0:0].copy()

        return df_sorted.iloc[start_idx:end_idx].copy()

    def slice_many_windows(
        self,
        df: pd.DataFrame,
        windows: List[Dict[str, pd.Timestamp]],
    ) -> Dict[int, pd.DataFrame]:
        """
        Slice many [start_ts, end_ts] windows using vectorized searchsorted.

        windows: list of dicts with keys: 'start_ts', 'end_ts'

        Returns:
            Dict mapping window index -> sliced DataFrame
        """
        if df.empty or not windows:
            return {}

        df_sorted = self.ensure_sorted_by_timestamp(df)
        ts = df_sorted["timestamp"]

        starts = [w["start_ts"] for w in windows]
        ends = [w["end_ts"] for w in windows]

        start_idx = ts.searchsorted(starts, side="left")
        end_idx = ts.searchsorted(ends, side="right")

        out: Dict[int, pd.DataFrame] = {}
        for i, (a, b) in enumerate(zip(start_idx, end_idx)):
            if a < b:
                out[i] = df_sorted.iloc[a:b].copy()
            else:
                out[i] = df_sorted.iloc[0:0].copy()

        return out

    def batch_slice_windows(
        self,
        df: pd.DataFrame,
        windows: List[Dict[str, pd.Timestamp]],
        batch_size: int = 200,
    ) -> List[pd.DataFrame]:
        """
        Batch slicing wrapper for memory discipline. This does not compute
        any decisions; it only returns sliced DataFrames.

        Returns:
            List of DataFrames in the same order as windows.
        """
        if not windows:
            return []

        results: List[pd.DataFrame] = []
        for i in range(0, len(windows), batch_size):
            batch = windows[i : i + batch_size]
            sliced = self.slice_many_windows(df, batch)
            for j in range(len(batch)):
                results.append(sliced[j])
        return results

    # =========================
    # Benchmarking (Safe)
    # =========================

    def benchmark_function(self, name: str, func, *args, repeats: int = 3, **kwargs) -> Dict[str, float]:
        """
        Benchmark a single function (same function) to establish baseline timing.
        This avoids the 'original vs optimized logic' trap.

        Returns:
            Dict with min/mean/max elapsed seconds.
        """
        timings = []
        for _ in range(max(1, repeats)):
            t0 = time.perf_counter()
            func(*args, **kwargs)
            timings.append(time.perf_counter() - t0)

        return {
            "name": name,
            "repeats": repeats,
            "min_sec": float(np.min(timings)),
            "mean_sec": float(np.mean(timings)),
            "max_sec": float(np.max(timings)),
        }


# =========================
# Example Usage (Research Only)
# =========================

def example_usage():
    optimizer = DataProcessingOptimizer(enable_memory_optimization=True, downcast_price_columns=False)

    dates = pd.date_range("2024-01-01", periods=1000, freq="1min")
    df = pd.DataFrame(
        {
            "timestamp": dates,
            "open": np.random.randn(1000) * 100 + 2000,
            "high": np.random.randn(1000) * 100 + 2000,
            "low": np.random.randn(1000) * 100 + 2000,
            "close": np.random.randn(1000) * 100 + 2000,
            "volume": np.random.randint(1000, 10000, 1000),
            "instrument": ["ES"] * 1000,
        }
    )

    mem_before = df.memory_usage(deep=True).sum() / 1024 / 1024
    df_opt = optimizer.optimize_dataframe_memory(df)
    mem_after = df_opt.memory_usage(deep=True).sum() / 1024 / 1024

    df_time = optimizer.precompute_time_fields(df_opt)

    start = pd.Timestamp("2024-01-01 10:00:00")
    end = pd.Timestamp("2024-01-01 12:00:00")
    sliced = optimizer.slice_time_window(df_time, start, end)

    print(f"Memory before: {mem_before:.2f} MB")
    print(f"Memory after:  {mem_after:.2f} MB")
    print(f"Sliced shape:  {sliced.shape}")

    bench = optimizer.benchmark_function("slice_time_window", optimizer.slice_time_window, df_time, start, end, repeats=10)
    print("Benchmark:", bench)

    return optimizer


if __name__ == "__main__":
    example_usage()
