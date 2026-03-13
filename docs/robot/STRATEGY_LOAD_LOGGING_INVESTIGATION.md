# Strategy Load Failure – Logging Investigation

**Date:** 2026-03-13  
**Symptom:** Strategy fails to load regardless of commit; some instruments (e.g. MNQ) don't turn on.

---

## Why Strategies Aren't Turning On (Primary Cause)

**Root cause: Windows "Not enough quota" (0x80070718)** – NinjaTrader is exhausting system resources (GDI handles, USER objects, or desktop heap). This explains why rollback does not fix it: it is not a code bug.

**Evidence from log.20260313.00008.txt:**
- 01:01:29 – MGC strategy reaches "Engine ready"
- 01:04:23 – 13× `Unhandled exception: Not enough quota is available to process this command`

**Why it started suddenly:** Resource use builds up over time (charts, strategies, windows). Once the limit is hit, new operations fail. Rollback does not help because the process is already over the limit.

**Immediate steps:**
1. Close NinjaTrader completely (Task Manager if needed).
2. Reboot the machine to clear GDI/USER handles.
3. Restart NinjaTrader and enable fewer charts/strategies at once.

**Longer-term:**
- Run fewer strategy instances (e.g. 4 instead of 8).
- Restart NinjaTrader periodically during the day.
- Consider increasing desktop heap (registry: `SharedSection` in `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\SubSystems\Windows`).

---

## Secondary: ExecutionJournal Race (Fixed)

When 8 strategies start at once, they used to race on `.startup_check`. One could fail with "journal directory not writable". Each instance now uses a unique temp file (`.startup_check_{guid}`) so they do not collide.

---

## Root Cause: Slow Historical Load (10+ Minutes) — FIXED

**Symptom:** Strategies took ~10 minutes to transition from "Loading..." to "Simulation" (vs. seconds in earlier sessions).

**Root cause:** During Historical phase, `Tick(barUtc)` is called with **bar time** (advances 1 min per bar). The timetable poll used `utcNow` (bar time) for `ShouldPoll()`, so with a 5-second poll interval and bars 1 minute apart, **every bar triggered a timetable poll**:
- `File.ReadAllBytes(timetable)` + SHA256 hash + `TimetableContract.LoadFromFile()` (JSON parse)
- ~400 bars × 8 strategies = **~3,200 disk I/O operations** during Historical
- File contention (OneDrive, antivirus) amplified the delay

**Fix (2026-03-13):** Use wall-clock time (`DateTimeOffset.UtcNow`) for the timetable poll check instead of `utcNow`. Poll now occurs at most every 5 seconds of real time, regardless of bar flow.

**Fix v2 (2026-03-13):** Pass `isHistorical` explicitly from strategy (`State == State.Historical`) so engine skips during Historical:
- Timetable poll (no config changes during replay)
- Reconciliation (no live account during Historical)
- Identity invariants check (Realtime-only)

**Files changed:** `RobotCore_For_NinjaTrader/RobotEngine.cs`, `RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs`, `NT_ADDONS/RobotEngine.cs`, `modules/robot/core/RobotEngine.cs`

---

## Summary

The strategy **does load** according to NinjaTrader logs (e.g. "Engine ready - all initialization complete"). The problem is likely one or more of:

1. **Logging pipeline overload** – `ENGINE_TICK_CALLSITE` logs on every tick, including Historical, causing huge volume.
2. **File lock contention** – Other processes (watchdog, OneDrive) holding log files.
3. **Backpressure** – Queue grows to 40k–290k events; pipeline can’t keep up.

---

## Findings

### 1. NinjaTrader Logs Show Success

Recent `log.20260313.*.txt` entries show:

- `Vendor assembly 'Robot.Core' version='1.0.0.0' loaded`
- `Strategy instance initialized`
- `Engine ready - all initialization complete. Instrument=MGC, EngineReady=True, InitFailed=False`

So the strategy loads and initializes successfully.

### 2. Robot Logging Errors (`logs/robot/robot_logging_errors.txt`)

| Error Type | Count | Last Seen |
|------------|-------|-----------|
| File locked – "being used by another process" | Many | 2026-03-12 02:17 |
| Backpressure (queue 40k–290k) | Many | 2026-02-26 |
| I/O race – "I/O package is not thread safe" | Several | 2026-02-26 |
| Log rotation failed (file locked) | Several | 2026-03-10 |
| Index/count buffer errors | Few | 2026-02-26 |

### 3. ENGINE_TICK_CALLSITE Volume

