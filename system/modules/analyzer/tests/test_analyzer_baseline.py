"""
Baseline Test for Analyzer Refactoring
Captures current analyzer results to ensure refactoring doesn't change outputs
"""

import pandas as pd
import pytest
from pathlib import Path
import hashlib
import json
from datetime import datetime

# Import analyzer components
import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from breakout_core.engine import run_strategy
from logic.config_logic import RunParams


def hash_dataframe(df: pd.DataFrame) -> str:
    """Create a hash of DataFrame contents for comparison"""
    # Sort by Date and Time for consistent hashing
    if not df.empty and 'Date' in df.columns and 'Time' in df.columns:
        df_sorted = df.sort_values(['Date', 'Time']).reset_index(drop=True)
    else:
        df_sorted = df.copy()
    
    # Convert to string representation (excluding index)
    df_str = df_sorted.to_string(index=False)
    return hashlib.md5(df_str.encode()).hexdigest()


def capture_baseline_results(data_file: Path, instrument: str, output_file: Path):
    """
    Capture baseline results from current analyzer
    
    Args:
        data_file: Path to input data file
        instrument: Instrument to analyze
        output_file: Path to save baseline results
    """
    # Load data
    df = pd.read_parquet(data_file)
    
    # Create run parameters
    rp = RunParams(
        instrument=instrument,
        enabled_sessions=["S1", "S2"],
        enabled_slots={"S1": [], "S2": []},  # All slots
        trade_days=[0, 1, 2, 3, 4],  # Mon-Fri
        same_bar_priority="STOP_FIRST",
        write_setup_rows=False,
        write_no_trade_rows=True
    )
    
    # Run strategy
    results = run_strategy(df, rp, debug=False)
    
    # Save baseline
    baseline = {
        "timestamp": datetime.now().isoformat(),
        "data_file": str(data_file),
        "instrument": instrument,
        "total_rows": len(results),
        "hash": hash_dataframe(results),
        "columns": list(results.columns),
        "sample_data": results.head(10).to_dict('records') if not results.empty else [],
        "summary": {
            "wins": len(results[results['Result'] == 'Win']) if not results.empty else 0,
            "losses": len(results[results['Result'] == 'Loss']) if not results.empty else 0,
            "be": len(results[results['Result'] == 'BE']) if not results.empty else 0,
            "total_profit": float(results['Profit'].sum()) if not results.empty else 0.0,
        }
    }
    
    # Save to file
    output_file.parent.mkdir(parents=True, exist_ok=True)
    with open(output_file, 'w') as f:
        json.dump(baseline, f, indent=2, default=str)
    
    # Also save full results as parquet for detailed comparison
    results_file = output_file.with_suffix('.parquet')
    results.to_parquet(results_file, index=False)
    
    return baseline


def compare_results(current_results: pd.DataFrame, baseline_file: Path) -> dict:
    """
    Compare current results with baseline
    
    Returns:
        Dictionary with comparison results
    """
    # Load baseline
    with open(baseline_file, 'r') as f:
        baseline = json.load(f)
    
    baseline_results = pd.read_parquet(baseline_file.with_suffix('.parquet'))
    
    # Compare
    comparison = {
        "hash_match": hash_dataframe(current_results) == baseline["hash"],
        "row_count_match": len(current_results) == baseline["total_rows"],
        "columns_match": list(current_results.columns) == baseline["columns"],
        "summary_match": {
            "wins": len(current_results[current_results['Result'] == 'Win']) == baseline["summary"]["wins"],
            "losses": len(current_results[current_results['Result'] == 'Loss']) == baseline["summary"]["losses"],
            "be": len(current_results[current_results['Result'] == 'BE']) == baseline["summary"]["be"],
            "total_profit": abs(float(current_results['Profit'].sum()) - baseline["summary"]["total_profit"]) < 0.01,
        }
    }
    
    # Check if all match
    comparison["all_match"] = (
        comparison["hash_match"] and
        comparison["row_count_match"] and
        comparison["columns_match"] and
        all(comparison["summary_match"].values())
    )
    
    return comparison


if __name__ == "__main__":
    # Example usage
    import argparse
    
    parser = argparse.ArgumentParser(description='Capture or compare analyzer baseline')
    parser.add_argument('--capture', action='store_true', help='Capture baseline')
    parser.add_argument('--compare', action='store_true', help='Compare with baseline')
    parser.add_argument('--data', type=str, required=True, help='Input data file')
    parser.add_argument('--instrument', type=str, required=True, help='Instrument')
    parser.add_argument('--baseline', type=str, default='baseline_results.json', help='Baseline file')
    
    args = parser.parse_args()
    
    data_file = Path(args.data)
    baseline_file = Path(args.baseline)
    
    if args.capture:
        print(f"Capturing baseline for {args.instrument}...")
        baseline = capture_baseline_results(data_file, args.instrument, baseline_file)
        print(f"Baseline captured: {baseline['total_rows']} rows, hash: {baseline['hash'][:16]}...")
        print(f"Saved to: {baseline_file}")
    
    if args.compare:
        print(f"Comparing results with baseline...")
        # This would need to run the analyzer first
        print("Not implemented - run analyzer separately and compare")

