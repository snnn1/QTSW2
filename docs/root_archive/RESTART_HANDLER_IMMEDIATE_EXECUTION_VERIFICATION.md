# Restart Handler Immediate Execution Verification

## Question
After `RestoreRangeLockedFromHydrationLog()` completes and calls `Transition(..., RANGE_LOCKED, ...)`, does `HandleRangeLockedState()` run immediately on the next tick/bar?

## Analysis

### Flow After Restoration

1. **Restoration in Constructor** (`RestoreRangeLockedFromHydrationLog`):
   ```csharp
   Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED_RESTORED", rangeLogData);
   ```
   - Sets `State = StreamState.RANGE_LOCKED`
   - Sets `_stateEntryTimeUtc = utcNow`
   - Saves journal
   - **Does NOT call `HandleRangeLockedState()` directly**

2. **Transition Method** (`Transition`):
   - Updates `State` property
   - Updates `_stateEntryTimeUtc`
   - Saves journal state
   - **Does NOT call state handlers**

3. **OnTick() Method** (called externally by RobotEngine/NinjaTrader):
   ```csharp
   public void OnTick(DateTimeOffset utcNow)
   {
       // Early return if committed
       if (_journal.Committed)
       {
           State = StreamState.DONE;
           return;
       }
       
       // Switch on current state
       switch (State)
       {
           case StreamState.RANGE_LOCKED:
               HandleRangeLockedState(utcNow);  // ← Called here
               break;
           // ...
       }
   }
   ```

4. **OnBar() Method**:
   - Only buffers bars
   - **Does NOT call state handlers**
   - State handlers are called via `OnTick()`, not `OnBar()`

### Verification

✅ **State is set correctly**: After restoration, `State == StreamState.RANGE_LOCKED`

✅ **No guards skip processing**: The only guard in `OnTick()` is:
   - `if (_journal.Committed) return;` - but restoration doesn't commit the journal

✅ **Handler will be called**: Next call to `OnTick()` will hit the switch statement and call `HandleRangeLockedState(utcNow)`

### Timing

**Important**: `HandleRangeLockedState()` is **NOT** called immediately during restoration. It will be called on the **next external call to `OnTick()`**.

This is the expected behavior:
- Restoration happens in constructor (synchronous)
- State handlers run on next tick (asynchronous, driven by market data)

### Expected Log Sequence

After restart with range locked:

1. `RANGE_LOCKED_RESTORED_FROM_HYDRATION` (or `RANGE_LOCKED_RESTORED_FROM_RANGES`)
   - Logged during restoration in constructor
   - State is now `RANGE_LOCKED`

2. **Next tick arrives** → `OnTick()` called

3. `HandleRangeLockedState()` executes:
   - Checks if orders need resubmission (`RESTART_RETRY_STOP_BRACKETS` if needed)
   - Checks for market close cutoff
   - Normal breakout detection continues

4. Normal `RANGE_LOCKED` state processing logs appear:
   - `EXECUTION_GATE_EVAL` (if breakout detection logic runs)
   - `RESTART_RETRY_STOP_BRACKETS` (if orders need resubmission)
   - Other normal `RANGE_LOCKED` state logs

### Potential Edge Case

**If no ticks arrive immediately after restart:**
- `HandleRangeLockedState()` won't run until the next tick
- This is fine - breakout detection doesn't need to run every tick
- Orders will be resubmitted when the next tick arrives (if needed)

**If bars arrive but no ticks:**
- `OnBar()` buffers bars but doesn't call state handlers
- `HandleRangeLockedState()` still waits for next `OnTick()` call
- This is fine - breakout detection can happen on next tick

### Conclusion

✅ **Verification Passes**: `HandleRangeLockedState()` will run on the next `OnTick()` call after restoration.

✅ **No blocking guards**: No guards prevent `HandleRangeLockedState()` from running.

✅ **Expected behavior**: State handlers run asynchronously on ticks, not synchronously during restoration.

**To verify in logs:**
1. Look for `RANGE_LOCKED_RESTORED_FROM_HYDRATION` (or `RANGE_LOCKED_RESTORED_FROM_RANGES`)
2. Look for the next `OnTick()` call (check for `TICK_CALLED` or `TICK_TRACE` logs)
3. Verify `HandleRangeLockedState()` logs appear after restoration (e.g., `RESTART_RETRY_STOP_BRACKETS`, `EXECUTION_GATE_EVAL`)

The timing is correct - restoration sets state synchronously, handlers run asynchronously on next tick.
