#!/usr/bin/env python3
"""
Parallel vs Sequential Determinism Parity Tests

CRITICAL: Parallel sequencer logic and sequential/state-capturing logic MUST produce
identical outputs. This is mandatory to prevent rolling-resequence vs full-rebuild drift.

Tests ensure:
- apply_sequencer_logic() (parallel) and apply_sequencer_logic_with_state() (sequential)
  produce identical outputs
- Test across multiple streams, date ranges, and filter configurations
- Assert exact DataFrame equality (row order, values, dtypes)
"""

import sys
import pandas as pd
import numpy as np
from pathlib import Path
from typing import Dict, List

# Add project root to path
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.matrix import sequencer_logic
from modules.matrix.tests.fixtures.analyzer_output_fixture import create_test_analyzer_output


def assert_dataframe_equality(df1: pd.DataFrame, df2: pd.DataFrame, context: str = "") -> None:
    """
    Assert that two DataFrames are exactly equal (row order, values, dtypes).
    
    Args:
        df1: First DataFrame
        df2: Second DataFrame
        context: Context string for error messages
        
    Raises:
        AssertionError: If DataFrames are not equal
    """
    # Check shape
    if len(df1) != len(df2):
        raise AssertionError(
            f"{context}: Row count mismatch - parallel: {len(df1)}, sequential: {len(df2)}"
        )
    
    if len(df1.columns) != len(df2.columns):
        raise AssertionError(
            f"{context}: Column count mismatch - parallel: {len(df1.columns)}, sequential: {len(df2.columns)}"
        )
    
    # Check columns
    missing_cols_1 = set(df2.columns) - set(df1.columns)
    missing_cols_2 = set(df1.columns) - set(df2.columns)
    if missing_cols_1 or missing_cols_2:
        raise AssertionError(
            f"{context}: Column mismatch - missing in parallel: {missing_cols_1}, missing in sequential: {missing_cols_2}"
        )
    
    # Sort by same columns for comparison
    sort_cols = ['Stream', 'trade_date', 'Time'] if all(c in df1.columns for c in ['Stream', 'trade_date', 'Time']) else ['Stream', 'Date', 'Time']
    if all(c in df1.columns for c in sort_cols):
        df1_sorted = df1.sort_values(sort_cols).reset_index(drop=True)
        df2_sorted = df2.sort_values(sort_cols).reset_index(drop=True)
    else:
        df1_sorted = df1.reset_index(drop=True)
        df2_sorted = df2.reset_index(drop=True)
    
    # Check each column
    for col in df1.columns:
        if col not in df2.columns:
            continue
        
        # Compare values (handle NaN properly)
        values_equal = True
        if df1_sorted[col].dtype != df2_sorted[col].dtype:
            # Try to convert to same dtype
            try:
                if pd.api.types.is_datetime64_any_dtype(df1_sorted[col]):
                    df2_sorted[col] = pd.to_datetime(df2_sorted[col])
                elif pd.api.types.is_numeric_dtype(df1_sorted[col]):
                    df2_sorted[col] = pd.to_numeric(df2_sorted[col], errors='coerce')
            except:
                pass
        
        # Compare with NaN handling
        try:
            if df1_sorted[col].dtype == 'object':
                # String comparison
                df1_str = df1_sorted[col].astype(str).fillna('')
                df2_str = df2_sorted[col].astype(str).fillna('')
                values_equal = (df1_str == df2_str).all()
            else:
                # Use pandas equals which handles NaN
                values_equal = df1_sorted[col].equals(df2_sorted[col])
        except Exception as e:
            # Fallback: element-wise comparison
            values_equal = False
            for idx in df1_sorted.index:
                val1 = df1_sorted.loc[idx, col]
                val2 = df2_sorted.loc[idx, col]
                if pd.isna(val1) and pd.isna(val2):
                    continue
                if val1 != val2:
                    values_equal = False
                    break
        
        if not values_equal:
            # Find first mismatch
            for idx in df1_sorted.index[:10]:  # Check first 10 rows
                val1 = df1_sorted.loc[idx, col]
                val2 = df2_sorted.loc[idx, col]
                if pd.isna(val1) and pd.isna(val2):
                    continue
                if val1 != val2:
                    raise AssertionError(
                        f"{context}: Column '{col}' mismatch at row {idx}: "
                        f"parallel={val1}, sequential={val2}"
                    )
    
    # If we get here, DataFrames are equal
    print(f"✓ {context}: DataFrames are identical ({len(df1)} rows, {len(df1.columns)} columns)")


