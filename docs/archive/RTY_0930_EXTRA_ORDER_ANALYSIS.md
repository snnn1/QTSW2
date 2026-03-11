# RTY 09:30 Extra Order Analysis

**Date**: February 4, 2026
**Time**: 09:30 CT (15:30 UTC)
**Stream**: RTY2

## User Report

"RTY just took a long order for no reason when 09:30 hit. 2 trades were put in the range correctly but there was also another order taken. Why was it taken?"

## Timeline Analysis

### 15:30:01 UTC (09:30 CT) - Range Locked

**Events**:
1. `STREAM_STATE_TRANSITION` → `RANGE_LOCKED`
2. `STOP_BRACKETS_SUBMIT_ENTERED` - Function entered
3. `INTENT_REGISTERED` (Long) - Intent ID: `8f29d02aec78ec32`
   - Direction: Long
   - Entry Price: 2674.7
   - Stream: RTY2
4. `INTENT_REGISTERED` (Short) - Intent ID: `6e452d1c83d17792`
   - Direction: Short
   - Entry Price: 2652.4
   - Stream: RTY2
5. `STOP_BRACKETS_SUBMITTED` - Both stop orders submitted
   - Long broker order ID: `160672f638874496ac5013eb07cb048d`
   - Short broker order ID: `155c1e19fc8d4822b571695ff3dff3cb`
   - OCO Group: `QTSW2:OCO_ENTRY:2026-02-04:RTY2:09:30:3122900d6ed048469bd2cf738fa40d76`

**Status**: ✅ **2 stop brackets submitted correctly** (Long and Short)

### 15:30:03 UTC - Protective Orders Submitted

**Events**:
1. `ORDER_CREATED_STOPMARKET` - Protective stop order
   - Order name: `6e452d1c83d17792_STOP`
   - Stop price: 2674.5
   - Quantity: 2
   - Action: BuyToCover (Short position)
2. `ORDER_SUBMIT_SUCCESS` - Protective stop submitted
   - Order type: `PROTECTIVE_STOP`
   - Direction: Short
   - Quantity: 2
3. `ORDER_CREATED_LIMIT` - Target order
   - Order name: `6e452d1c83d17792_TARGET`
   - Target price: 2642.4
   - Quantity: 2
4. `ORDER_SUBMIT_SUCCESS` - Target submitted
   - Order type: `TARGET`
   - Direction: Short
   - Quantity: 2
5. `PROTECTIVE_ORDERS_SUBMITTED` - Both protective orders submitted
   - Intent ID: `6e452d1c83d17792` (Short)
   - Fill quantity: 2
   - Total filled quantity: 2

**Critical Finding**: Protective orders were submitted for Short intent `6e452d1c83d17792` with `fill_quantity: 2`. This means **an entry fill occurred** for the Short stop bracket!

### 15:31:01 UTC - Breakout Entry Detected

**Events**:
1. `DRYRUN_INTENDED_ENTRY` - Breakout entry detected
   - Direction: Short
   - Entry Price: 2652.4
   - Trigger Reason: BREAKOUT
   - Entry Time: 15:30:00 UTC (09:30 CT)

## Root Cause Analysis

### The Problem

**Both `CheckImmediateEntryAtLock()` AND `SubmitStopEntryBracketsAtLock()` can submit orders**, and there's a **race condition** between them:

1. **`CheckImmediateEntryAtLock()`** is called FIRST (line 4517)
   - Checks if price is already at/through breakout level
   - If yes, calls `RecordIntendedEntry()` which:
     - Sets `_entryDetected = true`
     - Submits an **immediate entry order** (Limit order at breakout level)
     - Registers intent

2. **`SubmitStopEntryBracketsAtLock()`** is called SECOND (line 4523)
   - Checks `!_entryDetected` (line 4521)
   - If `_entryDetected = false`, submits **stop brackets** (Long + Short stop orders)

### The Bug

**Race Condition**: If `CheckImmediateEntryAtLock()` detects an immediate entry but the order hasn't filled yet, `_entryDetected` may not be set before `SubmitStopEntryBracketsAtLock()` checks it, OR:

**More Likely**: `CheckImmediateEntryAtLock()` may NOT detect immediate entry (price not quite at breakout), so `_entryDetected` stays `false`, and stop brackets are submitted. Then, if price moves to breakout level immediately after, BOTH orders can exist:
- Immediate entry order (from `CheckImmediateEntryAtLock()`)
- Stop bracket orders (from `SubmitStopEntryBracketsAtLock()`)

### Evidence

1. **Stop brackets were submitted** ✅ (Long and Short)
2. **Protective orders were submitted** ✅ (for Short intent)
3. **This means Short entry filled** ✅
4. **But no immediate entry detection logged** ⚠️

**Conclusion**: The Short stop bracket filled immediately (price was at/through breakout level), triggering protective orders. But the user reports a "long order for no reason" - this suggests a Long order was also submitted somehow.

## Possible Scenarios

### Scenario 1: Immediate Entry + Stop Brackets Both Submitted
- `CheckImmediateEntryAtLock()` detects Long immediate entry → Submits Long limit order
- `SubmitStopEntryBracketsAtLock()` also runs → Submits Long + Short stop brackets
- Result: **3 orders** (Long immediate, Long stop, Short stop)

### Scenario 2: Stop Brackets Filled Immediately
- Stop brackets submitted (Long + Short)
- Short stop bracket fills immediately (price at breakout)
- Long stop bracket also fills (if price moved through both breakouts)
- Result: **Both stop brackets filled** (but only one should fill due to OCO)

### Scenario 3: OCO Group Failure
- Stop brackets submitted with OCO group
- OCO group fails to link orders properly
- Both orders fill independently
- Result: **Both orders fill** (OCO didn't work)

## Next Steps to Investigate

1. **Check for immediate entry order submission** - Look for `RecordIntendedEntry` or `SubmitEntryOrder` events
2. **Check OCO group linking** - Verify OCO group was set correctly on both orders
3. **Check order fills** - Find all fill events for RTY2 around 15:30
4. **Check if Long order filled** - Verify if Long stop bracket also filled

## Recommendation

**Check execution logs** for:
- Entry fill events for Long intent `8f29d02aec78ec32`
- Entry fill events for Short intent `6e452d1c83d17792`
- Any immediate entry orders submitted
- OCO group validation
