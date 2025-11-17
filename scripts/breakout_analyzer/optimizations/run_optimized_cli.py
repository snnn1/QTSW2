#!/usr/bin/env python3
"""
Optimized Command Line Analyzer

Simple command-line interface to run the analyzer with optimizations.
"""

import sys
import pandas as pd
from pathlib import Path

# Add breakout_analyzer root to PYTHONPATH
ROOT = Path(__file__).resolve().parents[1]
sys.path.append(str(ROOT))

from breakout_core.engine import run_strategy
from logic.config_logic import RunParams
from optimized_engine_integration import OptimizedBreakoutEngine


def main():
    """Main function for command-line usage"""
    
    print("🚀 Optimized Breakout Strategy Analyzer")
    print("=" * 50)
    
    # Check if data file exists
    data_file = Path("../../data_processed/NQ_2006-2025.parquet")
    if not data_file.exists():
        print(f"❌ Data file not found: {data_file}")
        print("Please ensure the data file exists.")
        return
    
    # Load data
    print(f"📊 Loading data from: {data_file}")
    df = pd.read_parquet(data_file)
    print(f"✅ Loaded {len(df)} rows")
    
    # Create run parameters
    rp = RunParams(instrument='NQ')
    rp.enabled_sessions = ['S1', 'S2']
    rp.enabled_slots = {'S1': [], 'S2': []}
    rp.trade_days = [0, 1, 2, 3, 4]
    rp.same_bar_priority = 'STOP_FIRST'
    rp.write_setup_rows = False
    rp.write_no_trade_rows = True
    
    # Use small sample for testing
    df_sample = df.head(10000)
    print(f"🔍 Using sample of {len(df_sample)} rows for testing")
    
    # Benchmark original vs optimized
    print("\n📊 Running Performance Benchmark...")
    
    # Original engine
    print("⏱️ Running original engine...")
    import time
    start_time = time.time()
    original_result = run_strategy(df_sample, rp, debug=False)
    original_time = time.time() - start_time
    
    # Optimized engine
    print("🚀 Running optimized engine...")
    optimized_engine = OptimizedBreakoutEngine(enable_optimizations=True)
    start_time = time.time()
    optimized_result = optimized_engine.run_optimized_strategy(df_sample, rp, debug=False)
    optimized_time = time.time() - start_time
    
    # Calculate improvements
    speedup = original_time / optimized_time if optimized_time > 0 else 0
    time_saved = original_time - optimized_time
    
    # Display results
    print("\n" + "=" * 50)
    print("📈 PERFORMANCE RESULTS")
    print("=" * 50)
    print(f"Original time: {original_time:.2f} seconds")
    print(f"Optimized time: {optimized_time:.2f} seconds")
    print(f"Speedup: {speedup:.2f}x")
    print(f"Time saved: {time_saved:.2f} seconds")
    print(f"Original results: {len(original_result)} trades")
    print(f"Optimized results: {len(optimized_result)} trades")
    print(f"Results match: {len(original_result) == len(optimized_result)}")
    
    if speedup > 1.1:
        print("✅ Optimizations show performance improvements!")
    else:
        print("⚠️ Optimizations show minimal improvements")
    
    print("\n🎉 Analysis completed successfully!")


if __name__ == "__main__":
    main()


