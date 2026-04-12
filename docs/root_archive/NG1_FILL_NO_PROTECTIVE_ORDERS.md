# NG1 Fill - No Protective Orders Analysis

## Issue
NG1 entry order was filled long, but no limit or stop orders were placed.

## Diagnostic Steps

### 1. Check for Execution Events
Look for these events in `logs/robot/robot_MNG*.jsonl`:

```powershell
# Check for fill events
Get-Content "logs\robot\robot_MNG*.jsonl" | Select-String -Pattern "EXECUTION_FILLED|EXECUTION_PARTIAL_FILL"

# Check for errors
Get-Content "logs\robot\robot_MNG*.jsonl" | Select-String -Pattern "EXECUTION_ERROR|EXECUTION_UPDATE_IGNORED"

# Check for intent registration
Get-Content "logs\robot\robot_MNG*.jsonl" | Select-String -Pattern "INTENT_REGISTERED"
```

### 2. Possible Root Causes (Same as NQ1)

#### A. Order Tag Missing/Invalid
- **Event**: `EXECUTION_UPDATE_IGNORED_NO_TAG`
- **Cause**: Order tag not set correctly during submission
- **Check**: Look for `ORDER_TAG_SET_FAILED` events

#### B. Intent Not Registered
- **Event**: `EXECUTION_ERROR` with message "Intent not found in _intentMap"
- **Cause**: Intent was not registered before entry order submission
- **Check**: Look for `INTENT_REGISTERED` events for NG1 intent IDs

#### C. Intent Incomplete
- **Event**: `EXECUTION_ERROR` with message "Intent incomplete"
- **Cause**: Intent missing Direction, StopPrice, or TargetPrice
- **Check**: Look at `INTENT_REGISTERED` events - check `has_direction`, `has_stop_price`, `has_target_price` fields

#### D. Order Submission Failed
- **Event**: `ORDER_SUBMIT_FAIL` or `ORDER_SUBMIT_INTENT_NOT_REGISTERED`
- **Cause**: Protective orders failed to submit
- **Check**: Look for protective order submission attempts

### 3. Key Logging Points (Already Added)

The following logging was added to diagnose this issue:

1. **`EXECUTION_UPDATE_IGNORED_NO_TAG`** (line 914 in `NinjaTraderSimAdapter.NT.cs`)
   - Logs when execution updates are ignored due to missing/invalid tags

2. **`EXECUTION_ERROR`** (line 1038 in `NinjaTraderSimAdapter.NT.cs`)
   - Logs when entry fills but intent is not found in `_intentMap`

3. **`INTENT_REGISTERED`** (line 577 in `NinjaTraderSimAdapter.cs`)
   - Logs when intent is registered, including all required fields

4. **`ORDER_TAG_SET_FAILED`** (line 678 in `NinjaTraderSimAdapter.NT.cs`)
   - Logs when order tag verification fails

5. **`ORDER_SUBMIT_INTENT_NOT_REGISTERED`** (line 735, 2308 in `NinjaTraderSimAdapter.NT.cs`)
   - Logs when order submission is attempted but intent is not registered

### 4. Next Steps

1. **Check recent logs** for NG1/MNG execution events
2. **Identify the intent ID** from the entry order that filled
3. **Check if intent was registered** before the fill
4. **Check if HandleEntryFill was called** and what happened
5. **Check for any errors** during protective order submission

### 5. Recovery

If NG1 is currently in a position without protective orders:

1. **Manual intervention**: Place stop and limit orders manually in NinjaTrader
2. **Restart**: On restart, the system should detect the position and place protective orders (if recovery logic is implemented)
3. **Check recovery logic**: Verify that `OnStateChange(State.Realtime)` handles existing positions correctly

## Related Issues
- Same root cause as NQ1 fill issue (see `NQ1_FILL_NO_PROTECTIVE_ORDERS_ANALYSIS.md`)
- All logging enhancements added for NQ1 should also help diagnose NG1
