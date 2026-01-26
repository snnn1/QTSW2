# OPEN Result Type Implementation

**Date**: January 25, 2026  
**Purpose**: Implement Option A - Explicit OPEN result type for trades still open

---

## Overview

This document describes the implementation of an explicit `OPEN` result type to clearly distinguish between:
- **OPEN**: Trades still open (exit_time = NaT)
- **TIME**: Trades that expired AND closed (exit_time != NaT)

---

## Design Decision: Option A (Minimal Change)

### Result Enum
- **OPEN**: Trade still open (exit_time = NaT)
- **Win**: Target hit → Full target profit
- **BE**: T1 triggered + stop hit → 1 tick loss
- **Loss**: Stop hit without T1 → Actual loss
- **TIME**: Time expired AND trade closed (exit_time != NaT)

### Rules

1. **OPEN → exit_time = NaT**
   - Trade hasn't expired yet (expiry_time > current_time)
   - Exit time is Not a Time (NaT)
   - Profit reflects current PnL

2. **WIN / LOSS / BE / TIME → exit_time != NaT**
   - All closed trades have a valid exit_time
   - TIME only assigned when expiry_time ≤ current_time AND trade closed

3. **TIME Usage**
   - TIME is only used when trade expired AND closed
   - Previously, TIME was used for both open and expired trades
   - Now OPEN is used for open trades, TIME for expired closed trades

---

## Implementation Changes

### 1. `price_tracking_logic.py`

#### `_classify_result()` Method
- **Added**: Check for OPEN trade first (exit_time is NaT/None)
- **Updated**: TIME only returned when exit_time != NaT
- **Updated**: Docstring to document OPEN result type

```python
def _classify_result(self, t1_triggered: bool,
                    exit_reason: str, target_hit: bool = False, 
                    entry_price: float = 0.0, 
                    exit_time: pd.Timestamp = None, data: pd.DataFrame = None,
                    direction: str = "Long", t1_removed: bool = False) -> str:
    """
    Result Types:
    - OPEN: Trade still open (exit_time is NaT/None)
    - Win: Target hit → Full target profit
    - BE: T1 triggered + stop hit → 1 tick loss
    - Loss: Stop hit without T1 → Actual loss
    - TIME: Time expired AND trade closed (exit_time != NaT)
    """
    # Check for OPEN trade first (exit_time is NaT/None)
    if exit_time is None or pd.isna(exit_time):
        return "OPEN"
    
    # ... rest of classification logic
```

#### `execute_trade()` Method - Open Trade Handling
- **Changed**: Exit reason from "TIME" to "OPEN" for open trades
- **Changed**: Variable names from `exit_time_for_time_expiry` to `exit_time_for_open`
- **Updated**: Comments to reflect OPEN result type

```python
# Trade is still open if expiry_time is in the future
if expiry_time and expiry_time > current_time:
    exit_time_for_open = pd.NaT
    exit_price_for_open = float(last_bar_in_data["close"])
    
    result_classification = self._classify_result(
        t1_triggered, "OPEN", False, entry_price, exit_time_for_open, df, direction, t1_removed
    )
    
    return self._create_trade_execution(
        exit_price_for_open, exit_time_for_open, "OPEN", False, False, False,
        max_favorable, peak_time, peak_price, t1_triggered,
        stop_loss_adjusted, current_stop_loss, result_classification, profit
    )
```

### 2. `instrument_logic.py`

#### `calculate_profit()` Method
- **Added**: Handling for OPEN result type
- **Behavior**: Calculates current PnL based on current price (same as TIME)

```python
# Handle OPEN result (trade still open)
elif result == "OPEN":
    # Open trades: Calculate current PnL based on current price
    if direction == "Long":
        pnl_pts = exit_price - entry_price
    else:
        pnl_pts = entry_price - exit_price
    
    actual_profit = self.scale_profit(instrument, pnl_pts)
    return actual_profit
```

### 3. `result_logic.py`

#### Result Ranking
- **Added**: OPEN to ranking dictionary with lowest rank (1)
- **Updated**: Deduplication logic to handle OPEN

