# Modular Trading Logic Overview

## Overview
The trading system has been refactored into modular components for better maintainability, testability, and clarity. Each module handles a specific aspect of the trading logic.

## Module Structure

### 1. Range Detection Logic (`range_logic.py`)
**Purpose**: Calculate trading ranges for different time slots and sessions

**Key Components**:
- `RangeDetector`: Main class for range calculations
- `RangeResult`: Data class containing range details
- `calculate_range()`: Calculate range for specific date/time slot
- `calculate_breakout_levels()`: Calculate long/short breakout levels
- `validate_range()`: Validate range meets minimum requirements

**Usage**:
```python
from breakout_core.range_logic import RangeDetector

detector = RangeDetector(slot_config)
range_result = detector.calculate_range(df, date, "08:00", "S1")
```

### 2. MFE Calculation Logic (Integrated into Price Tracking)
**Purpose**: Calculate Maximum Favorable Excursion (peak) for trades - now integrated into price tracking

**Key Components**:
- `_calculate_final_mfe()`: Calculate complete MFE from entry until next time slot
- `_get_trigger_thresholds()`: Get T1/T2 trigger thresholds by level
- `_calculate_favorable_movement()`: Calculate favorable movement from entry price

**Note**: MFE logic is now fully integrated into the Price Tracking system for better accuracy and consistency.

### 3. Break-Even Logic (Integrated into Price Tracking)
**Purpose**: Handle T1/T2 trigger system and stop loss adjustments - now integrated into price tracking

**Key Components**:
- `_adjust_stop_loss_t1()`: Adjust stop loss for T1 trigger
- `_adjust_stop_loss_t2()`: Adjust stop loss for T2 trigger  
- `_classify_result()`: Classify trade result based on triggers
- `calculate_profit()`: Calculate profit based on result and triggers

**Note**: Break-even logic is now fully integrated into the Price Tracking system for real-time stop loss adjustments.

### 4. Entry Logic (`entry_logic.py`)
**Purpose**: Handle trade entry detection and validation

**Key Components**:
- `EntryDetector`: Main class for entry detection
- `EntryResult`: Data class containing entry details
- `detect_entry()`: Detect trade entry using breakout detection
- `validate_entry()`: Validate if entry meets requirements
- `calculate_target_level()`: Calculate target level for trade
- `calculate_stop_loss()`: Calculate initial stop loss

**Usage**:
```python
from breakout_core.entry_logic import EntryDetector

detector = EntryDetector()
entry_result = detector.detect_entry(df, range_result, brk_long, brk_short, freeze_close, end_ts)
```

### 5. Price Tracking Logic (`price_tracking_logic.py`)
**Purpose**: Handle real-time price monitoring, trade execution, MFE calculation, and break-even logic

**Key Components**:
- `PriceTracker`: Main class for integrated price tracking
- `TradeExecution`: Data class containing complete execution details
- `execute_trade()`: Execute trade with integrated MFE and break-even logic
- `_simulate_intra_bar_execution()`: Simulate realistic intra-bar execution using OHLC data
- `_simulate_realistic_entry()`: Simulate realistic entry execution at breakout levels
- `calculate_profit()`: Calculate profit based on result and triggers
- `_calculate_final_mfe()`: Calculate complete MFE from entry until next time slot

**Advanced Features**:
- **Intra-Bar Execution Simulation**: Uses sophisticated logic to determine realistic execution order when both target and stop are possible in the same bar
- **Price Action Analysis**: Considers bar momentum (bullish/bearish) and relative positions
- **Distance-Based Decisions**: Uses distance ratios and position analysis for realistic outcomes
- **Integrated MFE Calculation**: Real-time MFE tracking with accurate trigger detection
- **Real-Time Stop Loss Adjustments**: T1/T2 triggers adjust stop loss during trade execution

**Usage**:
```python
from breakout_core.price_tracking_logic import PriceTracker

tracker = PriceTracker()
execution = tracker.execute_trade(
    df, entry_time, entry_price, direction, target_level, stop_loss, 
    expiry_time, target_pts, level_idx, instrument, time_label, date
)
```

## Benefits of Modular Design

### 1. **Separation of Concerns**
- Each module handles one specific aspect
- Easier to understand and maintain
- Clear boundaries between different logic components

### 2. **Testability**
- Each module can be tested independently
- Easier to write unit tests for specific functionality
- Better debugging and error isolation

### 3. **Reusability**
- Modules can be reused in different contexts
- Easy to swap out implementations
- Promotes code reuse

### 4. **Maintainability**
- Changes to one module don't affect others
- Easier to add new features
- Better code organization

### 5. **Debugging**
- Easier to isolate issues to specific modules
- Better error reporting
- Clearer data flow

## Integration Example

The `integrated_engine.py` shows how to use all modules together:

```python
from breakout_core.integrated_engine import IntegratedTradingEngine

# Initialize engine with slot configuration
engine = IntegratedTradingEngine(slot_config)

# Run strategy
results = engine.run_strategy(df, params)
```

## Migration Path

1. **Current State**: Monolithic `engine.py` with all logic combined
2. **Modular State**: Separate modules for each logic component
3. **Future State**: Can gradually migrate to use modular components

## File Structure
```
breakout_core/
├── __init__.py
├── config.py
├── engine.py                    # Current monolithic engine
├── integrated_engine.py         # New modular engine
├── range_logic.py              # Range detection
├── mfe_logic.py                # MFE calculation
├── break_even_logic.py         # Break-even logic
├── entry_logic.py              # Entry detection
├── price_tracking_logic.py     # Price tracking
└── utils.py
```

## Next Steps

1. **Test modular components** individually
2. **Integrate with existing system** gradually
3. **Add comprehensive unit tests** for each module
4. **Update documentation** as needed
5. **Consider migrating** from monolithic to modular approach


