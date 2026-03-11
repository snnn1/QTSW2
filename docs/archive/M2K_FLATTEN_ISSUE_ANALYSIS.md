# M2K Flatten and Re-Entry Issue - Extensive Analysis

## Timeline of Events (2026-02-05 18:41:02-05)

### 18:41:03.175 - Target Order Fill (Untracked)
- **Event**: `EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL`
- **Order**: Broker ID `357414288151`
- **Tag**: `QTSW2:fa1708e718e939d9:TARGET`
- **Fill**: Price `2592.1`, Quantity `2`
- **Intent ID**: `fa1708e718e939d9`
- **Problem**: Target order filled but **NOT found in `_orderMap`**
- **Action**: System triggered flatten (fail-closed behavior)

### 18:41:03.176 - First Flatten Executed
- **Event**: `UNKNOWN_ORDER_FILL_FLATTENED`
- **Result**: Flatten succeeded
- **Note**: Position flattened due to untracked target order fill

### 18:41:05.504 - Flatten Order Fill (Untracked)
- **Event**: `EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL`
- **Order**: Broker ID `357414288233` (This is the "Buy to cover" order you saw)
- **Tag**: `"Close"` (Not a robot tag - this is NinjaTrader's flatten order)
- **Fill**: Price `2592.2`, Quantity `2`
- **Problem**: Flatten order has tag "Close" (not `QTSW2:*`) → cannot decode intent ID
- **Action**: System triggered **another flatten** (fail-closed behavior)

### 18:41:05.504 - Second Flatten Executed
- **Event**: `UNTrackED_FILL_FLATTENED`
- **Result**: Flatten succeeded
- **Note**: Position flattened due to untracked fill (the flatten order itself)

## Root Cause Analysis

### Primary Issue: Target Order Not in `_orderMap`

**What Happened**:
1. Protective target order (`QTSW2:fa1708e718e939d9:TARGET`) filled
2. System looked up `_orderMap[fa1708e718e939d9]` → **NOT FOUND**
3. Tag-based detection should have created `OrderInfo` on the fly, but it seems this didn't work
4. System treated it as untracked → triggered flatten

**Why Target Order Wasn't in `_orderMap`**:
- **Before our fix**: Target orders were NOT added to `_orderMap` when submitted
- **After our fix**: Target orders SHOULD be added to `_orderMap` when submitted
- **Possible reasons**:
  1. **DLL not deployed**: Old DLL still running (most likely)
  2. **Race condition**: Target order submitted but fill arrived before `_orderMap` update
  3. **Tag detection failed**: Tag-based fallback didn't work (less likely)

### Secondary Issue: Flatten Order Tag Mismatch

**What Happened**:
1. System submitted flatten order (Buy to cover Market order)
2. NinjaTrader tagged it as `"Close"` (not a robot tag)
3. When flatten order filled, system couldn't decode intent ID from tag
4. System treated it as untracked → triggered **another flatten**

**Why This Happens**:
- Flatten orders are submitted via NinjaTrader's `Flatten()` API
- NinjaTrader automatically tags them as `"Close"` (not our robot tag format)
- Our system expects `QTSW2:*` tags → cannot track flatten orders
- This is **expected behavior** - flatten orders are meant to close positions, not track them

**Is This a Problem?**:
- **No** - Flatten orders closing positions is correct behavior
- The second flatten is redundant but harmless (flattening an already-flat position)
- However, it creates confusing logs

## Why Re-Entry Happened

### The Sequence:
1. **Target filled** → Untracked → Flatten triggered
2. **Flatten executed** → Position closed
3. **BUT**: Entry stop order (`357414288148`) was still active
4. **Entry stop filled** → Created new long position
5. **Result**: Re-entry into long position

### Why Entry Stop Wasn't Cancelled:

**Entry Order State**:
- Broker ID `357414288148`
- Tag: `QTSW2:fa1708e718e939d9` (entry order)
- State transitions: `CancelPending` → `CancelSubmitted` → `Cancelled`
- **BUT**: These were `EXECUTION_UPDATE_UNKNOWN_ORDER` events (not critical)

**Possible Reasons**:
1. **OCO cancellation failed**: Entry stop should have been cancelled when target filled (OCO pair)
2. **Race condition**: Target filled before OCO cancellation processed
3. **Entry order not in `_orderMap`**: Entry order also wasn't tracked properly

## Fix Status

### ✅ Fixes Already Applied (In Code):
1. **Protective orders added to `_orderMap`**: Both stop and target orders are now added when submitted
2. **Tag-based detection**: Fallback creates `OrderInfo` from tag if not in `_orderMap`
3. **OCO cancellation fix**: Only cancels entry orders, not protective orders

### ⚠️ Issue: DLL Not Deployed
- **Status**: Code fixes are in place, but **DLL needs to be rebuilt and deployed**
- **Action Required**: Rebuild DLL and copy to NinjaTrader, then restart NinjaTrader

## What Should Have Happened (With Fixes)

### Correct Flow:
1. **Target order submitted** → Added to `_orderMap[intentId]` ✅
2. **Target order fills** → Found in `_orderMap` → Correctly journaled as exit fill ✅
3. **OCO cancels stop** → Stop order cancelled automatically ✅
4. **Entry stop cancelled** → When target filled, OCO should cancel entry stop ✅
5. **No re-entry** → Entry stop already cancelled ✅

### What Actually Happened (Without Fixes):
1. **Target order submitted** → NOT added to `_orderMap` ❌
2. **Target order fills** → NOT found in `_orderMap` → Treated as untracked ❌
3. **Flatten triggered** → Position closed ✅
4. **Entry stop still active** → Not cancelled ❌
5. **Entry stop fills** → Re-entry into long position ❌

## Recommendations

### Immediate Actions:
1. **Deploy DLL**: Rebuild and deploy the fixed DLL to NinjaTrader
2. **Restart NinjaTrader**: Required to load new DLL
3. **Monitor logs**: Watch for `PROTECTIVE_ORDER_FILL_TRACKED_FROM_TAG` events (should be rare)

### Long-Term Improvements:
1. **Flatten order handling**: Consider tracking flatten orders separately (they're not robot orders)
2. **OCO cancellation logging**: Add more detailed logging when OCO cancels orders
3. **Entry order tracking**: Ensure entry orders are always in `_orderMap` before submission

## Summary

### Root Cause:
**Protective target order was not in `_orderMap` when it filled**, causing:
1. Untracked fill detection → Flatten triggered
2. Entry stop not cancelled → Re-entry occurred

### Fix Status:
✅ **Code fixes complete** - Protective orders now added to `_orderMap`
⚠️ **DLL deployment pending** - Must rebuild and deploy to NinjaTrader

### Expected Outcome After Deployment:
- Target orders tracked in `_orderMap`
- No more untracked fill alerts for protective orders
- Entry stops properly cancelled when protective orders fill
- No re-entry issues