def test_parallel_sequential_parity_basic():
    """Test basic parity with simple data."""
    print("\n" + "="*70)
    print("Test: Basic Parallel vs Sequential Parity")
    print("="*70)
    
    # Create test data
    test_data = create_test_analyzer_output(
        streams=['ES1', 'ES2'],
        dates=['2025-01-06', '2025-01-07', '2025-01-08'],
        times=['07:30', '08:00', '09:00']
    )
    
    stream_filters = {}
    
    # Run parallel sequencer
    result_parallel = sequencer_logic.apply_sequencer_logic(
        test_data.copy(),
        stream_filters,
        display_year=None,
        parallel=True
    )
    
    # Run sequential sequencer (with state capture)
    result_sequential, _ = sequencer_logic.apply_sequencer_logic_with_state(
        test_data.copy(),
        stream_filters,
        display_year=None,
        parallel=False
    )
    
    # Assert equality
    assert_dataframe_equality(result_parallel, result_sequential, "Basic parity test")
    print("✓ Basic parity test passed")


def test_parallel_sequential_parity_with_filters():
    """Test parity with stream filters applied."""
    print("\n" + "="*70)
    print("Test: Parallel vs Sequential Parity with Filters")
    print("="*70)
    
    # Create test data
    test_data = create_test_analyzer_output(
        streams=['ES1', 'GC1'],
        dates=['2025-01-06', '2025-01-07', '2025-01-08', '2025-01-09'],
        times=['07:30', '08:00', '09:00', '09:30']
    )
    
    # Apply filters
    stream_filters = {
        'ES1': {
            'exclude_times': ['07:30'],
            'exclude_days_of_week': ['Friday']
        },
        'GC1': {
            'exclude_times': ['09:30']
        }
    }
    
    # Run parallel sequencer
    result_parallel = sequencer_logic.apply_sequencer_logic(
        test_data.copy(),
        stream_filters,
        display_year=None,
        parallel=True
    )
    
    # Run sequential sequencer
    result_sequential, _ = sequencer_logic.apply_sequencer_logic_with_state(
        test_data.copy(),
        stream_filters,
        display_year=None,
        parallel=False
    )
    
    # Assert equality
    assert_dataframe_equality(result_parallel, result_sequential, "Filter parity test")
    print("✓ Filter parity test passed")


def test_parallel_sequential_parity_time_changes():
    """Test parity with time changes (loss-triggered)."""
    print("\n" + "="*70)
    print("Test: Parallel vs Sequential Parity with Time Changes")
    print("="*70)
    
    # Create test data with losses to trigger time changes
    test_data = create_test_analyzer_output(
        streams=['ES1'],
        dates=['2025-01-06', '2025-01-07', '2025-01-08', '2025-01-09', '2025-01-10'],
        times=['07:30', '08:00', '09:00']
    )
    
    # Set some results to Loss to trigger time changes
    # This tests that time change logic is deterministic
    test_data.loc[test_data['Date'] == '2025-01-07', 'Result'] = 'Loss'
    test_data.loc[test_data['Date'] == '2025-01-09', 'Result'] = 'Loss'
    
    stream_filters = {}
    
    # Run parallel sequencer
    result_parallel = sequencer_logic.apply_sequencer_logic(
        test_data.copy(),
        stream_filters,
        display_year=None,
        parallel=True
    )
    
    # Run sequential sequencer
    result_sequential, _ = sequencer_logic.apply_sequencer_logic_with_state(
        test_data.copy(),
        stream_filters,
        display_year=None,
        parallel=False
    )
    
    # Assert equality
    assert_dataframe_equality(result_parallel, result_sequential, "Time change parity test")
    print("✓ Time change parity test passed")


