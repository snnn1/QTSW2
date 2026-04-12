"""
Analyzer Benchmark Module

This module provides benchmarking and profiling utilities for the
canonical breakout Analyzer. It does NOT implement or replace any
trading logic.

Purpose:
- Measure Analyzer wall-clock performance
- Compare runs across datasets or configurations
- Profile result aggregation cost

Hard rules:
- All trading logic must come from run_strategy
- No alternative execution paths
- No vectorized decision logic
"""

import time
from dataclasses import dataclass
from typing import Dict, Any, Tuple

import pandas as pd

# Canonical Analyzer import
from breakout_core.engine import run_strategy
from logic.config_logic import RunParams


# =========================
# Stats Container
# =========================

@dataclass
class BenchmarkStats:
    analyzer_time: float = 0.0
    aggregation_time: float = 0.0
    total_time: float = 0.0
    rows_processed: int = 0


# =========================
# Benchmark Runner
# =========================

class AnalyzerBenchmark:
    """
    Read-only benchmarking harness for the canonical Analyzer.
    """

    def __init__(self):
        self.stats = BenchmarkStats()

    def run(
        self,
        df: pd.DataFrame,
        rp: RunParams,
        debug: bool = False
    ) -> Tuple[pd.DataFrame, Dict[str, Any]]:
        """
        Run the Analyzer and record timing statistics.

        Args:
            df: Market data DataFrame (already validated / translated)
            rp: Run parameters
            debug: Analyzer debug flag

        Returns:
            (results_df, benchmark_summary)
        """
        start_total = time.perf_counter()

        # --- Analyzer execution ---
        start_analyzer = time.perf_counter()
        results_df = run_strategy(df, rp, debug=debug)
        analyzer_elapsed = time.perf_counter() - start_analyzer

        self.stats.analyzer_time += analyzer_elapsed
        self.stats.rows_processed += len(df)

        # --- Result aggregation timing (optional) ---
        start_agg = time.perf_counter()
        summary = self._aggregate_results(results_df)
        agg_elapsed = time.perf_counter() - start_agg

        self.stats.aggregation_time += agg_elapsed

        # --- Total ---
        self.stats.total_time += time.perf_counter() - start_total

        benchmark_summary = {
            "analyzer_time_sec": analyzer_elapsed,
            "aggregation_time_sec": agg_elapsed,
            "total_time_sec": analyzer_elapsed + agg_elapsed,
            "rows_processed": len(df),
            "trades_generated": len(results_df),
            "summary": summary,
        }

        return results_df, benchmark_summary

    def reset(self):
        """Reset accumulated benchmark statistics."""
        self.stats = BenchmarkStats()

    # =========================
    # Safe Post-Analyzer Helpers
    # =========================

    @staticmethod
    def _aggregate_results(results_df: pd.DataFrame) -> Dict[str, Any]:
        """
        Lightweight aggregation of Analyzer output.
        This operates ONLY on final results.
        """
        if results_df.empty:
            return {
                "trades": 0,
                "profit": 0.0,
                "wins": 0,
                "losses": 0,
                "break_even": 0,
                "no_trades": 0,
                "win_rate": 0.0,
            }

        trades_only = results_df[results_df["Result"] != "NoTrade"]

        wins = (results_df["Result"] == "Win").sum()
        losses = (results_df["Result"] == "Loss").sum()
        be = (results_df["Result"] == "BE").sum()
        nt = (results_df["Result"] == "NoTrade").sum()

        win_rate = (
            wins / len(trades_only)
            if len(trades_only) > 0
            else 0.0
        )

        return {
            "trades": len(results_df),
            "profit": float(results_df["Profit"].sum()),
            "wins": int(wins),
            "losses": int(losses),
            "break_even": int(be),
            "no_trades": int(nt),
            "win_rate": round(win_rate, 4),
        }


# =========================
# Example Usage (Research Only)
# =========================

def example_usage():
    """
    Example benchmark run.
    This is for research / profiling only.
    """

    import pandas as pd

    # Load sample data
    df = pd.read_parquet("data_processed/NQ_2006-2025.parquet")
    df_sample = df.head(50_000)

    # Run parameters
    rp = RunParams(instrument="NQ")
    rp.enabled_sessions = ["S1", "S2"]
    rp.enabled_slots = {"S1": [], "S2": []}
    rp.trade_days = [0, 1, 2, 3, 4]
    rp.same_bar_priority = "STOP_FIRST"
    rp.write_setup_rows = False
    rp.write_no_trade_rows = True

    benchmark = AnalyzerBenchmark()

    results, stats = benchmark.run(df_sample, rp, debug=False)

    print("=== Analyzer Benchmark ===")
    for k, v in stats.items():
        print(f"{k}: {v}")

    return results, stats


if __name__ == "__main__":
    example_usage()
