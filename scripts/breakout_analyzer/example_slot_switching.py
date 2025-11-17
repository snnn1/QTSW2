#!/usr/bin/env python3
"""
Example script showing how to use the time slot switching system with the analyzer
"""

import pandas as pd
import sys
import os

# Add the parent directory to the path so we can import breakout_core
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from logic.config_logic import RunParams
from breakout_core.engine import run_strategy


def create_sample_historical_results():
    """Create sample historical results for testing slot switching."""
    return pd.DataFrame([
        {"Date": "2025-01-02", "Time": "08:00", "Session": "S1", "Result": "Win", "Target": 10, "Peak": 12.5, "Direction": "Long", "Range": 15.0, "Stream": "ES", "Instrument": "ES", "Profit": 10.0},
        {"Date": "2025-01-02", "Time": "09:00", "Session": "S1", "Result": "Loss", "Target": 10, "Peak": 8.5, "Direction": "Short", "Range": 12.0, "Stream": "ES", "Instrument": "ES", "Profit": -20.0},
        {"Date": "2025-01-03", "Time": "08:00", "Session": "S1", "Result": "Loss", "Target": 10, "Peak": 6.5, "Direction": "Long", "Range": 18.0, "Stream": "ES", "Instrument": "ES", "Profit": -20.0},
        {"Date": "2025-01-03", "Time": "09:00", "Session": "S1", "Result": "Win", "Target": 10, "Peak": 15.0, "Direction": "Short", "Range": 14.0, "Stream": "ES", "Instrument": "ES", "Profit": 10.0},
        {"Date": "2025-01-04", "Time": "08:00", "Session": "S1", "Result": "BE", "Target": 10, "Peak": 8.0, "Direction": "Long", "Range": 16.0, "Stream": "ES", "Instrument": "ES", "Profit": 0.0},
        {"Date": "2025-01-04", "Time": "09:00", "Session": "S1", "Result": "Win", "Target": 10, "Peak": 12.0, "Direction": "Short", "Range": 13.0, "Stream": "ES", "Instrument": "ES", "Profit": 10.0},
    ])


def run_analyzer_example():
    """Run the analyzer with fixed slots (no switching)."""
    print("=== ANALYZER EXAMPLE ===")
    
    # Load market data (you would use your actual data here)
    try:
        df = pd.read_parquet("../../data_processed/merged_2025.parquet")
        print(f"Loaded market data: {len(df)} rows")
        print(f"Date range: {df['timestamp'].min()} to {df['timestamp'].max()}")
    except FileNotFoundError:
        print("Market data file not found. Please ensure data_processed/merged_2025.parquet exists.")
        return
    
    # Set up run parameters
    rp = RunParams(
        instrument="ES",
        enabled_sessions=["S1", "S2"],
        enabled_slots={"S1": ["08:00"], "S2": ["09:00"]},  # Fixed slots
        trade_days=[0, 1, 2, 3, 4],  # Mon-Fri
        same_bar_priority="STOP_FIRST",
        write_setup_rows=False,
        write_no_trade_rows=True
    )
    
    print(f"\nRun parameters: {rp}")
    
    # Run strategy with fixed slots (no switching)
    print("\n=== RUNNING WITH FIXED SLOTS ===")
    results = run_strategy(df, rp, debug=True)
    print(f"Results: {len(results)} trades")
    
    # Show results summary
    print("\n=== RESULTS SUMMARY ===")
    if len(results) > 0:
        wins = len(results[results['Result'] == 'Win'])
        losses = len(results[results['Result'] == 'Loss'])
        be_trades = len(results[results['Result'] == 'BE'])
        total_profit = results['Profit'].sum()
        
        print(f"Total trades: {len(results)}")
        print(f"Wins: {wins}")
        print(f"Losses: {losses}")
        print(f"Break-even: {be_trades}")
        print(f"Total profit: ${total_profit:.2f}")
        
        if wins + losses + be_trades > 0:
            win_rate = (wins / (wins + losses + be_trades)) * 100
            print(f"Win rate: {win_rate:.1f}%")
    else:
        print("No trades generated.")


if __name__ == "__main__":
    run_analyzer_example()



