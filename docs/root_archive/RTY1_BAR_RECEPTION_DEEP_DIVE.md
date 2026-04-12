# RTY1 Bar Reception Deep Dive

## Problem Statement
RTY1 stream is stuck in ARMED state and not transitioning to RANGE_BUILDING. Investigation into why bars are not being received.

## Current State
- **Stream**: RTY1 (RTY, S1 session, slot 09:00)
- **Current State**: ARMED
- **Time in State**: ~6+ hours
- **Expected Behavior**: Should transition to RANGE_BUILDING when:
  1. Range start time reached (02:00 CT for S1) ✓
  2. Market is open ✓
  3. Bars are available (`barCount > 0`) ❌ **BLOCKER**

## Key Findings

### 1. No RTY Bars Being Received
**Finding**: Recent `ONBARUPDATE_CALLED` events show bars for micro futures only:
- MES, MNQ, MYM, MCL, MGC, MNG, M2K
- **NO RTY bars at all**

**Evidence**:
- Last 50 `ONBARUPDATE_CALLED` events: 0 RTY bars
- Last RTY bar events: Yesterday (2026-01-28) for RTY2 only
- No RTY bars today (2026-01-29)

### 2. Bar Routing Logic
**How bars are routed** (`RobotEngine.cs` lines 1434-1468):
1. `OnBar()` receives bar with execution instrument (e.g., "RTY" or "M2K")
2. For each stream, calls `stream.IsSameInstrument(instrument)`
3. `IsSameInstrument()` canonicalizes both instruments and compares:
   - Stream's `CanonicalInstrument` (RTY for RTY1)
   - Incoming bar's canonical instrument (RTY for RTY bars, RTY for M2K bars)
4. If match, calls `stream.OnBar()` to buffer the bar

**Key Code** (`StreamStateMachine.cs` lines 422-431):
```csharp
public bool IsSameInstrument(string incomingInstrument)
{
    var incomingCanonical = GetCanonicalInstrument(incomingInstrument, _spec);
    return string.Equals(
        CanonicalInstrument,
        incomingCanonical,
        StringComparison.OrdinalIgnoreCase
    );
}
```

### 3. Diagnostic Events Missing
**Finding**: No diagnostic events found:
- No `BAR_ROUTING_DIAGNOSTIC` events (only logged if `enable_diagnostic_logs` is true)
- No `ONBARUPDATE_DIAGNOSTIC` events
- No `ARMED_WAITING_FOR_BARS` events (logged every 5 minutes if no bars)

**Implication**: Either:
- Diagnostic logging is disabled
- Robot engine is not running/processing
- Bars are not reaching the robot at all

### 4. Stream State Transitions
**Recent RTY1 transitions**:
- 2026-01-29 07:24:43: PRE_HYDRATION → ARMED
- 2026-01-29 07:24:45: ARMED → RANGE_BUILDING (briefly)
- 2026-01-29 07:24:48: Back to PRE_HYDRATION → ARMED (restart/reset)

**Analysis**: Stream restarted/reset, went back to PRE_HYDRATION, completed hydration, transitioned to ARMED, but no bars available to transition to RANGE_BUILDING.

## Root Cause Analysis

### Primary Issue: No RTY Bars from NinjaTrader
**Most Likely Causes**:

1. **RTY Execution Disabled, M2K Enabled**
   - **CRITICAL FINDING**: `execution_policy.json` shows:
     - RTY: `"enabled": false`
     - M2K: `"enabled": true`
   - M2K bars ARE arriving (micro RTY futures)
   - M2K bars should route to RTY1 stream (M2K → RTY canonical mapping)
   - **Problem**: If separate robot instances are running:
     - RTY robot instance: RTY1 stream exists, but RTY bars not arriving (RTY disabled)
     - M2K robot instance: M2K bars arriving, but may not have RTY1 stream configured
   - **Solution**: Either enable RTY execution OR ensure M2K robot instance has RTY1 stream

2. **RTY Instrument Not Configured in NinjaTrader**
   - RTY instrument may not be added to the strategy
   - RTY may not be subscribed to data feed
   - Check NinjaTrader instrument list

3. **RTY Data Feed Not Connected**
   - RTY data provider may not be connected
   - RTY may not be available from current data feed
   - Check NinjaTrader data connections

4. **RTY Bars Not Being Generated**
   - Market may be closed for RTY
   - RTY contract may have expired
   - RTY may not be trading today

5. **Instrument Name Mismatch**
   - NinjaTrader may be using different instrument name (e.g., "RTY 03-26" vs "RTY")
   - Check what instrument name NinjaTrader is using

### Secondary Issue: Bar Routing
If RTY bars ARE arriving but not reaching RTY1:
- Check `BAR_ROUTING_DIAGNOSTIC` events (if diagnostic logging enabled)
- Verify `IsSameInstrument()` matching logic
- Check if RTY1 stream's `CanonicalInstrument` matches incoming bar's canonical instrument

