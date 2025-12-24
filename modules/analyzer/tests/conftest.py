"""
Pytest fixtures for DataManager tests
Provides synthetic data fixtures for comprehensive testing
"""

import pytest
import pandas as pd
import numpy as np
from datetime import datetime, timedelta
import pytz

# Timezone constants
CHICAGO_TZ = pytz.timezone("America/Chicago")
UTC_TZ = pytz.UTC


@pytest.fixture
def chicago_timestamp():
    """Helper to create Chicago timezone-aware timestamps"""
    def _create_ts(year, month, day, hour, minute, second=0):
        dt = datetime(year, month, day, hour, minute, second)
        return CHICAGO_TZ.localize(dt)
    return _create_ts


@pytest.fixture
def clean_dataset(chicago_timestamp):
    """
    Clean dataset with valid OHLC data, no duplicates, sorted timestamps
    """
    timestamps = [
        chicago_timestamp(2025, 1, 2, 7, 30),
        chicago_timestamp(2025, 1, 2, 7, 31),
        chicago_timestamp(2025, 1, 2, 7, 32),
        chicago_timestamp(2025, 1, 2, 7, 33),
        chicago_timestamp(2025, 1, 2, 7, 34),
    ]
    
    df = pd.DataFrame({
        'timestamp': timestamps,
        'open': [100.0, 100.5, 101.0, 101.5, 102.0],
        'high': [100.5, 101.0, 101.5, 102.0, 102.5],
        'low': [99.5, 100.0, 100.5, 101.0, 101.5],
        'close': [100.2, 100.7, 101.2, 101.7, 102.2],
        'volume': [1000, 1100, 1200, 1300, 1400],
        'instrument': ['ES'] * 5
    })
    
    return df


@pytest.fixture
def duplicate_timestamp_dataset(chicago_timestamp):
    """
    Dataset with duplicate timestamps (same timestamp, different OHLC values)
    """
    base_ts = chicago_timestamp(2025, 1, 2, 7, 30)
    
    # Create duplicate timestamps
    timestamps = [
        base_ts,
        base_ts,  # Duplicate
        chicago_timestamp(2025, 1, 2, 7, 31),
        base_ts,  # Another duplicate
        chicago_timestamp(2025, 1, 2, 7, 32),
    ]
    
    df = pd.DataFrame({
        'timestamp': timestamps,
        'open': [100.0, 100.1, 100.5, 100.2, 101.0],  # Different values for duplicates
        'high': [100.5, 100.6, 101.0, 100.7, 101.5],
        'low': [99.5, 99.6, 100.0, 99.7, 100.5],
        'close': [100.2, 100.3, 100.7, 100.4, 101.2],
        'volume': [1000, 1050, 1100, 1025, 1200],
        'instrument': ['ES'] * 5
    })
    
    return df


@pytest.fixture
def invalid_ohlc_dataset(chicago_timestamp):
    """
    Dataset with invalid OHLC relationships:
    - high < low
    - open < low
    - close > high
    """
    timestamps = [
        chicago_timestamp(2025, 1, 2, 7, 30),
        chicago_timestamp(2025, 1, 2, 7, 31),
        chicago_timestamp(2025, 1, 2, 7, 32),
    ]
    
    df = pd.DataFrame({
        'timestamp': timestamps,
        # Row 0: high < low (should be swapped)
        'open': [100.0, 100.5, 101.0],
        'high': [99.0, 101.0, 102.0],  # 99.0 < 100.0 (low) - INVALID
        'low': [100.0, 100.0, 100.5],
        'close': [99.5, 100.7, 101.5],
        # Row 1: open < low (should be clipped)
        # Row 2: close > high (should be clipped)
        'volume': [1000, 1100, 1200],
        'instrument': ['ES'] * 3
    })
    
    # Manually set invalid values for row 1 and 2
    df.loc[1, 'open'] = 99.5  # open < low (100.0)
    df.loc[2, 'close'] = 102.5  # close > high (102.0)
    
    return df