- Logged on **every** `Tick()` call (including Historical).
- With 8 instruments × ~390 bars = ~3,120 ticks during Historical.
- In Realtime, ticks are continuous.
- Marked as WARN so it is not dropped under backpressure.
- This event type is a major contributor to queue growth.

### 4. Processes That Read Log Files

- **Watchdog** – `event_feed.py`, `fill_metrics.py`, `ledger_builder.py` read `robot_*.jsonl`.
- **Dashboard** – reads `frontend_feed.jsonl` (fed from robot logs).
- **Data merger** – may read logs.
- **OneDrive** – may lock files during sync if `QTSW2` is under OneDrive.

### 5. Strategy Lifecycle

`strategy_lifecycle.log` shows:

- `Historical_ENTER` → strategy enters "Calculating".
- No `Realtime_ENTER` in the last snippet, so it may stay in Historical for a long time or never reach Realtime.

### 6. "Loading..." in NinjaTrader Connection Column (2026-03-13)

**Symptom:** 6 of 7 strategy instances show "Loading..." in the Connection column; only 1 shows "Simulation".

**What "Loading..." means:** NinjaTrader shows "Loading..." while the strategy is in `State.Historical` (Calculating phase). It switches to "Simulation" or "Live" only after the strategy reaches `State.Realtime`.

**Evidence from `strategy_lifecycle.log` (01:24 UTC session):**
- All 7 instruments (M2K, MYM, MCL, MNG, MGC, MES, MNQ) entered `Historical_ENTER` between 01:24:23 and 01:24:26.
- **None reached `REALTIME_ENTER`** in that session.
- Strategies are stuck in Historical (Calculating) for 9+ minutes.

**Evidence from `robot_ENGINE.jsonl`:**
- `BAR_RECEIVED_NO_STREAMS` (MCL): Bars arriving but streams not yet created – indicates timetable/stream creation may have failed for that engine.
- `BAR_TIME_INTERPRETATION_MISMATCH` (MNQ): Bar age 1895 min – historical bars arriving out of order after disconnect; rate-limited warning.
- `DATA_STALL_RECOVERED` (M2K): One strategy recovered from data stall.

**Conclusion:** Strategies are stuck in Historical phase. They never complete "Calculating" and thus never reach Realtime. Possible causes:
1. Historical processing blocked or extremely slow (logging, I/O).
2. Timetable/stream creation failed for some engines (`BAR_RECEIVED_NO_STREAMS`).
3. Resource exhaustion ("Not enough quota") blocking UI/strategy thread.

---

## Root Cause Hypothesis

1. During Historical, `ENGINE_TICK_CALLSITE` floods the logging queue.
2. Queue grows to tens or hundreds of thousands of events.
3. Backpressure and write failures occur.
4. If logging blocks or slows the main thread, NinjaTrader can appear stuck on "Calculating" or unresponsive.
5. File locks from watchdog/OneDrive can cause write failures and rotation failures.

---

## Recommended Fixes

### Fix 1: Rate-Limit ENGINE_TICK_CALLSITE During Historical (High Priority)

In `RobotEngine.Tick()`, do not log `ENGINE_TICK_CALLSITE` during Historical, or rate-limit it (e.g. once per 5 seconds). During Historical the engine is not "live" for watchdog purposes.

**Location:** `RobotCore_For_NinjaTrader/RobotEngine.cs` around line 1376.

### Fix 2: Stop Watchdog Before Strategy Load (Operational)

If watchdog is running, stop it before starting NinjaTrader strategies. This avoids log file contention during startup.

### Fix 3: Exclude Logs from OneDrive (If Applicable)

If `QTSW2` is under OneDrive, exclude `logs/robot/` from sync to avoid OneDrive locking files.

### Fix 4: Reduce Log Volume in Config

In `configs/robot/logging.json`:

- Set `enable_diagnostic_logs: false` for normal runs.
- Consider lowering `max_queue_size` so backpressure triggers earlier and drops non-critical events sooner (trade-off: less logging vs. less load).

---

## Quick Test: Disable ENGINE_TICK_CALLSITE During Historical

To test whether this event is causing the issue:

1. Add a guard in `RobotEngine.Tick()`: skip `ENGINE_TICK_CALLSITE` when `State == State.Historical` (or equivalent).
2. Rebuild and redeploy.
3. Start the strategy and see if it reaches Realtime more reliably.

---

## Files Referenced

- `logs/robot/robot_logging_errors.txt` – logging pipeline errors
- `logs/robot/strategy_lifecycle.log` – strategy state transitions
- `RobotCore_For_NinjaTrader/RobotEngine.cs` – `ENGINE_TICK_CALLSITE` emission
- `configs/robot/logging.json` – logging configuration
