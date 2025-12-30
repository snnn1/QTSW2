#!/usr/bin/env python3
"""
Test script to verify analyzer works correctly for pipeline integration
"""

import sys
import os
from pathlib import Path
import pandas as pd
import pytz
import numpy as np

# Add modules to path
sys.path.insert(0, str(Path(__file__).parent / "modules" / "analyzer"))

from breakout_core.engine import run_strategy
from logic.config_logic import RunParams

def create_test_data():
    """Create minimal test data"""
    chicago_tz = pytz.timezone("America/Chicago")
    # Create data for a few days
    dates = pd.date_range('2025-01-01 02:00', '2025-01-03 16:00', freq='1min', tz=chicago_tz)
    
    # Create realistic price data
    np.random.seed(42)
    base_price = 4000.0
    prices = []
    for i in range(len(dates)):
        change = np.random.normal(0, 0.5)
        base_price += change
        prices.append(base_price)
    
    df = pd.DataFrame({
        'timestamp': dates,
        'open': prices,
        'high': [p + abs(np.random.normal(0, 0.25)) for p in prices],
        'low': [p - abs(np.random.normal(0, 0.25)) for p in prices],
        'close': [p + np.random.normal(0, 0.1) for p in prices],
        'instrument': 'ES'
    })
    
    # Ensure OHLC relationships are valid
    df['high'] = df[['open', 'high', 'close']].max(axis=1)
    df['low'] = df[['open', 'low', 'close']].min(axis=1)
    
    return df

def test_analyzer():
    """Test analyzer execution"""
    print("Creating test data...")
    df = create_test_data()
    print(f"Created {len(df)} rows of test data")
    print(f"Date range: {df['timestamp'].min()} to {df['timestamp'].max()}")
    
    print("\nRunning analyzer...")
    rp = RunParams(
        instrument="ES",
        enabled_sessions=["S1", "S2"],
        enabled_slots={"S1": ["07:30", "08:00"], "S2": ["09:30", "10:00"]},
        trade_days=[0, 1, 2, 3, 4]
    )
    
    try:
        results = run_strategy(df, rp, debug=False)
        print(f"\n[SUCCESS] Analyzer completed successfully!")
        print(f"   Generated {len(results)} result rows")
        
        if len(results) > 0:
            print(f"\nSample results:")
            print(results.head(10).to_string())
            print(f"\nResult summary:")
            print(f"   Total trades: {len(results)}")
            if 'Result' in results.columns:
                result_counts = results['Result'].value_counts()
                print(f"   Results breakdown:")
                for result_type, count in result_counts.items():
                    print(f"     {result_type}: {count}")
            if 'Profit' in results.columns:
                total_profit = results['Profit'].sum()
                print(f"   Total profit: {total_profit:.2f}")
        else:
            print("   [WARNING] No results generated (this may be normal for test data)")
        
        return True
    except Exception as e:
        print(f"\n[ERROR] Analyzer failed with error:")
        import traceback
        traceback.print_exc()
        return False

if __name__ == "__main__":
    print("=" * 60)
    print("Analyzer Pipeline Integration Test")
    print("=" * 60)
    
    success = test_analyzer()
    
    print("\n" + "=" * 60)
    if success:
        print("[PASS] TEST PASSED - Analyzer is working correctly")
    else:
        print("[FAIL] TEST FAILED - Analyzer has issues")
    print("=" * 60)
    
    sys.exit(0 if success else 1)
