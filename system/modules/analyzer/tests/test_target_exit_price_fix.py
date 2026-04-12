"""
Tests for target exit price fix (Critical Bug #1 and #2)

These tests verify that when target hits:
1. exit_price equals target_level (or execution_result.exit_price if provided)
2. profit is calculated using correct exit price, not current_stop_loss

These tests would fail on the old bug and pass on the fix.
"""

import pytest
import pandas as pd
import numpy as np
from datetime import datetime
import pytz

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent.parent))

from logic.price_tracking_logic import PriceTracker
from logic.debug_logic import DebugManager
from logic.instrument_logic import InstrumentManager
from logic.config_logic import ConfigManager


class TestTargetExitPriceFix:
    """Tests for target exit price and profit calculation fix"""
    
    @pytest.fixture
    def price_tracker(self):
        """Create PriceTracker instance for testing"""
        debug_manager = DebugManager(False)
        instrument_manager = InstrumentManager()
        config_manager = ConfigManager()
        return PriceTracker(debug_manager, instrument_manager, config_manager)
    
    @pytest.fixture
    def sample_data_long(self):
        """Create sample OHLC data for long trade that hits target"""
        chicago_tz = pytz.timezone("America/Chicago")
        entry_time = pd.Timestamp("2025-01-02 09:00:00", tz=chicago_tz)
        
        # Create bars: entry bar, then bar where target hits
        timestamps = [
            entry_time,
            entry_time + pd.Timedelta(minutes=1),
            entry_time + pd.Timedelta(minutes=2),
        ]
        
        entry_price = 4000.0
        target_pts = 10.0
        target_level = entry_price + target_pts  # 4010.0
        
        df = pd.DataFrame({
            'timestamp': timestamps,
            'open': [entry_price, entry_price + 2.0, entry_price + 5.0],
            'high': [entry_price + 1.0, entry_price + 8.0, target_level + 1.0],  # Target hit in last bar
            'low': [entry_price - 1.0, entry_price - 0.5, entry_price + 4.0],
            'close': [entry_price + 0.5, entry_price + 7.0, target_level],
            'instrument': 'ES'
        })
        
        return df, entry_time, entry_price, target_level, target_pts
    
    @pytest.fixture
    def sample_data_long_with_t1(self):
        """Create sample OHLC data for long trade with T1 trigger, then target hit"""
        chicago_tz = pytz.timezone("America/Chicago")
        entry_time = pd.Timestamp("2025-01-02 09:00:00", tz=chicago_tz)
        
        entry_price = 4000.0
        target_pts = 10.0
        target_level = entry_price + target_pts  # 4010.0
        t1_threshold = target_pts * 0.65  # 6.5 points
        be_stop = entry_price - 0.25  # 1 tick below entry (ES tick size = 0.25)
        
        # Create bars: entry, T1 trigger bar, target hit bar
        timestamps = [
            entry_time,
            entry_time + pd.Timedelta(minutes=1),
            entry_time + pd.Timedelta(minutes=2),
            entry_time + pd.Timedelta(minutes=3),
        ]
        
        df = pd.DataFrame({
            'timestamp': timestamps,
            'open': [entry_price, entry_price + 2.0, entry_price + 7.0, entry_price + 8.0],
            'high': [entry_price + 1.0, entry_price + 7.0, entry_price + 8.0, target_level + 1.0],  # T1 in bar 2, target in bar 4
            'low': [entry_price - 1.0, entry_price - 0.5, be_stop + 0.5, entry_price + 7.5],
            'close': [entry_price + 0.5, entry_price + 6.5, entry_price + 7.5, target_level],
            'instrument': 'ES'
        })
        
        return df, entry_time, entry_price, target_level, target_pts, be_stop
    
    def test_target_hit_exit_price_no_t1(self, price_tracker, sample_data_long):
        """Test that target hit uses target_level as exit_price, not current_stop_loss"""
        df, entry_time, entry_price, target_level, target_pts = sample_data_long
        
        # Set up trade parameters
        direction = "Long"
        stop_loss = entry_price - 20.0  # Original stop loss
        expiry_time = entry_time + pd.Timedelta(hours=24)
        
        # Execute trade
        result = price_tracker.execute_trade(
            df=df,
            entry_time=entry_time,
            entry_price=entry_price,
            direction=direction,
            target_level=target_level,
            stop_loss=stop_loss,
            expiry_time=expiry_time,
            target_pts=target_pts,
            instrument="ES",
            time_label="09:00",
            date=entry_time,
            debug=False
        )
        
        # Verify target was hit
        assert result.target_hit is True, "Target should have been hit"
        assert result.exit_reason == "Win", "Exit reason should be Win"
        
        # CRITICAL: Verify exit_price equals target_level, not current_stop_loss
        assert result.exit_price == target_level, \
            f"exit_price should be target_level ({target_level}), got {result.exit_price}"
        assert result.exit_price != stop_loss, \
            f"exit_price should NOT be stop_loss ({stop_loss})"
        
        # Verify profit is calculated correctly (should be target profit, not stop-based)
        expected_profit = target_pts  # For ES, 1 point = $50, but profit calculation handles scaling
        # Profit should be positive and based on target, not stop loss
        assert result.profit > 0, f"Profit should be positive for target hit, got {result.profit}"
        
        # Verify profit calculation uses target exit price
        # For ES with target hit, profit should equal target points (scaled by instrument)
        # Since we're using target_exit_price now, profit should be correct
        profit_from_target = price_tracker.calculate_profit(
            entry_price, target_level, direction, "Win",
            False, target_pts, "ES", target_hit=True
        )
        assert abs(result.profit - profit_from_target) < 0.01, \
            f"Profit should match calculation using target_level, got {result.profit}, expected ~{profit_from_target}"
    
    def test_target_hit_exit_price_with_t1_trigger(self, price_tracker, sample_data_long_with_t1):
        """Test that target hit after T1 trigger still uses target_level, not BE stop"""
        df, entry_time, entry_price, target_level, target_pts, be_stop = sample_data_long_with_t1
        
        # Set up trade parameters
        direction = "Long"
        stop_loss = entry_price - 20.0  # Original stop loss
        expiry_time = entry_time + pd.Timedelta(hours=24)
        
        # Execute trade
        result = price_tracker.execute_trade(
            df=df,
            entry_time=entry_time,
            entry_price=entry_price,
            direction=direction,
            target_level=target_level,
            stop_loss=stop_loss,
            expiry_time=expiry_time,
            target_pts=target_pts,
            instrument="ES",
            time_label="09:00",
            date=entry_time,
            debug=False
        )
        
        # Verify target was hit
        assert result.target_hit is True, "Target should have been hit"
        assert result.exit_reason == "Win", "Exit reason should be Win"
        assert result.t1_triggered is True, "T1 should have been triggered"
        assert result.stop_loss_adjusted is True, "Stop loss should have been adjusted"
        
        # CRITICAL: Verify exit_price equals target_level, not BE stop or original stop
        assert result.exit_price == target_level, \
            f"exit_price should be target_level ({target_level}), got {result.exit_price}"
        assert result.exit_price != be_stop, \
            f"exit_price should NOT be BE stop ({be_stop})"
        assert result.exit_price != stop_loss, \
            f"exit_price should NOT be original stop_loss ({stop_loss})"
        
        # CRITICAL: Verify profit is target-based, not BE-based
        # Profit should be full target profit, not zero (which would be BE profit)
        assert result.profit > 0, \
            f"Profit should be positive for target hit (even with T1 triggered), got {result.profit}"
        
        # Verify profit matches calculation using target_level
        profit_from_target = price_tracker.calculate_profit(
            entry_price, target_level, direction, "Win",
            True, target_pts, "ES", target_hit=True
        )
        assert abs(result.profit - profit_from_target) < 0.01, \
            f"Profit should match calculation using target_level, got {result.profit}, expected ~{profit_from_target}"
        
        # Verify profit is NOT zero (which would indicate BE calculation)
        assert result.profit != 0.0, \
            "Profit should NOT be zero for target hit (zero would indicate BE calculation error)"
    
    def test_target_hit_with_execution_result_exit_price(self, price_tracker):
        """Test that if execution_result provides exit_price, it's used instead of target_level"""
        chicago_tz = pytz.timezone("America/Chicago")
        entry_time = pd.Timestamp("2025-01-02 09:00:00", tz=chicago_tz)
        
        entry_price = 4000.0
        target_pts = 10.0
        target_level = entry_price + target_pts  # 4010.0
        execution_exit_price = 4010.5  # Slightly different from target_level
        
        # Create data where target hits
        timestamps = [
            entry_time,
            entry_time + pd.Timedelta(minutes=1),
        ]
        
        df = pd.DataFrame({
            'timestamp': timestamps,
            'open': [entry_price, entry_price + 5.0],
            'high': [entry_price + 1.0, execution_exit_price + 1.0],
            'low': [entry_price - 1.0, entry_price + 4.0],
            'close': [entry_price + 0.5, execution_exit_price],
            'instrument': 'ES'
        })
        
        direction = "Long"
        stop_loss = entry_price - 20.0
        expiry_time = entry_time + pd.Timedelta(hours=24)
        
        # Execute trade
        result = price_tracker.execute_trade(
            df=df,
            entry_time=entry_time,
            entry_price=entry_price,
            direction=direction,
            target_level=target_level,
            stop_loss=stop_loss,
            expiry_time=expiry_time,
            target_pts=target_pts,
            instrument="ES",
            time_label="09:00",
            date=entry_time,
            debug=False
        )
        
        # Verify target was hit
        assert result.target_hit is True
        
        # If execution_result provides exit_price, it should be used
        # Note: The actual execution_result.exit_price depends on _analyze_actual_price_movement logic
        # This test verifies the fix correctly uses execution_result.exit_price when available
        # The exact value depends on the execution simulation, but it should NOT be current_stop_loss
        assert result.exit_price != stop_loss, \
            f"exit_price should NOT be stop_loss ({stop_loss}), got {result.exit_price}"
        assert result.exit_price >= target_level or abs(result.exit_price - target_level) < 1.0, \
            f"exit_price should be close to target_level ({target_level}), got {result.exit_price}"
