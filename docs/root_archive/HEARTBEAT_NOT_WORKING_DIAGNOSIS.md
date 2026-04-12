# Heartbeat Not Working - Diagnosis

## Status: ❌ NOT WORKING

**Finding**: 0 `ENGINE_HEARTBEAT` events found in `robot_ENGINE.jsonl`

## HeartbeatAddOn Design

The `HeartbeatAddOn` is a NinjaTrader AddOn that:
- Runs automatically when NinjaTrader starts
- Emits `ENGINE_HEARTBEAT` events every 5 seconds
- Uses a `System.Threading.Timer` for wall-clock timing
- Independent of bars, ticks, instruments, or strategies

## Possible Causes

### 1. AddOn Not Enabled in NinjaTrader
**Most Likely**: The AddOn must be manually enabled in NinjaTrader.

**Check**:
- NinjaTrader → Tools → AddOns
- Look for "HeartbeatAddOn"
- Ensure it's checked/enabled

**Fix**: Enable the AddOn in NinjaTrader Tools menu

### 2. AddOn Not Reaching State.Active
The AddOn goes through these states:
- `State.SetDefaults` → Sets name/description
- `State.Configure` → Initializes logger
- `State.Active` → Starts timer (THIS IS WHERE HEARTBEAT STARTS)
- `State.Terminated` → Stops timer

**Check**: Look for Print statements in NinjaTrader Output window:
- "HeartbeatAddOn CONFIGURE"
- "HeartbeatAddOn ACTIVE"
- "HeartbeatAddOn: Timer started"

**If missing**: AddOn may not be loading or reaching Active state

### 3. Logger Initialization Failing
The logger is initialized in `State.Configure`:
```csharp
_logger = new RobotLogger(projectRoot, customLogDir: null, instrument: null);
```

**Check**: Look for Print statements:
- "HeartbeatAddOn: RobotLogger initialized successfully"
- "HeartbeatAddOn: ERROR initializing logger"

**If failing**: Project root resolution or logger creation may be failing

### 4. Events Being Filtered Out
Events are written via `RobotLogger.Write()` which routes through `RobotLoggingService`.

**Check**: 
- Verify `RobotLoggingService` is routing ENGINE events correctly
- Check if log level filtering is blocking ENGINE_HEARTBEAT

## Diagnostic Steps

### Step 1: Check NinjaTrader Output Window
1. Open NinjaTrader
2. Go to Tools → Output
3. Look for Print statements from HeartbeatAddOn:
   - "HeartbeatAddOn CONFIGURE"
   - "HeartbeatAddOn ACTIVE"
   - "HeartbeatAddOn: Timer started"
   - "HeartbeatAddOn: Heartbeat emitted at..."

### Step 2: Verify AddOn is Enabled
1. NinjaTrader → Tools → AddOns
2. Find "HeartbeatAddOn"
3. Ensure it's checked/enabled
4. If not present, the AddOn may not be compiled/loaded

### Step 3: Check Compilation
1. Verify `HeartbeatAddOn.cs` is in NinjaTrader project
2. Check for compilation errors
3. Rebuild NinjaTrader project

### Step 4: Check Logger Initialization
If AddOn reaches Active but no heartbeats:
- Check Print statements for logger initialization errors
- Verify project root resolution
- Check if `RobotLogger` constructor is failing

## Expected Behavior

When working correctly:
- AddOn loads automatically when NinjaTrader starts
- Reaches `State.Active` and starts timer
- Emits `ENGINE_HEARTBEAT` every ~5 seconds
- Events appear in `robot_ENGINE.jsonl` with:
  - `event: "ENGINE_HEARTBEAT"`
  - `stream: "__engine__"`
  - `instance_id: <GUID>`
  - `strategy_state: "Active"` (or current state)
  - `ninjatrader_connection_state: "Connected"` (or current state)

## Next Steps

1. ✅ Check NinjaTrader Output window for Print statements
2. ✅ Verify AddOn is enabled in Tools → AddOns
3. ✅ Check for compilation errors
4. ✅ Verify project root resolution
5. ⏳ If still not working, add more diagnostic logging

## Code Location

- **File**: `modules/robot/ninjatrader/HeartbeatAddOn.cs`
- **Timer Start**: `StartHeartbeatTimer()` called in `State.Active`
- **Heartbeat Emit**: `TimerCallback()` method
- **Event Creation**: Uses `RobotEvents.EngineBase()` with `eventType: "ENGINE_HEARTBEAT"`
