# Loss Logic Module Documentation

## Overview

The Loss Logic Module (`loss_logic.py`) provides comprehensive loss management functionality for the breakout trading system. It handles stop loss calculations, loss tracking, risk management, and loss analysis.

## Key Features

### 1. Stop Loss Management
- **Initial Stop Loss Calculation**: Configurable multiplier-based stop loss calculation
- **T1/T2 Adjustments**: Dynamic stop loss adjustments based on profit targets
- **Instrument-Specific Logic**: Special handling for different trading instruments (e.g., GC)
- **Risk-Reward Analysis**: Calculate and validate risk-reward ratios

### 2. Loss Tracking and Analysis
- **Daily Loss Tracking**: Monitor daily loss limits and totals
- **Loss Pattern Analysis**: Identify common loss types and patterns
- **Statistical Reporting**: Comprehensive loss statistics and metrics
- **Loss Classification**: Categorize losses by type and severity

### 3. Risk Management
- **Loss Limits**: Per-trade and daily loss limits
- **Account Risk**: Percentage-based risk management
- **Loss Validation**: Validate losses against risk parameters
- **Recommendations**: Dynamic stop loss recommendations based on market conditions

## Core Classes

### LossManager

The main class for loss management functionality.

```python
from logic.loss_logic import LossManager, StopLossConfig

# Create with default configuration
loss_manager = LossManager()

# Create with custom configuration
config = StopLossConfig(
    initial_multiplier=3.0,
    t1_adjustment="break_even",
    t2_adjustment="lock_profit",
    max_loss_per_trade=100.0,
    max_daily_loss=500.0
)
loss_manager = LossManager(config)
```

### StopLossConfig

Configuration class for loss management parameters.

```python
config = StopLossConfig(
    initial_multiplier=3.0,      # 3x target as initial stop
    t1_adjustment="break_even",  # T1 adjustment type
    t2_adjustment="lock_profit", # T2 adjustment type
    t2_profit_lock=0.65,         # Lock in 65% profit
    max_loss_per_trade=100.0,    # Max loss per trade
    max_daily_loss=500.0,        # Max daily loss
    risk_per_trade=0.02          # 2% risk per trade
)
```

## Usage Examples

### 1. Basic Stop Loss Calculation

```python
# Calculate initial stop loss
entry_price = 4500.0
direction = "Long"
target_pts = 10.0
instrument = "ES"

initial_sl = loss_manager.calculate_initial_stop_loss(
    entry_price, direction, target_pts, instrument
)
print(f"Initial Stop Loss: {initial_sl}")
```

### 2. T1/T2 Stop Loss Adjustments

```python
# T1 adjustment (65% of target reached)
t1_sl, t1_reason = loss_manager.adjust_stop_loss_t1(
    entry_price, direction, target_pts, instrument
)

# T2 adjustment (90% of target reached)
t2_sl, t2_reason = loss_manager.adjust_stop_loss_t2(
    entry_price, direction, target_pts, level_idx=0
)
```

### 3. Loss Detection and Calculation

```python
# Check if stop loss was hit
high = 4490.0
low = 4475.0
stop_hit = loss_manager.check_stop_loss_hit(high, low, initial_sl, direction)

# Calculate loss amount
exit_price = 4480.0
loss_amount = loss_manager.calculate_loss_amount(
    entry_price, exit_price, direction, instrument
)
```

### 4. Comprehensive Loss Analysis

```python
# Create detailed loss result
loss_result = loss_manager.create_loss_result(
    entry_price=entry_price,
    exit_price=exit_price,
    direction=direction,
    stop_loss_price=initial_sl,
    exit_time=pd.Timestamp.now(),
    loss_type=LossType.STOP_LOSS,
    stop_loss_type=StopLossType.INITIAL,
    target_price=entry_price + target_pts,
    peak_price=entry_price + 5.0,
    instrument=instrument,
    account_size=10000.0
)

print(f"Loss Amount: {loss_result.loss_amount}")
print(f"Risk-Reward Ratio: {loss_result.risk_reward_ratio}")
print(f"Acceptable Loss: {loss_result.is_acceptable_loss}")
```

### 5. Loss Tracking and Statistics

```python
# Record a loss
loss_manager.record_loss(
    loss_amount, 
    pd.Timestamp.now(), 
    LossType.STOP_LOSS, 
    "trade_001"
)

# Get loss statistics
stats = loss_manager.get_loss_statistics(days=30)
print(f"Total Losses: {stats['total_trades']}")
print(f"Average Loss: {stats['average_loss']}")

# Analyze loss patterns
patterns = loss_manager.analyze_loss_patterns()
print(f"Most Common Loss: {patterns['most_common_loss_type']}")
```

