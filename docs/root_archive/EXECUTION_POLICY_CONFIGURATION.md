# Execution Policy Configuration - How Contract Quantities Are Determined

## Current Behavior

The robot **DOES** read from the config file (`configs/execution_policy.json`), but with an important limitation:

### How It Works Now

1. **At Startup**: Robot loads `configs/execution_policy.json` once
2. **When Streams Are Created**: Each stream reads `base_size` from the policy and stores it
3. **Cached Value**: The quantity is stored in `_orderQuantity` (readonly field) and never changes
4. **Comment in Code**: "startup-only, not reloadable"

### The Problem

- **Policy file changed** → Robot doesn't see the change until restart
- **Streams cache quantity** → Even if policy reloaded, existing streams keep old value
- **Requires restart** → To pick up new policy values

## Current Config File Location

**File**: `configs/execution_policy.json`

**Structure**:
```json
{
  "schema": "qtsw2.execution_policy",
  "canonical_markets": {
    "RTY": {
      "execution_instruments": {
        "M2K": {
          "enabled": true,
          "base_size": 2,    ← This is what determines quantity
          "max_size": 2
        }
      }
    }
  }
}
```

## Code Flow

1. **RobotEngine.Start()** (line 398):
   ```csharp
   _executionPolicy = ExecutionPolicy.LoadFromFile(_executionPolicyPath);
   // Comment: "startup-only, not reloadable"
   ```

2. **When Creating Stream** (line 2833):
   ```csharp
   var orderQuantity = GetOrderQuantity(canonicalInstrument, executionInstrument);
   // Reads from _executionPolicy.base_size
   ```

3. **StreamStateMachine Constructor** (line 233):
   ```csharp
   _orderQuantity = orderQuantity;  // Stored as readonly field
   ```

4. **When Submitting Orders** (line 3525):
   ```csharp
   _executionAdapter.SubmitStopEntryOrder(..., _orderQuantity, ...);
   // Uses cached _orderQuantity value
   ```

## Why It's Designed This Way

**Consistency**: Prevents quantity from changing mid-trade
- If policy changes while a stream has open orders, quantity mismatch could occur
- Cached value ensures all orders for a stream use the same quantity

**Performance**: Avoids file I/O on every order submission

**Safety**: Fail-closed design - if policy can't be loaded, execution is blocked

## Making It Dynamic (If Needed)

If you want the robot to reload the policy dynamically, you would need to:

### Option 1: Reload Policy on Timetable Change
- When timetable changes (new trading day), reload execution policy
- Recreate streams with new quantities
- **Pros**: Picks up changes daily
- **Cons**: Still requires timetable change

### Option 2: Add File Watcher/Poller
- Use `FilePoller` (already exists for timetable) to watch execution policy
- Reload policy when file changes
- Recreate affected streams
- **Pros**: Real-time updates
- **Cons**: More complex, need to handle mid-trade changes

### Option 3: Read Policy on Each Order (Not Recommended)
- Read policy file every time an order is submitted
- **Pros**: Always current
- **Cons**: File I/O overhead, potential inconsistency if policy changes mid-stream

## Current Workaround

**To apply new policy values:**
1. Update `configs/execution_policy.json`
2. Restart the robot
3. Streams will be recreated with new quantities

## Verification

Check logs for `INTENT_POLICY_REGISTERED` events:
```json
{
  "event": "INTENT_POLICY_REGISTERED",
  "data": {
    "canonical_instrument": "RTY",
    "execution_instrument": "M2K",
    "expected_qty": "2",  ← This shows what quantity was used
    "max_qty": "2",
    "source": "EXECUTION_POLICY_FILE"
  }
}
```

## Summary

- ✅ Robot reads from `configs/execution_policy.json`
- ✅ Uses `base_size` value for order quantity
- ⚠️ Only reads once at startup
- ⚠️ Caches value in each stream
- ⚠️ Requires restart to pick up changes

**The config file IS the source of truth, but changes require a restart to take effect.**
