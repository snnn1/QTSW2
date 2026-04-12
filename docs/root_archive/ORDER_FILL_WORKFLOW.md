# What Happens When One Entry Order Fills

## Overview

When a range locks, the robot places **two entry stop orders** in an OCO (One-Cancels-Other) group:
- **Long entry stop** (above range high)
- **Short entry stop** (below range low)

These orders are OCO within the stream only, meaning only one can fill.

## Expected Workflow When One Order Fills

### 1. Order Fill Detection
- NinjaTrader's `OnExecutionUpdate` event fires when an entry order fills
- The robot records the fill in `_orderMap` and `ExecutionJournal`
- Fill event logged: `EXECUTION_FILLED` with fill price and quantity

### 2. OCO Cancellation (Automatic)
- **NinjaTrader automatically cancels the opposite entry order** when one fills
- This is handled by NinjaTrader's OCO group mechanism
- The robot does NOT need to manually cancel - NT handles it
- The opposite order will receive an `OrderUpdate` event with `OrderState.Cancelled`

### 3. Entry Fill Processing (`HandleEntryFill`)
When an entry order fills, the robot immediately:

#### a) Validates Intent Data
- Checks that intent has: Direction, StopPrice, TargetPrice
- If missing, logs error and returns (no protective orders placed)

#### b) Records Entry Fill Time
- Stores `EntryFillTime` in order info for watchdog tracking
- Resets protective order acknowledgment flags

#### c) Validates Exit Orders
- Checks with coordinator: `CanSubmitExit(intentId, fillQuantity)`
- Ensures no conflicts or violations

#### d) Submits Protective Stop Order
- Calls `SubmitProtectiveStop()` with:
  - Intent ID
  - Instrument
  - Direction (Long/Short)
  - Stop price (from intent)
  - **Fill quantity** (not original order quantity)
- Retries up to 3 times with 100ms delay between attempts

#### e) Submits Target Order
- Calls `SubmitTargetOrder()` with:
  - Intent ID
  - Instrument
  - Direction
  - Target price (from intent)
  - **Fill quantity** (not original order quantity)
- Retries up to 3 times with 100ms delay between attempts

#### f) Protective Orders Are OCO
- Stop and Target orders are placed as OCO pair
- Only one can fill (stop loss OR target)
- When one fills, the other automatically cancels

### 4. Success Path
If both protective orders submit successfully:
- Logs: `PROTECTIVE_ORDERS_SUBMITTED`
- Logs: `PROTECTIVES_PLACED` (proof log with encoded tags)
- Checks for unprotected positions
- Position is now protected with stop loss and target

### 5. Failure Path
If either protective order fails after retries:
- Logs: `PROTECTIVE_ORDERS_FAILED_FLATTENED`
- **Immediately flattens the position** (emergency safety measure)
- Stands down the stream (prevents further trading)
- Notifies coordinator of protective failure
- Raises high-priority alert notification
- Persists incident record

## Key Points

### OCO Behavior
- **Entry orders**: OCO within stream (Long vs Short)
- **Protective orders**: OCO pair (Stop vs Target)
- NinjaTrader handles OCO cancellation automatically
- Robot does NOT manually cancel OCO orders

### Quantity Handling
- Protective orders are sized to **filled quantity**, not original order quantity
- Supports partial fills correctly
- Each fill gets its own protective orders

### Safety Mechanisms
1. **Intent validation**: Ensures all required data before placing protective orders
2. **Coordinator validation**: Prevents conflicts and violations
3. **Retry logic**: 3 attempts with delays for transient failures
4. **Emergency flatten**: If protective orders fail, position is immediately closed
5. **Stream stand-down**: Prevents further trading if protection fails

### Logging Events
- `EXECUTION_FILLED`: Entry order filled
- `PROTECTIVE_ORDERS_SUBMITTED`: Both protective orders placed successfully
- `PROTECTIVES_PLACED`: Proof log with encoded order tags
- `PROTECTIVE_ORDERS_FAILED_FLATTENED`: Emergency flatten due to failure
- `ORDER_CANCELLED`: Opposite entry order cancelled (from NT OCO)

## Example Sequence

```
08:00:00 - Range locks for RTY1
08:00:00 - Long entry stop @ 2675.9 submitted (OCO group: RTY1_08:00)
08:00:01 - Short entry stop @ 2660.9 submitted (OCO group: RTY1_08:00)
08:05:23 - Long entry stop FILLED @ 2676.0
08:05:23 - EXECUTION_FILLED event logged
08:05:23 - Short entry stop CANCELLED (OCO automatic)
08:05:23 - HandleEntryFill() called
08:05:23 - Protective stop @ 2661.0 submitted (quantity: 1)
08:05:23 - Target order @ 2686.0 submitted (quantity: 1)
08:05:23 - PROTECTIVE_ORDERS_SUBMITTED logged
08:05:23 - PROTECTIVES_PLACED logged
08:05:23 - Position protected: Long 1 @ 2676.0, Stop @ 2661.0, Target @ 2686.0
```

## Code References

- Entry fill handling: `NinjaTraderSimAdapter.NT.cs` lines 1206-1282
- HandleEntryFill: `NinjaTraderSimAdapter.cs` lines 330-516
- OCO group creation: `SubmitStopEntryOrderReal()` uses `ocoGroup` parameter
- Protective order submission: `SubmitProtectiveStop()` and `SubmitTargetOrder()`