## Diagnostic Checklist

### Immediate Checks
1. **Is RTY instrument configured in NinjaTrader?**
   - Check NinjaTrader strategy instrument list
   - Verify RTY is added and subscribed

2. **Are RTY bars arriving at all?**
   - Check raw robot logs (`robot_ENGINE.jsonl`) for RTY bar events
   - Look for `OnBar` calls with RTY instrument

3. **Is the robot engine running?**
   - Check for `ENGINE_TICK_CALLSITE` events
   - Verify engine is processing ticks

4. **Check instrument matching**
   - Verify RTY1's `CanonicalInstrument` is "RTY"
   - Check if incoming bars use "RTY" or different name

### Diagnostic Commands

```powershell
# Check for RTY bars in raw logs
Select-String -Path "logs/robot/robot_ENGINE.jsonl" -Pattern '"instrument":"RTY"|RTY.*OnBar' | Select-Object -Last 20

# Check for BAR_ROUTING_DIAGNOSTIC events
Select-String -Path "logs/robot/frontend_feed.jsonl" -Pattern "BAR_ROUTING_DIAGNOSTIC.*RTY" | Select-Object -Last 10

# Check for ARMED_WAITING_FOR_BARS events
Select-String -Path "logs/robot/frontend_feed.jsonl" -Pattern "ARMED_WAITING_FOR_BARS.*RTY1" | Select-Object -Last 5

# Check what instruments are receiving bars
Select-String -Path "logs/robot/frontend_feed.jsonl" -Pattern "ONBARUPDATE_CALLED" | Select-Object -Last 100 | ForEach-Object { ($_.Line | ConvertFrom-Json).data.instrument } | Group-Object | Sort-Object Count -Descending
```

## Expected Behavior

### When RTY Bars Arrive
1. `OnBar()` called with RTY instrument
2. `IsSameInstrument("RTY")` called for RTY1 stream
3. Match found (RTY == RTY)
4. `stream.OnBar()` called → bar buffered
5. `GetBarBufferCount() > 0` → transition to RANGE_BUILDING
6. Range computed from buffered bars
7. Range locked at slot time (09:00)

### Current State
- Step 1 is not happening: No RTY bars arriving from NinjaTrader
- RTY1 is correctly waiting in ARMED state
- Once bars arrive, transition should happen immediately

## Recommendations

1. **Check NinjaTrader Configuration**
   - Verify RTY instrument is added to strategy
   - Verify RTY is subscribed to data feed
   - Check RTY contract expiration date

2. **Enable Diagnostic Logging**
   - Set `enable_diagnostic_logs: true` in logging config
   - This will enable `BAR_ROUTING_DIAGNOSTIC` events
   - Will show which instruments are receiving bars and which streams match

3. **Check Data Feed**
   - Verify RTY data provider is connected
   - Check if RTY is trading today
   - Verify RTY contract is active

4. **Monitor Bar Reception**
   - Watch for `ONBARUPDATE_CALLED` events with RTY instrument
   - Check for `ARMED_WAITING_FOR_BARS` events (logged every 5 minutes)
   - Monitor `BAR_ROUTING_DIAGNOSTIC` events if diagnostic logging enabled

## Conclusion

**Root Cause**: RTY bars are not being received by the RTY robot instance. However, M2K bars ARE arriving, which should route to RTY1 stream via canonical mapping (M2K → RTY).

**Key Finding**: RTY execution is disabled (`execution_policy.json`), but M2K execution is enabled. This suggests:
- Separate robot instances may be running (one for RTY, one for M2K)
- M2K bars are going to the M2K instance, not the RTY instance
- RTY1 stream exists in the RTY instance, but that instance isn't receiving M2K bars

**Next Steps**:
1. **Check if M2K bars route to RTY1**: Enable diagnostic logging (`enable_diagnostic_logs: true`) to see `BAR_ROUTING_DIAGNOSTIC` events showing M2K → RTY routing
2. **Verify robot instance configuration**: Check if RTY robot instance is configured to receive M2K bars, or if M2K instance has RTY1 stream
3. **Enable RTY execution**: If RTY should receive RTY bars directly, enable RTY in `execution_policy.json`
4. **OR configure M2K as execution instrument**: If using M2K for RTY streams, ensure RTY1 stream uses M2K as execution instrument
5. **Check NinjaTrader configuration**: Verify which instruments are configured in NinjaTrader strategy
6. **Monitor bar routing**: Once bars start routing correctly, RTY1 should transition to RANGE_BUILDING immediately

The watchdog is correctly showing RTY1 in ARMED state - it's waiting for bars that aren't arriving. The issue is likely a robot instance configuration problem where M2K bars aren't reaching the RTY1 stream.
