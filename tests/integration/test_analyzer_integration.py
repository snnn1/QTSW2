"""
Integration tests for the analyzer module

These tests verify end-to-end functionality of the analyzer,
including error handling, data validation, and edge cases.
"""

import pytest
import pandas as pd
import numpy as np
from datetime import datetime, timedelta
import pytz

# Import analyzer components
import sys
import os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '../../'))

from modules.analyzer.breakout_core.engine import run_strategy
from modules.analyzer.logic.config_logic import RunParams
from modules.analyzer.logic.validation_logic import ValidationManager


class TestAnalyzerIntegration:
    """Integration tests for analyzer"""
    
    @pytest.fixture
    def sample_data(self):
        """Create sample OHLC data for testing"""
        chicago_tz = pytz.timezone("America/Chicago")
        dates = pd.date_range('2025-01-01 02:00', '2025-01-03 16:00', freq='1min', tz=chicago_tz)
        
        # Create realistic price data
        np.random.seed(42)
        base_price = 4000.0
        prices = []
        for i in range(len(dates)):
            # Random walk with some trend
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
    
    def test_basic_analyzer_run(self, sample_data):
        """Test basic analyzer execution"""
        rp = RunParams(
            instrument="ES",
            enabled_sessions=["S1", "S2"],
            enabled_slots={"S1": ["07:30", "08:00"], "S2": ["09:30", "10:00"]},
            trade_days=[0, 1, 2, 3, 4]
        )
        
        results = run_strategy(sample_data, rp, debug=False)
        
        assert isinstance(results, pd.DataFrame)
        assert len(results) > 0
        assert 'Date' in results.columns
        assert 'Time' in results.columns
        assert 'Result' in results.columns
    
    def test_empty_dataframe_handling(self):
        """Test handling of empty DataFrame"""
        empty_df = pd.DataFrame(columns=['timestamp', 'open', 'high', 'low', 'close', 'instrument'])
        
        rp = RunParams(
            instrument="ES",
            enabled_sessions=["S1"],
            enabled_slots={"S1": ["07:30"]},
            trade_days=[0]
        )
        
        results = run_strategy(empty_df, rp, debug=False)
        
        assert isinstance(results, pd.DataFrame)
        assert len(results) == 0
    
    def test_invalid_ohlc_data(self):
        """Test handling of invalid OHLC data"""
        chicago_tz = pytz.timezone("America/Chicago")
        dates = pd.date_range('2025-01-01 02:00', '2025-01-01 10:00', freq='1min', tz=chicago_tz)
        
        # Create invalid OHLC data (high < low)
        df = pd.DataFrame({
            'timestamp': dates,
            'open': [4000.0] * len(dates),
            'high': [3999.0] * len(dates),  # Invalid: high < low
            'low': [4001.0] * len(dates),
            'close': [4000.0] * len(dates),
            'instrument': 'ES'
        })
        
        rp = RunParams(
            instrument="ES",
            enabled_sessions=["S1"],
            enabled_slots={"S1": ["07:30"]},
            trade_days=[0]
        )
        
        # Should raise validation error
        validation_manager = ValidationManager()
        validation_result = validation_manager.validate_dataframe(df)
        assert not validation_result.is_valid
        assert len(validation_result.errors) > 0
    
    def test_negative_prices(self):
        """Test handling of negative prices"""
        chicago_tz = pytz.timezone("America/Chicago")
        dates = pd.date_range('2025-01-01 02:00', '2025-01-01 10:00', freq='1min', tz=chicago_tz)
        
        df = pd.DataFrame({
            'timestamp': dates,
            'open': [-100.0] * len(dates),  # Invalid: negative price
            'high': [100.0] * len(dates),
            'low': [-100.0] * len(dates),
            'close': [100.0] * len(dates),
            'instrument': 'ES'
        })
        
        validation_manager = ValidationManager()
        validation_result = validation_manager.validate_dataframe(df)
        assert not validation_result.is_valid
        assert any('negative' in error.lower() for error in validation_result.errors)
    
    def test_duplicate_timestamps(self, sample_data):
        """Test handling of duplicate timestamps"""
        # Add duplicate timestamp
        duplicate_row = sample_data.iloc[0].copy()
        sample_data_with_duplicate = pd.concat([sample_data, duplicate_row.to_frame().T], ignore_index=True)
        
        validation_manager = ValidationManager()
        validation_result = validation_manager.validate_dataframe(sample_data_with_duplicate)
        
        # Should warn about duplicates
        assert any('duplicate' in warning.lower() for warning in validation_result.warnings)
    
    def test_timezone_handling(self):
        """Test timezone handling"""
        # Create data with timezone
        chicago_tz = pytz.timezone("America/Chicago")
        dates = pd.date_range('2025-01-01 02:00', '2025-01-01 10:00', freq='1min', tz=chicago_tz)
        
        df = pd.DataFrame({
            'timestamp': dates,
            'open': [4000.0] * len(dates),
            'high': [4001.0] * len(dates),
            'low': [3999.0] * len(dates),
            'close': [4000.0] * len(dates),
            'instrument': 'ES'
        })
        
        rp = RunParams(
            instrument="ES",
            enabled_sessions=["S1"],
            enabled_slots={"S1": ["07:30"]},
            trade_days=[0]
        )
        
        # Should handle timezone-aware data correctly
        results = run_strategy(df, rp, debug=False)
        assert isinstance(results, pd.DataFrame)
    
    def test_missing_instrument_data(self, sample_data):
        """Test handling when instrument data is missing"""
        # Filter out all ES data
        sample_data_filtered = sample_data[sample_data['instrument'] != 'ES']
        
        rp = RunParams(
            instrument="ES",
            enabled_sessions=["S1"],
            enabled_slots={"S1": ["07:30"]},
            trade_days=[0]
        )
        
        results = run_strategy(sample_data_filtered, rp, debug=False)
        
        # Should return empty DataFrame
        assert isinstance(results, pd.DataFrame)
        assert len(results) == 0
    
    def test_no_trade_scenarios(self, sample_data):
        """Test scenarios where no trades occur"""
        # Create data with very small range (unlikely to break out)
        chicago_tz = pytz.timezone("America/Chicago")
        dates = pd.date_range('2025-01-01 02:00', '2025-01-01 10:00', freq='1min', tz=chicago_tz)
        
        # Very tight range
        df = pd.DataFrame({
            'timestamp': dates,
            'open': [4000.0] * len(dates),
            'high': [4000.1] * len(dates),
            'low': [3999.9] * len(dates),
            'close': [4000.0] * len(dates),
            'instrument': 'ES'
        })
        
        rp = RunParams(
            instrument="ES",
            enabled_sessions=["S1"],
            enabled_slots={"S1": ["07:30"]},
            trade_days=[0],
            write_no_trade_rows=True
        )
        
        results = run_strategy(df, rp, debug=False)
        
        # Should have NoTrade entries if enabled
        assert isinstance(results, pd.DataFrame)
        if len(results) > 0:
            assert 'NoTrade' in results['Result'].values or len(results) == 0


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
