# Why Orders Aren't Executing After Hydration

## Summary

Orders are being blocked because the robot is running in **harness/testing mode** (not inside NinjaTrader Strategy), so orders are submitted via mock implementation. The execution journal marks them as submitted, blocking subsequent attempts.

## Root Cause

**NinjaTrader context is not set** - robot is running outside NinjaTrader Strategy

### Evidence

1. **SIM Account Verification**: Shows `"MOCK - harness mode"`
   ```
   SIM_ACCOUNT_VERIFIED: "note": "SIM account [REDACTED] passed (MOCK - harness mode)"
   ```

2. **Execution Mode**: SIM ✅ (correct)

3. **NT Context Set**: ❌ False (`_ntContextSet = false`)

4. **Order Submission**: Using mock implementation (not real NT API)

5. **Execution Journal**: Marks orders as submitted (even though mock)

6. **Subsequent Entries**: Blocked as duplicates

## What's Happening

### Flow 1: Entry Detected → Blocked by Journal
```
Entry Detected (DRYRUN_INTENDED_ENTRY)
  → RecordIntendedEntry()
    → Build Intent
    → Check Idempotency (ExecutionJournal.IsIntentSubmitted)
      → Journal file exists → EXECUTION_SKIPPED_DUPLICATE
        → Order never submitted
```

### Flow 2: Entry Detected → Mock Submission → Journal Blocks Next Time
```
Entry Detected
  → RecordIntendedEntry()
    → Check Idempotency → Pass (first time)
    → Check Risk Gates → Pass
    → SubmitEntryOrder() → Mock implementation
      → Returns success (mock order ID: "NT_...")
      → ExecutionJournal.RecordSubmission() → Marks as submitted
      → Journal file created: 2026-01-27_NG1_<intent_id>.json
        → EntrySubmitted: True
        → Entry Order ID: (empty - mock)
    → Next time same entry detected → Blocked as duplicate
```

## The Problem

**Execution Journal Persistence**:
- Journal files persist to disk: `data/execution_journals/2026-01-27_NG1_<intent_id>.json`
- Intent ID is computed from: `tradingDate|stream|instrument|session|slotTime|direction|entryPrice|stopPrice|targetPrice|beTrigger`
- If same entry parameters detected again → Same intent ID → Blocked as duplicate

**Mock vs Real Orders**:
- Mock orders return success but don't place real orders
- Execution journal doesn't distinguish mock from real
- Journal marks mock orders as submitted → Blocks real orders later

## Solution

### Option 1: Run Inside NinjaTrader Strategy (Required for Real Orders)

The robot **must** run inside `RobotSimStrategy` in NinjaTrader to get real NT context:

1. **Copy Strategy**: Copy `RobotSimStrategy.cs` to NinjaTrader Strategy project
2. **Set NT Context**: Strategy calls `adapter.SetNTContext(Account, Instrument)`
3. **Real Orders**: `_ntContextSet = true` → Orders use real NT API

**Check**: Look for `SIM_ACCOUNT_VERIFIED` - should NOT say "MOCK - harness mode"

### Option 2: Clear Execution Journal (For Testing)

If testing in harness mode and want to resubmit orders:
- Delete execution journal files: `data/execution_journals/2026-01-27_*.json`
- Or modify journal entries to mark as not submitted

**Warning**: This bypasses idempotency protection - only for testing!

### Option 3: Fix Idempotency Check (If Needed)

If same entry legitimately detected multiple times:
- Include timestamp or run_id in intent ID computation
- OR: Allow resubmission if order wasn't actually filled (check `EntryFilled`)

## Current State

### From Logs
- **Entries Detected**: ✅ Yes (DRYRUN_INTENDED_ENTRY events)
- **Orders Submitted**: ❌ No (EXECUTION_SKIPPED_DUPLICATE)
- **NT Context**: ❌ Not set (MOCK - harness mode)
- **Execution Mode**: ✅ SIM

### From Execution Journal
- **Journal Files**: Exist for today (2026-01-27)
- **Entry Submitted**: True
- **Entry Order ID**: Empty (mock submission)
- **Entry Filled**: False

## Next Steps

1. **Verify NT Context**: Check if robot is running inside NinjaTrader Strategy
2. **If Harness Mode**: Either:
   - Switch to running inside NT Strategy (for real orders)
   - OR clear execution journal (for testing only)
3. **Check Order Submission**: Look for `ORDER_SUBMIT_ATTEMPT` events to see if orders reach adapter

## Related Files

- `modules/robot/ninjatrader/RobotSimStrategy.cs` - NT Strategy host (calls SetNTContext)
- `modules/robot/core/Execution/NinjaTraderSimAdapter.cs` - Adapter (checks _ntContextSet)
- `modules/robot/core/Execution/ExecutionJournal.cs` - Journal (idempotency check)
- `modules/robot/core/StreamStateMachine.cs` - RecordIntendedEntry() (order submission flow)
