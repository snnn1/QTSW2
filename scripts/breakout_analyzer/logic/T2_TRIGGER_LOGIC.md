# T2 Trigger Logic Documentation

## Overview
This document explains the T2 (Trigger 2) logic for the breakout trading system. T2 triggers are used to move the stop loss to lock in profit when a trade reaches a certain percentage of its target.

## T2 Trigger Thresholds by Level

### Level 1 (level_idx = 0)
- **Target**: 10 points (ES/MES), 50 points (NQ/MNQ), etc.
- **T1 Threshold**: 65% of target (6.5 points for 10-point target)
- **T2 Threshold**: 95% of target (9.5 points for 10-point target)
- **T2 Profit Lock**: 65% of target (6.5 points for 10-point target)

### Level 2 (level_idx = 1)
- **Target**: 15 points (ES/MES), 75 points (NQ/MNQ), etc.
- **T1 Threshold**: 50% of target (7.5 points for 15-point target)
- **T2 Threshold**: 90% of target (13.5 points for 15-point target)
- **T2 Profit Lock**: 65% of target (9.75 points for 15-point target)

### Level 3+ (level_idx >= 2)
- **Target**: 20 points (ES/MES), 100 points (NQ/MNQ), etc.
- **T1 Threshold**: 45% of target (9.0 points for 20-point target)
- **T2 Threshold**: 90% of target (18.0 points for 20-point target)
- **T2 Profit Lock**: 75% of target (15.0 points for 20-point target)

## Implementation Details

### Code Location
The T2 trigger logic is implemented in:
- `scripts/breakout_analyzer/logic/price_tracking_logic.py` - Main implementation
- `scripts/breakout_analyzer/logic/break_even_logic.py` - Legacy implementation
- `scripts/breakout_analyzer/logic/mfe_logic.py` - MFE calculation

### Key Methods

#### `_get_trigger_thresholds(target_pts, level_idx)`
```python
def _get_trigger_thresholds(self, target_pts: float, level_idx: int) -> Tuple[float, float]:
    """Get T1/T2 trigger thresholds based on level"""
    if level_idx == 0:  # Level 1
        t1_threshold = target_pts * 0.65  # 65% of target
        t2_threshold = target_pts * 0.95  # 95% of target
    elif level_idx == 1:  # Level 2
        t1_threshold = target_pts * 0.50  # 50% of target
        t2_threshold = target_pts * 0.90  # 90% of target
    else:  # Level 3+
        t1_threshold = target_pts * 0.45  # 45% of target
        t2_threshold = target_pts * 0.90  # 90% of target (same as Level 2)
    
    return t1_threshold, t2_threshold
```

#### `_adjust_stop_loss_t2(entry_price, direction, target_pts, level_idx)`
```python
def _adjust_stop_loss_t2(self, entry_price: float, direction: str, 
                        target_pts: float, level_idx: int) -> float:
    """Adjust stop loss for T2 trigger"""
    # T2 triggered: Move stop loss to lock in profit
    # Level 3+ gets 75% profit, others get 65%
    if level_idx >= 2:  # Level 3+
        lock_profit = target_pts * 0.75  # 75% profit for Level 3+
    else:
        lock_profit = target_pts * 0.65  # 65% profit for Level 1-2
        
    if direction == "Long":
        return entry_price + lock_profit
    else:
        return entry_price - lock_profit
```

## Trade Classification Logic

### Result Classification
```python
def _classify_result(self, t1_triggered: bool, t2_triggered: bool,
                    exit_reason: str, target_hit: bool = False) -> str:
    """Classify trade result based on triggers and exit reason"""
    # Check if target was hit first - this overrides everything
    if target_hit or exit_reason == "Win":
        return "Win"  # Full target reached = Full profit
    
    # Handle time expiry first - this should override trigger status
    if exit_reason == "TIME":
        return "TIME"
    
    # Any T2 trigger = Win (regardless of whether target was hit)
    if t2_triggered:
        return "Win"  # T2 triggered = Win (profit will be calculated correctly)
    elif t1_triggered:
        return "BE"   # T1 triggered but not T2 = Break Even
    else:
        return "Loss" # No triggers hit = Loss
```

### Profit Calculation
```python
def calculate_profit(self, entry_price: float, exit_price: float,
                    direction: str, result: str, 
                    t1_triggered: bool, t2_triggered: bool,
                    target_pts: float, level_idx: int = 0, instrument: str = "ES",
                    target_hit: bool = False) -> float:
    """Calculate profit based on result and triggers"""
    # ... (PnL calculation) ...
    
    if result == "Win":
        # Win trades: Check if it was a full target hit or T2 stop out
        if target_hit:
            # Full target reached = Full profit
            return target_pts
        elif t2_triggered:
            # T2 triggered but stopped out: Level 3+ gets 75% profit
            if level_idx >= 2:
                return target_pts * 0.75
            else:
                return target_pts * 0.65  # Level 1-2 gets 65% profit
        else:
            # Other win cases (shouldn't happen)
            return target_pts
    elif result == "BE":
        # Break-even trades: 0 profit
        return 0.0
    else:
        # Loss trades: Use actual PnL
        return pnl_pts
```

## Examples

### Example 1: Level 3 Trade (20-point target)
- **Entry**: Long at 4000.25
- **Target**: 4020.25 (20 points)
- **T2 Threshold**: 18.0 points (90% of 20)
- **Peak**: 18.25 points
- **Result**: T2 triggered → "Win" with 15.0 points profit (75% of 20)

### Example 2: Level 2 Trade (15-point target)
- **Entry**: Long at 4000.25
- **Target**: 4015.25 (15 points)
- **T2 Threshold**: 13.5 points (90% of 15)
- **Peak**: 14.0 points
- **Result**: T2 triggered → "Win" with 9.75 points profit (65% of 15)

### Example 3: Level 1 Trade (10-point target)
- **Entry**: Long at 4000.25
- **Target**: 4010.25 (10 points)
- **T2 Threshold**: 9.5 points (95% of 10)
- **Peak**: 9.0 points
- **Result**: T1 triggered only → "BE" with 0 profit

## Key Points

1. **Level 2 and Level 3** both use **90% T2 threshold**
2. **Level 1** uses **95% T2 threshold** (more conservative)
3. **Level 3+** gets **75% profit lock** on T2 trigger
4. **Level 1-2** get **65% profit lock** on T2 trigger
5. **All T2 triggers** result in "Win" classification (not "T2_Win")
6. **Profit calculation** distinguishes between full target hits and T2 stop outs

## Historical Changes

- **2024-01-18**: Changed Level 3 T2 threshold from 95% to 90% to match Level 2
- **2024-01-18**: Removed "T2_Win" from results, all wins show as "Win"
- **2024-01-18**: Fixed profit calculation to give full target profit for target hits, T2 profit for T2 stop outs

## Testing

The T2 trigger logic is tested with:
- Exact threshold testing (18.0 points for Level 3)
- Profit calculation verification
- Result classification validation
- Cross-level consistency checks

All tests confirm that Level 2 and Level 3 T2 triggers work correctly at 90% thresholds.
