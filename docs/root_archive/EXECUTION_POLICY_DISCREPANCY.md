# Execution Policy Discrepancy Investigation

## Problem

- **Policy file** (`configs/execution_policy.json`): Shows `base_size: 2` for M2K
- **Robot logs**: Show `policy_base_size: "1"` and `expected_qty: "1"`
- **Actual orders**: Placed with `quantity: 1`

## Timeline

- **File Last Modified**: Jan 28, 2026 at 23:33:37 UTC (11:33 PM)
- **Robot Run Started**: Jan 29, 2026 at 14:00:00 UTC (2:00 PM)
- **Time Difference**: ~14.5 hours (file modified BEFORE robot started)

## Evidence

### Current File Content
```json
"RTY": {
  "execution_instruments": {
    "M2K": {
      "enabled": true,
      "base_size": 2,  ← File shows 2
      "max_size": 2
    }
  }
}
```

### Log Evidence
```json
{
  "event": "INTENT_POLICY_REGISTERED",
  "data": {
    "canonical_instrument": "RTY",
    "execution_instrument": "M2K",
    "expected_qty": "1",  ← Logs show 1
    "max_qty": "2"
  }
}
```

### Actual Orders
```json
{
  "event_type": "ORDER_SUBMITTED",
  "data": {
    "quantity": "1"  ← Orders placed with 1
  }
}
```

## Possible Explanations

### 1. File Was Edited But Not Saved (Most Likely)
- File was edited in editor to change from 1 to 2
- Change wasn't saved to disk until Jan 28 at 11:33 PM
- Robot started before file was saved, read old value (1)
- **Evidence**: File modification time matches when change was saved

### 2. File System Caching Issue
- File system cached old value
- Robot read cached value instead of current file
- **Evidence**: Unlikely on Windows with normal file operations

### 3. Robot Reading From Different Location
- Robot reading from different path than expected
- **Evidence**: Code shows standardized path `configs/execution_policy.json`

### 4. Deserialization Bug
- Bug in JSON deserialization reading wrong value
- **Evidence**: Code looks correct, reads `base_size` directly

## Code Flow

1. **RobotEngine.Start()** (line 398):
   ```csharp
   _executionPolicy = ExecutionPolicy.LoadFromFile(_executionPolicyPath);
   ```

2. **When Creating Stream** (line 2833):
   ```csharp
   var orderQuantity = GetOrderQuantity(canonicalInstrument, executionInstrument);
   // Should return base_size from policy
   ```

3. **GetOrderQuantity()** (line 2256):
   ```csharp
   var quantity = execInstPolicy.base_size;
   return quantity;  // Should return 2
   ```

4. **StreamStateMachine Constructor** (line 233):
   ```csharp
   _orderQuantity = orderQuantity;  // Stored as readonly
   ```

5. **When Submitting Orders** (line 3525):
   ```csharp
   _executionAdapter.SubmitStopEntryOrder(..., _orderQuantity, ...);
   // Uses cached _orderQuantity value
   ```

## Root Cause Analysis

**Most Likely**: File was edited in editor but not saved when robot started
- Editor shows `base_size: 2` (unsaved change)
- File on disk had `base_size: 1` (old value)
- Robot read from disk, got `base_size: 1`
- File was saved later (Jan 28, 11:33 PM)
- Robot already cached `_orderQuantity = 1`

## Solution

**To Fix Immediately:**
1. **Save the file** (if editor has unsaved changes)
2. **Restart the robot** to reload policy
3. **Verify** logs show `expected_qty: "2"` after restart

**To Prevent Future Issues:**
- Always save config files before starting robot
- Consider adding file watcher to reload policy on change
- Add validation to log policy file hash at startup

## Verification Steps

After restart, check logs for:
```json
{
  "event": "INTENT_POLICY_REGISTERED",
  "data": {
    "expected_qty": "2"  ← Should be 2 after restart
  }
}
```

## Conclusion

The robot **IS** reading from the config file correctly, but it read the **old value** (1) because the file wasn't saved when the robot started. The file now shows the correct value (2), but the robot needs to be restarted to pick it up.