def test_parallel_sequential_parity_multiple_streams():
    """Test parity with multiple streams."""
    print("\n" + "="*70)
    print("Test: Parallel vs Sequential Parity with Multiple Streams")
    print("="*70)
    
    # Create test data with multiple streams
    test_data = create_test_analyzer_output(
        streams=['ES1', 'ES2', 'GC1', 'GC2', 'CL1', 'NQ1'],
        dates=['2025-01-06', '2025-01-07', '2025-01-08'],
        times=['07:30', '08:00', '09:00']
    )
    
    stream_filters = {
        'ES2': {'exclude_times': ['07:30']},
        'GC2': {'exclude_days_of_week': ['Friday']}
    }
    
    # Run parallel sequencer
    result_parallel = sequencer_logic.apply_sequencer_logic(
        test_data.copy(),
        stream_filters,
        display_year=None,
        parallel=True
    )
    
    # Run sequential sequencer
    result_sequential, _ = sequencer_logic.apply_sequencer_logic_with_state(
        test_data.copy(),
        stream_filters,
        display_year=None,
        parallel=False
    )
    
    # Assert equality
    assert_dataframe_equality(result_parallel, result_sequential, "Multiple streams parity test")
    print("✓ Multiple streams parity test passed")


def test_parallel_sequential_parity_notrade_days():
    """Test parity with NoTrade days."""
    print("\n" + "="*70)
    print("Test: Parallel vs Sequential Parity with NoTrade Days")
    print("="*70)
    
    # Create test data with some missing days (NoTrade)
    test_data = create_test_analyzer_output(
        streams=['ES1'],
        dates=['2025-01-06', '2025-01-08', '2025-01-10'],  # Missing 07 and 09
        times=['07:30', '08:00']
    )
    
    stream_filters = {}
    
    # Run parallel sequencer
    result_parallel = sequencer_logic.apply_sequencer_logic(
        test_data.copy(),
        stream_filters,
        display_year=None,
        parallel=True
    )
    
    # Run sequential sequencer
    result_sequential, _ = sequencer_logic.apply_sequencer_logic_with_state(
        test_data.copy(),
        stream_filters,
        display_year=None,
        parallel=False
    )
    
    # Assert equality
    assert_dataframe_equality(result_parallel, result_sequential, "NoTrade days parity test")
    print("✓ NoTrade days parity test passed")


def run_all_tests():
    """Run all parity tests."""
    print("\n" + "="*70)
    print("PARALLEL VS SEQUENTIAL DETERMINISM PARITY TESTS")
    print("="*70)
    print("\nThese tests ensure parallel and sequential sequencer logic produce")
    print("identical outputs to prevent rolling-resequence vs full-rebuild drift.\n")
    
    tests = [
        test_parallel_sequential_parity_basic,
        test_parallel_sequential_parity_with_filters,
        test_parallel_sequential_parity_time_changes,
        test_parallel_sequential_parity_multiple_streams,
        test_parallel_sequential_parity_notrade_days,
    ]
    
    passed = 0
    failed = 0
    
    for test_func in tests:
        try:
            test_func()
            passed += 1
        except AssertionError as e:
            print(f"\n✗ {test_func.__name__} FAILED: {e}")
            failed += 1
        except Exception as e:
            print(f"\n✗ {test_func.__name__} ERROR: {e}")
            import traceback
            traceback.print_exc()
            failed += 1
    
    print("\n" + "="*70)
    print(f"RESULTS: {passed} passed, {failed} failed")
    print("="*70)
    
    if failed > 0:
        print("\n⚠️  WARNING: Parity tests failed! This indicates non-determinism.")
        print("   Parallel and sequential sequencer logic must produce identical outputs.")
        sys.exit(1)
    else:
        print("\n✓ All parity tests passed!")
        sys.exit(0)


if __name__ == "__main__":
    run_all_tests()
