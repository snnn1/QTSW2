# Robot Heartbeat Architecture — Code Inspection Report

**Task:** Determine whether the robot emits an engine heartbeat independent of market ticks. Inspection only; no code changes.

---

## Section 1 — Heartbeat-Style Events

### ENGINE_TICK_CALLSITE

| File | Function | Condition | Driven by |
|------|----------|-----------|-----------|
| `RobotCore_For_NinjaTrader/RobotEngine.cs` | `TickInternal()` (lines 1469–1485) | Rate-limited: `(nowWall - _lastEngineTickCallsiteLogUtc).TotalSeconds >= 5` | **Tick()** — called from OnBarUpdate |
| `modules/robot/core/RobotEngine.cs` | `TickInternal()` (lines 1252–1261) | Every call (feed rate-limits to 5s) | **Tick()** — called from OnBarUpdate |

**Exact path:** `RobotSimStrategy.OnBarUpdate()` → `_engine.Tick(tickTimeUtc, State == State.Historical)` → `RobotEngine.TickInternal()` → `LogEvent(..., "ENGINE_TICK_CALLSITE", ...)`.

### ENGINE_ALIVE

| File | Function | Condition | Driven by |
|------|----------|-----------|-----------|
| `RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs` | `OnBarUpdate()` (lines 1475–1477) | `State == Realtime && CurrentBar > 0 && CurrentBar % HEARTBEAT_BARS_INTERVAL == 0` (HEARTBEAT_BARS_INTERVAL=1) | **Bar-driven** — every bar in Realtime |

### ENGINE_HEARTBEAT

- **Not emitted** in current codebase. Deprecated; only referenced in docs and event_processor handler (no-op).

### ENGINE_LOOP / ENGINE_LOOP_HEARTBEAT / STRATEGY_HEARTBEAT

- **Not found** in RobotCore_For_NinjaTrader or modules/robot/core.

### TIMER_META_HEARTBEAT

- **Registered** in RobotEventTypes.cs.
- **Not emitted** — no `LogEvent`/`LogEngineEvent` with `"TIMER_META_HEARTBEAT"` in the codebase.

---

## Section 2 — True Engine Heartbeat Source

### Where is ENGINE_TICK_CALLSITE emitted?

**Answer: C) Inside the strategy main loop — specifically, inside `TickInternal()`, which is called from `OnBarUpdate()`.**

**Code path:**

1. `RobotSimStrategy.OnBarUpdate()` (line 1327)
2. → `_engine.Tick(tickTimeUtc, State == State.Historical)` (line 1741)
3. → `RobotEngine.TickInternal(utcNow, isHistorical)` (line 1428)
4. → `LogEvent(..., "ENGINE_TICK_CALLSITE", ...)` (lines 1479–1484)

**Not emitted from:**
- A) OnBarUpdate() — Tick() is called from OnBarUpdate, but the event is emitted inside TickInternal
- B) OnMarketData() — OnMarketData does **not** call Tick(); it only does BE evaluation
- D) Timer/scheduled task — no timer calls Tick()
- E) Other — no other callers found

**Conclusion:** ENGINE_TICK_CALLSITE is emitted only when `Tick()` runs, and `Tick()` is invoked only from `OnBarUpdate()`. So it is **bar-driven**.

---

## Section 3 — Timer-Based Events

### Timers / periodic tasks found

| File | Usage | Emits heartbeat? |
|------|-------|------------------|
| `ProtectiveCoverageCoordinator.cs` | `Timer` for audit | No — internal audit only |
| `InstrumentExecutionAuthority.cs` | `Timer` for stall check | No |
| `MismatchEscalationCoordinator.cs` | `Timer` for audit | No |
| `NotificationService.cs` | `Task.Delay` in loops | No — notification worker |

### TIMER_META_HEARTBEAT

- Defined in event registry; **no emission** in RobotEngine, strategy, or monitoring code.

### Conclusion