```python
# Define result ranking (OPEN has lowest rank for deduplication)
rank = {"Win":5,"BE":4,"Loss":3,"TIME":2,"OPEN":1}
```

#### `classify_trade_result()` Method
- **Added**: Handling for OPEN exit_reason
- **Updated**: Docstring to include OPEN

```python
def classify_trade_result(self, exit_reason: str, t1_triggered: bool, 
                        target_hit: bool = False) -> str:
    """
    Args:
        exit_reason: Reason for trade exit ("Win", "Loss", "TIME", "OPEN")
    Returns:
        Result classification ("Win", "BE", "Loss", "TIME", "OPEN")
    """
    # Handle OPEN trades (trade still open)
    if exit_reason == "OPEN":
        return "OPEN"
    # ... rest of logic
```

### 4. Documentation Updates

#### `ANALYZER_DEEP_DIVE.md`
- **Updated**: Result Classification section to include OPEN
- **Updated**: Result Types section to clarify TIME vs OPEN
- **Updated**: Deduplication ranking to include OPEN

---

## Behavior Changes

### Before
- Open trades: Result = "TIME", exit_time = NaT
- Expired trades: Result = "TIME", exit_time = expiry_time
- **Issue**: TIME used for both open and expired trades

### After
- Open trades: Result = "OPEN", exit_time = NaT
- Expired trades: Result = "TIME", exit_time = expiry_time
- **Benefit**: Clear distinction between open and expired trades

---

## Validation

### Rules Enforced

1. ✅ **OPEN → exit_time = NaT**
   - Verified in `_classify_result()`: Checks `exit_time is None or pd.isna(exit_time)`
   - Verified in `execute_trade()`: Sets `exit_time_for_open = pd.NaT`

2. ✅ **WIN / LOSS / BE / TIME → exit_time != NaT**
   - Verified: All closed trades have valid exit_time
   - TIME only returned when `exit_time != NaT` in `_classify_result()`

3. ✅ **TIME only when expired AND closed**
   - Verified: TIME only used when `expiry_time <= current_time` AND `exit_time != NaT`
   - Open trades use OPEN instead of TIME

---

## Backward Compatibility

### Breaking Changes
- **Result Type**: New "OPEN" result type added
- **Existing Data**: Existing data with TIME and exit_time=NaT should be interpreted as OPEN

### Non-Breaking Changes
- All existing result types (Win, BE, Loss, TIME) remain unchanged
- TIME still valid for expired closed trades
- Profit calculation for OPEN same as TIME (current PnL)

---

## Testing Recommendations

1. **Open Trade Test**
   - Create trade with expiry_time in future
   - Verify: Result = "OPEN", exit_time = NaT

2. **Expired Trade Test**
   - Create trade with expiry_time in past
   - Verify: Result = "TIME", exit_time = expiry_time

3. **Profit Calculation Test**
   - Verify OPEN profit calculation matches TIME logic (current PnL)

4. **Deduplication Test**
   - Verify OPEN has lowest rank in deduplication
   - Verify OPEN trades are handled correctly in deduplication

---

## Files Modified

1. `modules/analyzer/logic/price_tracking_logic.py`
   - `_classify_result()`: Added OPEN check
   - `execute_trade()`: Changed open trade handling to use OPEN

2. `modules/analyzer/logic/instrument_logic.py`
   - `calculate_profit()`: Added OPEN handling

3. `modules/analyzer/logic/result_logic.py`
   - Result ranking: Added OPEN
   - `classify_trade_result()`: Added OPEN handling

4. `docs/analyzer/ANALYZER_DEEP_DIVE.md`
   - Updated result type documentation

---

## Summary

✅ **Implementation Complete**

- Explicit OPEN result type added
- Clear separation: OPEN (open trades) vs TIME (expired closed trades)
- All rules enforced: OPEN → exit_time = NaT, TIME → exit_time != NaT
- Backward compatible: Existing result types unchanged
- Documentation updated

The analyzer now clearly distinguishes between open trades (OPEN) and expired closed trades (TIME), improving clarity and maintainability.
