# Root Cause: StartBehavior=WaitUntilFlat

## The Real Issue

**NinjaTrader Log Evidence**:
```
StartBehavior=WaitUntilFlat
AccountPosition=MNG 03-26 1L
```

**Translation**: 
- NinjaTrader is configured to wait until the account is flat before transitioning to Realtime
- Account has an existing position: MNG 03-26 1L (1 Long position)
- NinjaTrader will NOT transition to Realtime until this position is closed
- Strategy stays in "Loading" state waiting for account to be flat
- Eventually disables if account doesn't go flat

## Why Our Code Looks Innocent

Our code is actually working correctly:
- ✅ `DATALOADED_INITIALIZATION_COMPLETE` logged (initialization completes)
- ✅ `NT_CONTEXT_WIRED` logged (adapter wired successfully)
- ✅ `_engineReady = true` set correctly
- ✅ All hardening fixes working

**But**: NinjaTrader never calls `OnStateChange(State.Realtime)` because:
- StartBehavior=WaitUntilFlat blocks the transition
- Account is not flat (has MNG position)
- NinjaTrader waits indefinitely for account to be flat

## Why This Affects MGC/MYM/M2K

If these strategies also have `StartBehavior=WaitUntilFlat` configured:
- They check for flat account
- If account has any positions (from other strategies or manual trades)
- They wait indefinitely
- Never reach Realtime state

## Solution

### Option 1: Change StartBehavior in NinjaTrader (Recommended)
**Action**: Change strategy setting from `WaitUntilFlat` to `Immediately`

**How**:
1. Right-click strategy in NinjaTrader
2. Properties → Strategy Settings
3. Change "Start behavior" from "Wait until flat" to "Immediately"
4. Apply and restart strategy

**Result**: 
- Strategy transitions to Realtime immediately
- Doesn't wait for account to be flat
- Can handle existing positions

### Option 2: Close Existing Position
**Action**: Close the MNG 03-26 1L position manually

**How**:
1. Close position in NinjaTrader
2. Restart strategies
3. They should transition to Realtime once account is flat

**Result**: 
- Account becomes flat
- Strategies transition to Realtime
- But this is a workaround - Option 1 is better

## Why OnEachTick Wasn't the Issue

The `Calculate = OnEachTick` change may have been a red herring:
- The real issue is `StartBehavior=WaitUntilFlat`
- Even with `OnBarClose`, strategies would still be stuck if account isn't flat
- However, reverting to `OnBarClose` is still good (less blocking potential)

## Verification

After changing StartBehavior to "Immediately":
1. ✅ Strategies should reach Realtime immediately
2. ✅ `REALTIME_STATE_REACHED` should appear in logs
3. ✅ Strategies should start processing bars/ticks
4. ✅ No more "Loading" state

## Code Status

**Our Code**: ✅ Working correctly - no changes needed
**NinjaTrader Configuration**: ❌ Needs change - StartBehavior setting

## Conclusion

**Root Cause**: `StartBehavior=WaitUntilFlat` + existing account position
**Fix**: Change StartBehavior to "Immediately" in NinjaTrader strategy settings
**Code Changes**: None needed - our code is fine

This explains everything perfectly:
- Why initialization completes ✅
- Why Realtime never reached ❌
- Why strategies show "Loading" ⏳
- Why they eventually disable 🔴