**No timer-based heartbeat exists.** All timers are for audits, stall checks, or notifications. None emit liveness events.

---

## Section 4 — Behavior on Weekends

### If the market is closed and no ticks occur, does the robot emit any heartbeat?

**No.**

- `OnBarUpdate()` runs only when bars are formed.
- On weekends (no trading), NinjaTrader typically does not call `OnBarUpdate()`.
- No bars → no `OnBarUpdate()` → no `Tick()` → no `ENGINE_TICK_CALLSITE`.
- No bars → no `ENGINE_ALIVE` (it is emitted every bar in Realtime).

### Watchdog behavior when markets are closed

The watchdog has **no engine heartbeat source** when markets are closed. `ENGINE_TICK_CALLSITE` and `ENGINE_ALIVE` both depend on bar/tick flow.

---

## Section 5 — Watchdog Liveness Logic

### Events that call `update_engine_tick()` (and thus update `_last_engine_heartbeat`)

| Event Type | File | Line |
|------------|------|------|
| ENGINE_TICK_CALLSITE | event_processor.py | 175 |
| ENGINE_ALIVE | event_processor.py | 180 |

**Removed (no longer drive liveness):** ENGINE_START, ENGINE_HEARTBEAT, ENGINE_TICK_HEARTBEAT, ONBARUPDATE_CALLED, ENGINE_TICK_STALL_RECOVERED.

---

## Section 6 — Final Summary

### 1. Does the robot emit a timer-based heartbeat independent of ticks?

**No.** There is no timer-based heartbeat. All liveness events are bar/tick-driven.

### 2. What event currently drives watchdog engine liveness?

**ENGINE_TICK_CALLSITE** (primary) and **ENGINE_ALIVE** (fallback). Both are emitted only when `Tick()` or bar processing runs, which requires market data.

### 3. Will the robot emit that event when the market is closed?

**No.** With no bars or ticks, `OnBarUpdate()` is not called, so neither `ENGINE_TICK_CALLSITE` nor `ENGINE_ALIVE` is emitted.

### 4. If not, is the engine technically running but invisible to the watchdog?

**Yes.** The strategy process can be running (NinjaTrader open, strategy enabled) but with no market data. In that case the engine loop is effectively idle and no heartbeat is emitted, so the watchdog will show ENGINE STALLED even though the process is up.

### 5. If a heartbeat does not exist, where is the best place in the robot loop to emit one?

**Recommended:** Add a **timer-driven heartbeat** in `RobotEngine` that runs on a fixed interval (e.g. every 5–10 seconds) when the engine is in Realtime, independent of bars/ticks. Options:

1. **RobotEngine** — Use a `System.Threading.Timer` or `System.Timers.Timer` started in `Start()` / when entering Realtime, firing every N seconds, and emit a new event type (e.g. `ENGINE_TIMER_HEARTBEAT` or reuse `ENGINE_TICK_CALLSITE` with a different source flag).
2. **RobotSimStrategy** — Use NinjaTrader’s `TriggerCustomEvent()` with a timer to call a method that invokes `_engine.Tick()` or a dedicated heartbeat method. This keeps the emission in the strategy layer.
3. **AddOn / separate component** — A NinjaTrader AddOn with its own timer could emit heartbeat events, but that adds deployment and coupling complexity.

**Preferred:** Option 1 — a timer inside `RobotEngine` that emits when the engine is running in Realtime, so liveness is independent of market data and works during weekends, halts, and data outages.

---

## Scenarios Where Current Design Fails

| Scenario | ENGINE_TICK_CALLSITE | ENGINE_ALIVE | Watchdog sees |
|----------|----------------------|--------------|---------------|
| Market open, strategy running | Yes (~every 5s) | Yes (every bar) | ENGINE ALIVE |
| Weekend, strategy enabled | No | No | ENGINE STALLED |
| Exchange halt | No | No | ENGINE STALLED |
| Data outage | No | No | ENGINE STALLED |
| Overnight (no session) | No | No | ENGINE STALLED |
