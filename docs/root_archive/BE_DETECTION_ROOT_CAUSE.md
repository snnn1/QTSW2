# Break-Even Detection Root Cause Analysis

## Problem Statement

**When price hits break-even point, nothing happens** - stop order is not modified to break-even.

## Diagnosis Results

### Findings

1. **BE Trigger Detection IS Working** ✅
   - 2028 `BE_TRIGGER_RETRY_NEEDED` events found
   - BE trigger price is being reached
   - `CheckBreakEvenTriggersTickBased()` is being called

2. **Stop Orders NOT Submitted** ❌
   - 0 protective stop orders submitted
   - 271 entry fills occurred
   - All fills have `intent_id = "UNKNOWN"`

3. **Root Cause Chain**
   ```
   Entry Fill → intent_id = "UNKNOWN" 
   → HandleEntryFill() NOT called 
   → Protective stop orders NOT submitted 
   → BE trigger detected but no stop order to modify
   → "Stop order not found for BE modification" error
   ```

## The Issue

### Primary Issue: UNKNOWN Intent ID

**271 entry fills have `intent_id = "UNKNOWN"`**

When fills have UNKNOWN intent_id:
- `HandleExecutionUpdateReal()` can't resolve intent context
- `HandleEntryFill()` is never called
- Protective stop orders are never submitted
- BE detection runs but has no stop order to modify

### Secondary Issue: Intent Resolution Failure

From earlier analysis:
- Order tags may not be set correctly
- Intent resolution fails → `intent_id = "UNKNOWN"`
- Fills are processed but can't be linked to intents

## Why BE Detection Appears to Work

BE detection IS working for intent `317ee2f6b100915a`:
- Intent was registered with BE trigger: 25309.500
- BE trigger price reached (2028 retry events)
- But stop order doesn't exist (never submitted)

## The Fix Needed

### Fix #1: Intent Resolution (Already Fixed)
- ✅ Fixed: Untracked fills now flatten position (fail-closed)
- ⚠️ Still need: Fix intent resolution so fills have valid intent_id

### Fix #2: Protective Order Submission
Even if intent_id is resolved, we need to ensure:
1. `HandleEntryFill()` is called after entry fill
2. Protective stop orders are submitted successfully
3. Stop orders are tracked correctly

## Current Status

- ✅ BE trigger calculation: Working
- ✅ BE trigger detection: Working (tick-based)
- ✅ Intent registration: Working (BE trigger set)
- ❌ Intent resolution: Failing (UNKNOWN intent_id)
- ❌ Protective order submission: Not happening (due to UNKNOWN intent_id)
- ❌ Stop order modification: Failing (no stop order exists)

## Next Steps

1. **Fix intent resolution** so fills have valid intent_id
2. **Verify HandleEntryFill is called** after intent resolution fix
3. **Verify protective orders are submitted** after HandleEntryFill
4. **Test BE detection** with valid intent_id and submitted stop orders

## Expected Behavior After Fix

1. Entry fill → intent_id resolved → HandleEntryFill() called
2. HandleEntryFill() → Protective stop order submitted
3. BE trigger reached → Stop order found → Modified to BE stop price
4. Position protected at break-even ✅
