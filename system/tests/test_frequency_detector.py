"""
Unit Tests for translator.frequency_detector module
Tests for tick vs minute data detection
"""

import pytest
import pandas as pd
from datetime import datetime, timedelta

from translator.frequency_detector import (
    detect_data_frequency,
    is_tick_data,
    is_minute_data,
    get_data_type_summary
)


class TestDetectDataFrequency:
    """Tests for detect_data_frequency() function"""
    
    def test_detect_tick_data(self):
        """Test: Detects tick data (sub-second intervals)"""
        # Create tick data with 0.5 second intervals
        timestamps = []
        base_time = datetime(2024, 1, 15, 9, 30, 0)
        for i in range(10):
            timestamps.append(base_time + timedelta(seconds=i * 0.5))
        
        df = pd.DataFrame({
            'timestamp': timestamps,
            'open': [4825.0] * 10,
            'high': [4826.0] * 10,
            'low': [4824.0] * 10,
            'close': [4825.5] * 10,
            'volume': [1000] * 10
        })
        
        result = detect_data_frequency(df)
        assert result == 'tick'
    
    def test_detect_minute_data(self):
        """Test: Detects 1-minute bar data"""
        # Create minute data with 60 second intervals
        timestamps = []
        base_time = datetime(2024, 1, 15, 9, 30, 0)
        for i in range(10):
            timestamps.append(base_time + timedelta(minutes=i))
        
        df = pd.DataFrame({
            'timestamp': timestamps,
            'open': [4825.0] * 10,
            'high': [4826.0] * 10,
            'low': [4824.0] * 10,
            'close': [4825.5] * 10,
            'volume': [100000] * 10
        })
        
        result = detect_data_frequency(df)
        assert result == '1min'
    
    def test_detect_5min_data(self):
        """Test: Detects 5-minute bar data"""
        timestamps = []
        base_time = datetime(2024, 1, 15, 9, 30, 0)
        for i in range(10):
            timestamps.append(base_time + timedelta(minutes=i * 5))
        
        df = pd.DataFrame({
            'timestamp': timestamps,
            'open': [4825.0] * 10,
            'high': [4826.0] * 10,
            'low': [4824.0] * 10,
            'close': [4825.5] * 10,
            'volume': [500000] * 10
        })
        
        result = detect_data_frequency(df)
        assert result == '5min'
    
    def test_detect_high_frequency_tick(self):
        """Test: Detects high-frequency tick data (few seconds apart)"""
        timestamps = []
        base_time = datetime(2024, 1, 15, 9, 30, 0)
        for i in range(10):
            timestamps.append(base_time + timedelta(seconds=i * 3))  # 3 second intervals
        
        df = pd.DataFrame({
            'timestamp': timestamps,
            'open': [4825.0] * 10,
            'high': [4826.0] * 10,
            'low': [4824.0] * 10,
            'close': [4825.5] * 10,
            'volume': [5000] * 10
        })
        
        result = detect_data_frequency(df)
        assert result == 'tick'


class TestIsTickData:
    """Tests for is_tick_data() function"""
    
    def test_identifies_tick_data(self):
        """Test: Returns True for tick data"""
        timestamps = [datetime(2024, 1, 15, 9, 30, 0) + timedelta(seconds=i * 0.1) for i in range(10)]
        df = pd.DataFrame({'timestamp': timestamps, 'close': [4825.0] * 10})
        
        assert is_tick_data(df) is True
    
    def test_identifies_minute_data_as_not_tick(self):
        """Test: Returns False for minute data"""
        timestamps = [datetime(2024, 1, 15, 9, 30, 0) + timedelta(minutes=i) for i in range(10)]
        df = pd.DataFrame({'timestamp': timestamps, 'close': [4825.0] * 10})
        
        assert is_tick_data(df) is False


class TestIsMinuteData:
    """Tests for is_minute_data() function"""
    
    def test_identifies_minute_data(self):
        """Test: Returns True for minute data"""
        timestamps = [datetime(2024, 1, 15, 9, 30, 0) + timedelta(minutes=i) for i in range(10)]
        df = pd.DataFrame({'timestamp': timestamps, 'close': [4825.0] * 10})
        
        assert is_minute_data(df) is True
    
    def test_identifies_tick_data_as_not_minute(self):
        """Test: Returns False for tick data"""
        timestamps = [datetime(2024, 1, 15, 9, 30, 0) + timedelta(seconds=i * 0.1) for i in range(10)]
        df = pd.DataFrame({'timestamp': timestamps, 'close': [4825.0] * 10})
        
        assert is_minute_data(df) is False


class TestGetDataTypeSummary:
    """Tests for get_data_type_summary() function"""
    
    def test_summary_for_tick_data(self):
        """Test: Provides correct summary for tick data"""
        timestamps = [datetime(2024, 1, 15, 9, 30, 0) + timedelta(seconds=i * 0.5) for i in range(10)]
        df = pd.DataFrame({'timestamp': timestamps, 'close': [4825.0] * 10})
        
        summary = get_data_type_summary(df)
        
        assert summary['frequency'] == 'tick'
        assert summary['is_tick'] is True
        assert summary['is_minute'] is False
        assert summary['total_rows'] == 10
        assert summary['time_range_start'] == timestamps[0]
        assert summary['time_range_end'] == timestamps[-1]
    
    def test_summary_for_minute_data(self):
        """Test: Provides correct summary for minute data"""
        timestamps = [datetime(2024, 1, 15, 9, 30, 0) + timedelta(minutes=i) for i in range(10)]
        df = pd.DataFrame({'timestamp': timestamps, 'close': [4825.0] * 10})
        
        summary = get_data_type_summary(df)
        
        assert summary['frequency'] == '1min'
        assert summary['is_tick'] is False
        assert summary['is_minute'] is True
        assert summary['total_rows'] == 10

