# S1 Streams Stuck in ARMED State - Diagnostic Summary

## Problem
S1 streams (ES1, NG1, NQ1, YM1, RTY1) are stuck in ARMED state when they should be transitioning to RANGE_BUILDING.

## Root Cause Analysis

### 1. Transition Requirements (from StreamStateMachine.cs:1746-1792)
Streams transition from ARMED → RANGE_BUILDING when:
- ✅ `utcNow >= RangeStartUtc` (Range start time has passed - S1 range start is 02:00 Chicago)
- ❌ `barCount > 0` (Bars must be available in buffer)
- ✅ `utcNow < MarketCloseUtc` (Market is not closed)

**Current Status**: Range start time has passed (current time ~07:01 Chicago), but `barCount == 0` for all S1 streams.

### 2. Data Feed Connection Issues
From recent logs:
- **ENGINE_TICK_STALL_DETECTED**: Last tick at 11:14:02 UTC, stall detected at 11:16:07 UTC (2+ minutes gap)
- **DISCONNECT_FAIL_CLOSED_ENTERED**: Multiple disconnection events
- **CONNECTION_RECOVERED**: Connections recovered but may not be receiving bars

### 3. No Bars Being Received
- **No ENGINE_BAR_HEARTBEAT events** in recent logs (indicates no bars from NinjaTrader)
- **No BAR_DELIVERY_TO_STREAM events** (bars not being routed to streams)
- **No BAR_BUFFERED events** (bars not being accepted into buffers)

### 4. Bar Routing Logic (RobotEngine.cs:1419-1425)
Bars are routed to streams via `IsSameInstrument()`:
```csharp
foreach (var s in _streams.Values)
{
    if (s.IsSameInstrument(instrument))  // Matches canonical instrument
    {
        s.OnBar(barUtc, open, high, low, close, utcNow);
    }
}
```

**Issue**: If no bars are being received from NinjaTrader, routing logic never executes.

## Investigation Findings

### Bar Routing
- ✅ **Routing logic is correct**: Uses `IsSameInstrument()` which handles canonical mapping (MES → ES)
- ✅ **Streams exist**: S1 streams are initialized and in ARMED state
- ❌ **No bars incoming**: NinjaTrader is not calling `OnBar()` or bars are being rejected

### Data Feed Connection
- ❌ **Connection instability**: Multiple disconnect/recovery cycles
- ❌ **Tick stalls**: ENGINE_TICK_STALL_DETECTED indicates data feed gaps
- ❌ **No bar heartbeats**: ENGINE_BAR_HEARTBEAT events missing (bars not being received)

### Pre-Hydration Status
- ⚠️ **No recent PRE_HYDRATION_COMPLETE events**: Cannot confirm if pre-hydration completed successfully
- ⚠️ **No BARSREQUEST events**: Cannot confirm if historical bars were requested/loaded

## Next Steps to Investigate

### 1. Check NinjaTrader Connection
- Verify NinjaTrader is connected to data feed
- Check if OnBarUpdate() is being called in RobotSimStrategy
- Verify instrument subscriptions are active

### 2. Check Bar Reception
- Enable diagnostic logs (`enable_diagnostic_logs: true`) to see ENGINE_BAR_HEARTBEAT events
- Check if bars are being rejected (look for BAR_PARTIAL_REJECTED events)
- Verify bar timestamps are valid (not future bars)

### 3. Check Stream Initialization
- Verify streams completed pre-hydration successfully
- Check if BarsRequest loaded historical bars for S1 streams
- Verify streams are in correct state (ARMED vs PRE_HYDRATION)

### 4. Check Instrument Matching
- Verify execution instruments match what NinjaTrader is providing
- Check canonical instrument mapping (MES → ES, MNQ → NQ, etc.)
- Verify IsSameInstrument() is matching correctly

## Potential Micro vs Mini Issue

### Hypothesis
If NinjaTrader is subscribed to **micro futures** (MES, MNQ, MYM, etc.) but the timetable specifies **mini futures** (ES, NQ, YM, etc.):

1. **Timetable** specifies: `"instrument": "ES"` → Stream has `ExecutionInstrument = "ES"`, `CanonicalInstrument = "ES"`
2. **BarsRequest** maps ES → MES (via `GetMicroFutureForBaseInstrument()`)
3. **OnBarUpdate()** receives bars for MES
4. **OnBar()** receives `instrument = "MES"` 
5. **IsSameInstrument("MES")** should map MES → ES and match ES streams ✅

**However**, if NinjaTrader strategy is subscribed to the **wrong instrument**:
- Strategy subscribed to ES, but bars are coming from MES subscription → No bars received
- Strategy subscribed to MES, but timetable expects ES → Bars route correctly via canonical mapping

### Key Question
**What instrument is the NinjaTrader strategy actually subscribed to?**
- Check `Instrument.MasterInstrument.Name` in RobotSimStrategy (line 89)
- This determines what bars OnBarUpdate() receives
- If it's MES but timetable has ES, bars should still route (canonical mapping)
- If it's ES but no ES data feed → No bars received

## Recommended Fixes

### Immediate Actions
1. **Check NinjaTrader Instrument**: Verify what instrument the strategy is subscribed to
2. **Check Execution Policy**: Verify execution instruments match what's subscribed
3. **Restart Robot**: May resolve connection issues
4. **Enable Diagnostic Logs**: Add `enable_diagnostic_logs: true` to see bar reception

### Code-Level Fixes (if needed)
1. **Add logging** in OnBar() to track when bars are received but no streams match
2. **Add diagnostic** in HandleArmedState() to log why bars aren't available
3. **Add recovery logic** to request bars via BarsRequest if no bars received after range start
4. **Add instrument mismatch detection**: Log warning if OnBar() receives instrument that doesn't match any stream

## Files to Check
- `modules/robot/ninjatrader/RobotSimStrategy.cs` - OnBarUpdate() implementation
- `modules/robot/core/RobotEngine.cs` - OnBar() routing logic
- `modules/robot/core/StreamStateMachine.cs` - HandleArmedState() transition logic
- `configs/robot/logging.json` - Diagnostic logging configuration
