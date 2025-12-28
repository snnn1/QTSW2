#!/usr/bin/env python3
"""
Command Line Analyzer

Simple command-line interface to run the analyzer.
All analyzer semantics live in run_strategy() in breakout_core/engine.py
"""

import sys
import pandas as pd
from pathlib import Path

# Add breakout_analyzer root to PYTHONPATH
ROOT = Path(__file__).resolve().parents[1]
sys.path.append(str(ROOT))

from breakout_core.engine import run_strategy
from logic.config_logic import RunParams


def main():
    """Main function for command-line usage"""
    
    print("Breakout Strategy Analyzer")
    print("=" * 50)
    
    # Check if data file exists
    data_file = Path("../../data_processed/NQ_2006-2025.parquet")
    if not data_file.exists():
        print(f"Data file not found: {data_file}")
        print("Please ensure the data file exists.")
        return
    
    # Load data
    print(f"Loading data from: {data_file}")
    df = pd.read_parquet(data_file)
    print(f"Loaded {len(df)} rows")
    
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
    print(f"Using sample of {len(df_sample)} rows for testing")
    
    # Run strategy - all analyzer semantics live in run_strategy()
    print("\nRunning strategy...")
    import time
    start_time = time.time()
    result = run_strategy(df_sample, rp, debug=False)
    elapsed_time = time.time() - start_time
    
    # Display results
    print("\n" + "=" * 50)
    print("RESULTS")
    print("=" * 50)
    print(f"Execution time: {elapsed_time:.2f} seconds")
    print(f"Results: {len(result)} trades")
    print("\nAnalysis completed successfully!")


if __name__ == "__main__":
    main()
