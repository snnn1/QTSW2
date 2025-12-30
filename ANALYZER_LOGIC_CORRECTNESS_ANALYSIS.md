# Analyzer Logic Correctness Analysis

## Overview

This document provides a detailed analysis of complex logic paths in the analyzer, identifying potential correctness issues and edge cases.

## 1. MFE Calculation Edge Cases

### Current Implementation

**Location**: `modules/analyzer/logic/price_tracking_logic.py:173-190`

**Issue**: When data doesn't extend to MFE end time, the system uses available data but logging may not be visible.

**Analysis**:
- Uses logging module with different levels (debug/info/warning)
- Falls back gracefully to available data
- May not be visible if logging not configured

**Recommendation**: 
- Ensure logging is properly configured
- Consider adding a summary report of MFE data gaps at end of processing

### MFE Tracking After Stop Loss Hit

**Location**: `modules/analyzer/logic/price_tracking_logic.py:222-250`

**Issue**: MFE tracking stops when original stop loss is hit, but this may not be the intended behavior.

**Current Logic**:
```python
if original_stop_hit and not mfe_stopped:
    mfe_stopped = True
```

**Analysis**: This is correct behavior - MFE should stop tracking when original stop is hit, as per strategy definition.

## 2. T1 Trigger Detection

### Per-Bar vs Intra-Bar Detection

**Location**: `modules/analyzer/logic/price_tracking_logic.py:288-297`

**Current Implementation**:
```python
current_favorable = self._calculate_favorable_movement(high, low, entry_price, direction)
if not t1_triggered and current_favorable >= t1_threshold:
    t1_triggered = True
```

**Issue**: T1 trigger is detected per-bar, not intra-bar. This means:
- If T1 threshold is 6.5 points and price reaches 6.5 points mid-bar, trigger happens at bar close
- This is a limitation of bar-based data (no tick data available)

**Impact**: 
- T1 may trigger slightly late (at bar close instead of exact threshold)
- Stop loss adjustment happens at bar close, not exact trigger point
- This is acceptable given bar-based data limitation

**Recommendation**: Document this limitation clearly.

### T1 Trigger in Same Bar as Stop Hit

**Location**: `modules/analyzer/logic/price_tracking_logic.py:952-955`

**Issue**: Special handling when T1 triggers in same bar as stop hit.

**Current Logic**:
```python
if t1_triggered and not t1_triggered_previous_bar:
    # T1 just triggered in this bar - don't check stop in same bar
    return None
```

**Analysis**: This prevents stop from being hit in the same bar where T1 triggers, which is correct behavior.

## 3. Entry Detection Logic

### Hardcoded Debug Date

**Location**: `modules/analyzer/logic/entry_logic.py:95-118`

**Issue**: Hardcoded debug output for specific date (2025-12-29).

**Recommendation**: Remove or make configurable via debug parameter.

### Dual Immediate Entry

**Location**: `modules/analyzer/logic/entry_logic.py:137-158`

**Current Logic**: Chooses closer breakout when both conditions met.

**Analysis**: Logic is correct - both conditions are checked before calling this method, so it's safe to choose closer breakout.

### Market Close Filtering

**Location**: `modules/analyzer/logic/entry_logic.py:85-91`

**Issue**: Breakouts after 16:00 are filtered out.

**Analysis**: This is correct behavior per strategy definition - entries should only occur during market hours.

## 4. Intra-Bar Execution Logic

### Complexity Analysis

**Location**: `modules/analyzer/logic/price_tracking_logic.py:907-990`

**Current Implementation**: Uses `_analyze_actual_price_movement()` to determine execution order.

**Logic Flow**:
1. Check if target is possible → always analyze
2. If only stop possible → check T1 trigger state
3. Use close price proximity when both possible

**Potential Issues**:
- Close price proximity may not accurately reflect which level hit first
- No consideration of bar open price in some cases

**Recommendation**: 
- Add unit tests for all execution scenarios
- Document assumptions about execution order

### Target Hits First Logic

**Location**: `modules/analyzer/logic/price_tracking_logic.py:1071-1118` (Long), `1120-1167` (Short)

**Current Logic**:
- If open >= target (Long) or open <= target (Short) → Target hits immediately
- If open <= stop (Long) or open >= stop (Short) → Stop hits immediately
- If both possible → Use close price proximity

**Analysis**: Logic is sound but relies on close price proximity assumption.

## 5. Time Expiry Logic

### Complexity Analysis

**Location**: `modules/analyzer/logic/price_tracking_logic.py:463-649`

**Multiple Code Paths**:
1. Time expiry during bar loop (lines 463-515)
2. Trade still open after loop (lines 517-580)
3. Trade expired after loop (lines 582-649)

**Potential Issues**:
- Multiple conditions for determining expiry time
- Complex logic for finding exit price at expiry
- May be inconsistent between paths

**Recommendation**: Refactor into separate methods for clarity.

### Expiry Time Calculation

**Location**: `modules/analyzer/logic/price_tracking_logic.py:715-761`

**Current Logic**: Calculates expiry as next day same slot + 1 minute.

**Issue**: Friday trades expire Monday (3 days ahead).

**Analysis**: Logic is correct per strategy definition.

## 6. Peak Calculation Edge Cases

### MFE Peak vs Execution Peak

**Location**: `modules/analyzer/logic/price_tracking_logic.py:371-376, 427-432, 497-502, 622-627`

**Issue**: Falls back to execution peak if MFE peak is 0.

**Current Logic**:
```python
if mfe_peak == 0.0 and max_favorable_execution > 0.0:
    max_favorable = max_favorable_execution
```

**Analysis**: This is correct - if MFE stopped early due to stop loss, use execution peak.

## 7. Profit Calculation

### Micro-Futures Scaling

**Location**: `modules/analyzer/logic/price_tracking_logic.py:1745-1786`

**Issue**: Scaling logic duplicated in multiple places.

**Current Logic**:
- Divides by 10 for micro-futures
- Multiplies losses by 10 for display (MES)

**Analysis**: Logic is correct but should be centralized.

## Recommendations

### High Priority

1. **Remove Hardcoded Debug Date**: Remove or make configurable
2. **Document T1 Trigger Limitation**: Clearly document per-bar vs intra-bar detection
3. **Add Unit Tests**: Comprehensive tests for intra-bar execution scenarios
4. **Refactor Time Expiry Logic**: Break into separate methods

### Medium Priority

1. **Centralize Profit Calculation**: Move micro-futures scaling to InstrumentManager
2. **Add Execution Order Tests**: Test all execution order scenarios
3. **Document Assumptions**: Document close price proximity assumption

### Low Priority

1. **Code Cleanup**: Simplify complex conditionals
2. **Performance**: Optimize repeated calculations