@pytest.fixture
def missing_bar_dataset(chicago_timestamp):
    """
    Dataset with missing 1-minute bars (gaps in time series)
    """
    # Create timestamps with gaps: 7:30, 7:31, 7:33, 7:35 (missing 7:32, 7:34)
    timestamps = [
        chicago_timestamp(2025, 1, 2, 7, 30),
        chicago_timestamp(2025, 1, 2, 7, 31),
        chicago_timestamp(2025, 1, 2, 7, 33),  # Gap: missing 7:32
        chicago_timestamp(2025, 1, 2, 7, 35),  # Gap: missing 7:34
    ]
    
    df = pd.DataFrame({
        'timestamp': timestamps,
        'open': [100.0, 100.5, 101.0, 101.5],
        'high': [100.5, 101.0, 101.5, 102.0],
        'low': [99.5, 100.0, 100.5, 101.0],
        'close': [100.2, 100.7, 101.2, 101.7],
        'volume': [1000, 1100, 1200, 1300],
        'instrument': ['ES'] * 4
    })
    
    return df


@pytest.fixture
def timezone_mixed_dataset():
    """
    Dataset with mixed timezones: naive, UTC, and Chicago
    """
    # Naive timestamps (as pandas Timestamps without timezone)
    naive_ts1 = pd.Timestamp(datetime(2025, 1, 2, 7, 30))
    naive_ts2 = pd.Timestamp(datetime(2025, 1, 2, 7, 31))
    
    # UTC timestamps
    utc_ts1 = pd.Timestamp(datetime(2025, 1, 2, 13, 30), tz=UTC_TZ)  # 7:30 AM Chicago = 1:30 PM UTC
    utc_ts2 = pd.Timestamp(datetime(2025, 1, 2, 13, 31), tz=UTC_TZ)
    
    # Chicago timestamps
    chicago_ts1 = pd.Timestamp(datetime(2025, 1, 2, 7, 32), tz=CHICAGO_TZ)
    chicago_ts2 = pd.Timestamp(datetime(2025, 1, 2, 7, 33), tz=CHICAGO_TZ)
    
    timestamps = [naive_ts1, naive_ts2, utc_ts1, utc_ts2, chicago_ts1, chicago_ts2]
    
    df = pd.DataFrame({
        'timestamp': timestamps,
        'open': [100.0, 100.5, 101.0, 101.5, 102.0, 102.5],
        'high': [100.5, 101.0, 101.5, 102.0, 102.5, 103.0],
        'low': [99.5, 100.0, 100.5, 101.0, 101.5, 102.0],
        'close': [100.2, 100.7, 101.2, 101.7, 102.2, 102.7],
        'volume': [1000, 1100, 1200, 1300, 1400, 1500],
        'instrument': ['ES'] * 6
    })
    
    # Keep as object dtype (pandas can't convert mixed timezone-aware/naive to datetime64)
    # DataManager will handle the conversion
    # This simulates real-world data that may have mixed timezone types
    return df


@pytest.fixture
def unsorted_dataset(chicago_timestamp):
    """
    Dataset with unsorted timestamps (out of chronological order)
    """
    timestamps = [
        chicago_timestamp(2025, 1, 2, 7, 33),  # Out of order
        chicago_timestamp(2025, 1, 2, 7, 30),  # Should be first
        chicago_timestamp(2025, 1, 2, 7, 32),  # Out of order
        chicago_timestamp(2025, 1, 2, 7, 31),  # Out of order
        chicago_timestamp(2025, 1, 2, 7, 34),  # Should be last
    ]
    
    df = pd.DataFrame({
        'timestamp': timestamps,
        'open': [101.5, 100.0, 101.0, 100.5, 102.0],
        'high': [102.0, 100.5, 101.5, 101.0, 102.5],
        'low': [101.0, 99.5, 100.5, 100.0, 101.5],
        'close': [101.7, 100.2, 101.2, 100.7, 102.2],
        'volume': [1300, 1000, 1200, 1100, 1400],
        'instrument': ['ES'] * 5
    })
    
    return df


@pytest.fixture
def malformed_dataset():
    """
    Dataset with missing required columns or wrong data types
    """
    # Missing 'instrument' column
    df = pd.DataFrame({
        'timestamp': [datetime(2025, 1, 2, 7, 30)],
        'open': [100.0],
        'high': [100.5],
        'low': [99.5],
        'close': [100.2],
        'volume': [1000],
        # Missing 'instrument' column
    })
    
    return df


@pytest.fixture
def wrong_dtype_dataset(chicago_timestamp):
    """
    Dataset with wrong data types (e.g., strings instead of floats)
    """
    df = pd.DataFrame({
        'timestamp': [chicago_timestamp(2025, 1, 2, 7, 30)],
        'open': ['100.0'],  # String instead of float
        'high': [100.5],
        'low': [99.5],
        'close': [100.2],
        'volume': [1000],
        'instrument': ['ES']
    })
    
    return df

