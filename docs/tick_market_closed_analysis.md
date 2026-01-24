# Tick() Behavior When Market is Closed

## Answer: YES, Ticks Happen When Market is Closed

## Evidence

### 1. Timer Starts Regardless of Market State

**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs` (line 205-209)

```csharp
// CRITICAL FIX: Start timer in DataLoaded state for SIM mode
// In SIM mode, the strategy may never reach Realtime state if market is closed,
// but we still need heartbeats for watchdog monitoring
// Start periodic timer for time-based state transitions (decoupled from bar arrivals)
StartTickTimer();
```

**Key Point**: The comment explicitly states that the timer starts even when the market is closed, specifically to enable watchdog monitoring.

### 2. Timer Fires Every 1 Second Unconditionally

**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs` (line 887)

```csharp
_tickTimer = new Timer(TickTimerCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
```

**Key Point**: The timer interval is fixed at 1 second. There's no market-state check that pauses or stops the timer.

### 3. Timer Callback Calls Tick() Unconditionally

**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs` (line 912-929)

```csharp
private void TickTimerCallback(object? state)
{
    try
    {
        if (!_engineReady || _engine is null)
        {
            return;  // Only checks engine readiness, NOT market state
        }
        
        var utcNow = DateTimeOffset.UtcNow;
        _engine.Tick(utcNow);  // Called unconditionally if engine ready
    }
    catch (Exception ex)
    {
        // Error handling
    }
}
```

**Key Point**: The only guard is `_engineReady` (engine initialization), NOT market state. If the engine is ready, `Tick()` is called every second.

### 4. Tick() Emits Heartbeat Unconditionally (After Our Fix)

**File**: `modules/robot/core/RobotEngine.cs` (line 748-777)

```csharp
lock (_engineLock)
{
    // HEARTBEAT: Emit unconditionally for process liveness (before any early returns)
    // This ensures watchdog always sees engine liveness, regardless of trading readiness
    
    // ... heartbeat emission code ...
    
    // TRADING READINESS: Guards that prevent trading logic
    if (_spec is null) return;
    if (_time is null) return;
    
    // ... rest of trading logic ...
}
```

**Key Point**: Heartbeat emits at the very top, before any market-state or trading-readiness checks.

## What Happens When Market is Closed

1. ✅ **Timer continues firing** every 1 second
2. ✅ **Tick() is called** every second (if engine ready)
3. ✅ **Heartbeat emits** every 60 seconds (rate-limited)
4. ✅ **Streams receive Tick()** calls (if engine ready)
5. ⚠️ **Trading logic may be blocked** by risk gates (but Tick() still runs)

## Why This Design Makes Sense

1. **Watchdog Monitoring**: Need heartbeats even when market closed to detect if engine process is alive
2. **Time-Based State Transitions**: Streams need Tick() calls to transition states based on time (e.g., PRE_HYDRATION → ARMED when slot time arrives)
3. **Timetable Reactivity**: Need to poll timetable file for changes even when market closed
4. **Recovery Management**: Broker recovery state machine needs Tick() calls to manage reconnect flow

## Verification

To verify ticks are happening when market is closed:

1. **Check logs for ENGINE_TICK_HEARTBEAT events** during market closed hours
2. **Check logs for TICK_CALLED events** from StreamStateMachine (rate-limited to 1 min)
3. **Check logs for TICK_TRACE events** from StreamStateMachine (rate-limited to 5 min)

If these events appear during market closed hours, ticks are definitely happening.

## Potential Issues

### None Identified

The implementation correctly allows ticks when market is closed:
- Timer doesn't check market state ✅
- Tick() doesn't check market state ✅
- Heartbeat emits unconditionally ✅
- Trading logic is protected by risk gates (separate concern) ✅

## Summary

**YES, ticks happen when market is closed.**

The timer fires every 1 second regardless of market state, and `Tick()` is called unconditionally (if engine is ready). This is by design to:
- Enable watchdog monitoring
- Drive time-based state transitions
- Allow timetable hot-reloading
- Manage broker recovery

The only thing that changes when market is closed is that **trading logic may be blocked by risk gates**, but the Tick() method itself still executes and emits heartbeats.