## Loss Types

### LossType Enum

- **STOP_LOSS**: Trade exited due to stop loss hit
- **TIME_EXPIRY**: Trade expired due to time limit
- **BREAK_EVEN**: Trade closed at break-even
- **PARTIAL_LOSS**: Partial loss before stop hit
- **MAX_LOSS**: Maximum loss limit reached

### StopLossType Enum

- **INITIAL**: Original stop loss level
- **T1_ADJUSTED**: Stop loss adjusted for T1 trigger
- **T2_ADJUSTED**: Stop loss adjusted for T2 trigger
- **TRAILING**: Trailing stop loss
- **BREAK_EVEN**: Stop loss moved to break-even

## Configuration Options

### StopLossConfig Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `initial_multiplier` | 3.0 | Multiplier for initial stop loss (3x target) |
| `t1_adjustment` | "break_even" | T1 adjustment type |
| `t2_adjustment` | "lock_profit" | T2 adjustment type |
| `t2_profit_lock` | 0.65 | Profit percentage to lock on T2 |
| `max_loss_per_trade` | 100.0 | Maximum loss per trade |
| `max_daily_loss` | 500.0 | Maximum daily loss |
| `risk_per_trade` | 0.02 | Risk percentage per trade |

### T1 Adjustment Options

- **"break_even"**: Move stop loss to entry price
- **"partial"**: Move stop loss to 25% profit
- **All instruments**: All instruments (including GC) now use standard break-even behavior

### T2 Adjustment Options

- **"lock_profit"**: Lock in specified percentage of profit
- **"trailing"**: Implement trailing stop logic

## Integration with Trading System

The loss logic module integrates seamlessly with the existing trading system:

### 1. PriceTracker Integration

```python
# PriceTracker now uses LossManager for stop loss calculations
price_tracker = PriceTracker(loss_config)
```

### 2. EntryDetector Integration

```python
# EntryDetector uses LossManager for initial stop loss calculation
entry_detector = EntryDetector(loss_config)
```

### 3. Engine Integration

```python
# Engine initializes loss management components
loss_config = StopLossConfig()
loss_manager = LossManager(loss_config)
entry_detector = EntryDetector(loss_config)
price_tracker = PriceTracker(loss_config)
```

## Advanced Features

### 1. Dynamic Stop Loss Recommendations

```python
# Get recommendations based on instrument and volatility
recommendations = loss_manager.get_stop_loss_recommendations(
    "ES", 
    volatility=1.2
)
```

### 2. Loss Validation

```python
# Validate loss against limits
is_valid = loss_manager.validate_loss_limits(
    loss_amount, 
    trade_date
)
```

### 3. Daily Loss Management

```python
# Get daily loss total
daily_loss = loss_manager.get_daily_loss(trade_date)

# Reset daily losses (call at start of new day)
loss_manager.reset_daily_losses()
```

## Error Handling

The module includes comprehensive error handling:

- **Invalid Configuration**: Validates configuration parameters
- **Missing Data**: Handles missing or invalid input data
- **Calculation Errors**: Graceful handling of calculation errors
- **Limit Exceeded**: Clear indication when limits are exceeded

## Performance Considerations

- **Efficient Calculations**: Optimized for high-frequency trading scenarios
- **Memory Management**: Minimal memory footprint for loss tracking
- **Fast Lookups**: Efficient data structures for loss statistics
- **Configurable Limits**: Adjustable limits to prevent memory issues

## Testing

The module includes comprehensive test coverage:

- **Unit Tests**: Individual method testing
- **Integration Tests**: End-to-end functionality testing
- **Edge Cases**: Boundary condition testing
- **Performance Tests**: Load and stress testing

## Future Enhancements

Planned enhancements include:

- **Machine Learning Integration**: AI-powered loss prediction
- **Advanced Risk Models**: Sophisticated risk assessment
- **Real-time Monitoring**: Live loss tracking and alerts
- **Portfolio-level Risk**: Cross-trade risk management
- **Backtesting Integration**: Historical loss analysis

## Support and Maintenance

For questions or issues with the loss logic module:

1. Check the example scripts in `examples/`
2. Review the configuration options
3. Examine the integration patterns
4. Consult the error messages and logs

The module is designed to be robust, maintainable, and easily extensible for future trading system enhancements.
