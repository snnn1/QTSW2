# Order Execution Blocking Analysis

## Problem

Orders are not being executed after hydration completes, even though entries are being detected.

## Root Cause

**Orders are being blocked by execution journal idempotency check**

### What's Happening

1. **Entries ARE being detected** ✅
   - `DRYRUN_INTENDED_ENTRY` events are logged
   - Breakouts are detected correctly
   - Entry prices and directions are computed

2. **Orders ARE being skipped** ❌
   - `EXECUTION_SKIPPED_DUPLICATE` events with reason `INTENT_ALREADY_SUBMITTED`
   - Execution journal thinks the order was already submitted

3. **Execution Journal Shows Previous Submissions**
   - Journal files exist for today (2026-01-27)
   - Entries marked as `EntrySubmitted: True`
   - But `Entry Order ID` is empty (suggests mock submission or failed submission)

### Why This Happens

**Intent ID Computation**:
The intent ID is computed from:
```
tradingDate|stream|instrument|session|slotTime|direction|entryPrice|stopPrice|targetPrice|beTrigger
```

**Example for NG1**:
- Trading Date: `2026-01-27`
- Stream: `NG1`
- Instrument: `NG` (canonical)
- Session: `S1`
- Slot Time: `07:30`
- Direction: `Long`
- Entry Price: `3.811`
- Stop/Target/BE: (computed from range)

If the **same entry parameters** are detected again (same price, same direction, same slot), the intent ID will be **identical**, and the execution journal will block it as a duplicate.

### The Issue

**Scenario 1: Same Entry Detected Multiple Times**
- Entry detected at 14:02 → Order submitted → Journal marked as submitted
- Entry detected again at 14:20 → **Same intent ID** → Skipped as duplicate

**Scenario 2: Entry Parameters Haven't Changed**
- Range locked → Breakout levels computed
- Same breakout price detected → Same intent ID → Blocked

**Scenario 3: Execution Journal Persists Across Runs**
- Previous engine run submitted order → Journal file created
- New engine run detects same entry → Journal file exists → Blocked

## Evidence

### From Logs
```
EXECUTION_SKIPPED_DUPLICATE
  Reason: INTENT_ALREADY_SUBMITTED
  Trading Date: 2026-01-27
  Stream: NG1
  Direction: Long
  Entry Price: 3.811
```

### From Execution Journal Files
```
2026-01-27_NG1_055af04b2bcd8c43.json:
  Entry Submitted: True
  Entry Filled: False
  Entry Order ID: (empty)
  Entry Submitted At: 2026-01-27T13:30:04
```

## Why Entry Order ID is Empty

The execution journal shows `EntrySubmitted: True` but `Entry Order ID` is empty. This suggests:

1. **Mock Submission**: Order was submitted via mock adapter (harness testing)
2. **Failed Submission**: Order submission failed but journal was still updated
3. **NT Context Not Set**: Real NT API not available, using mock

## Root Cause Identified ✅

**The robot is running in harness/testing mode, not inside NinjaTrader Strategy**

### Evidence
- `SIM_ACCOUNT_VERIFIED` events show: `"note": "SIM account [REDACTED] passed (MOCK - harness mode)"`
- This means `_ntContextSet = false` in `NinjaTraderSimAdapter`
- Orders are being submitted via **mock implementation** (not real NT API)

### What's Happening

1. **Execution Mode**: SIM ✅
2. **NT Context Set**: ❌ False (running in harness, not NT Strategy)
3. **Order Submission**: Mock implementation (returns success but doesn't place real orders)
4. **Execution Journal**: Marks orders as submitted (even though they're mock)
5. **Subsequent Entries**: Blocked as duplicates (journal thinks order was already submitted)

### The Flow

```
Entry Detected → RecordIntendedEntry()
  → Check Idempotency (ExecutionJournal.IsIntentSubmitted)
    → Journal file exists → EXECUTION_SKIPPED_DUPLICATE
      → Order never submitted (blocked before adapter call)
```

OR

```
Entry Detected → RecordIntendedEntry()
  → Check Idempotency → Pass
  → Check Risk Gates → Pass
  → SubmitEntryOrder() → Mock implementation
    → Returns success (mock order ID)
    → ExecutionJournal.RecordSubmission() → Marks as submitted
    → Next time: Blocked as duplicate
```

## Solutions

### Option 1: Run Inside NinjaTrader Strategy (Recommended)
The robot needs to run inside `RobotSimStrategy` in NinjaTrader to get real NT context:
- Strategy calls `SetNTContext()` with real Account and Instrument
- `_ntContextSet` becomes true
- Orders use real NT API (`SubmitEntryOrderReal()`)

**Check**: Look for `SIM_ACCOUNT_VERIFIED` events - should NOT say "MOCK - harness mode"

### Option 2: Clear Execution Journal (If Needed)
If orders were submitted in mock mode and shouldn't block real submissions:
- Delete or archive execution journal files for today
- Or modify journal entries to mark as not submitted

### Option 3: Fix Intent ID Collision
If the same entry is being detected multiple times legitimately:
- Intent ID should include timestamp or run_id to make it unique
- OR: Allow resubmission if order wasn't actually filled

### Option 4: Check Execution Mode
Verify execution mode is actually SIM (not DRYRUN):
- Check `EXECUTION_MODE_SET` events
- Should show `mode = SIM, adapter = NinjaTraderSimAdapter`

## Next Steps

1. **Check if NT context is set**: Look for `SIM_ACCOUNT_VERIFIED` events
2. **Check execution mode**: Verify it's SIM, not DRYRUN
3. **Check order submission**: Look for `ORDER_SUBMIT_ATTEMPT` or `ORDER_SUBMITTED` events
4. **Check journal entries**: See if orders were actually submitted or just mocked

## Related Code

- `modules/robot/core/StreamStateMachine.cs` - `RecordIntendedEntry()` (line 4186)
- `modules/robot/core/Execution/ExecutionJournal.cs` - `IsIntentSubmitted()` (line 77)
- `modules/robot/core/Execution/NinjaTraderSimAdapter.cs` - `SubmitEntryOrder()` (line 160)
