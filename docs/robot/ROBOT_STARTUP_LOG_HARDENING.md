# Robot Startup Log Hardening — Implementation Summary

## Overview

Reduces excessive startup logging and avoids reconciliation checks before the broker environment is ready. No trading logic, risk logic, order execution, reconciliation math, watchdog logic, or event schema was modified.

---

## 1. Engine-Level Event Deduplication

### Problem

When NinjaTrader starts, each strategy instance (one per instrument) logs identical engine-level events. With 8 instruments, the log contained 8 identical messages for:

- `EXECUTION_BLOCKED NT_CONTEXT_NOT_SET`
- `RECONCILIATION_QTY_MISMATCH`
- `POSITION_DRIFT_DETECTED`
- `EXPOSURE_INTEGRITY_VIOLATION`

### Solution

Added `EngineLogDedupe` with a short-term dedupe cache. Identical events within `ENGINE_LOG_DEDUPE_WINDOW_SECONDS` (10 seconds) are logged once per engine state change, not once per instrument.

### Implementation

- **New file:** `EngineLogDedupe.cs` (in both `modules/robot/core/` and `RobotCore_For_NinjaTrader/`)
- **Constant:** `ENGINE_LOG_DEDUPE_WINDOW_SECONDS = 10`
- **Dedupe key:** `event_type:reason` (e.g. `EXECUTION_BLOCKED:NT_CONTEXT_NOT_SET`)
- **Integration:** `RobotLoggingService.Log()` calls `EngineLogDedupe.ShouldLog(eventType, reason)` before enqueueing; if `false`, the event is skipped.

### Files Modified

- `modules/robot/core/EngineLogDedupe.cs` (new)
- `RobotCore_For_NinjaTrader/EngineLogDedupe.cs` (new)
- `modules/robot/core/RobotLoggingService.cs` — dedupe check before enqueue
- `RobotCore_For_NinjaTrader/RobotLoggingService.cs` — same

---

## 2. Suppress Reconciliation Before Broker Connection

### Problem

Reconciliation ran even when NinjaTrader was not connected or not logged in, producing meaningless warnings (`RECONCILIATION_QTY_MISMATCH`, `POSITION_DRIFT_DETECTED`, `EXPOSURE_INTEGRITY_VIOLATION`) because broker position was not yet available.

### Solution

Added a connection guard before running reconciliation. Reconciliation runs only when broker connection is confirmed.

### Implementation

- **New property:** `IsBrokerConnected` — `_lastConnectionStatus == ConnectionStatus.Connected`
- **Guards added in:**
  - `RunReconciliationOnRealtimeStart()` — returns immediately if `!IsBrokerConnected`
  - `RunReconciliationPeriodicThrottle()` — skips `_reconciliationRunner?.RunPeriodicThrottle()` if `!IsBrokerConnected`
- **Note:** `RunPendingForceReconcile()` (operator-triggered force reconcile from file) still runs; it does not depend on broker position.

### Files Modified

- `RobotCore_For_NinjaTrader/RobotEngine.cs` — `IsBrokerConnected` property and guards in both reconciliation entry points

---

## 3. Expected Result

Startup log should now look like:

```
ENGINE_START
EXECUTION_BLOCKED NT_CONTEXT_NOT_SET
CONNECTION_CONFIRMED
IDENTITY_INVARIANTS_OK
ENGINE_TIMER_HEARTBEAT
ENGINE_ALIVE
```

Instead of dozens of duplicate warnings.

---

## 4. Constraints Respected

- **Not modified:** order execution logic, reconciliation math, watchdog logic, event schema
- **Only adjusted:** logging behavior and connection guard
- **Safety checks unchanged:** engine still blocks execution when `NT_CONTEXT_NOT_SET` or broker connection is missing
