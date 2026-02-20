# 2026-02-19 Daily Summary & Full Error Investigation

## Executive Summary

**Date:** Wednesday, February 19, 2026  
**Scope:** Robot system operations, NinjaTrader deployment, error log analysis, and fixes applied.

---

## 1. Operations Performed Today

| Action | Result |
|--------|--------|
| Rebuilt Robot.Core (Release) | Success |
| Deployed Robot.Core.dll + Robot.Core.pdb to NinjaTrader Custom | Success |
| Fixed "unable to retrieve type add on for ninjascript base" | Added missing Robot.Contracts.dll |
| System health assessment | Dashboard, Watchdog, Pipeline all healthy |
| Error log analysis | 6 distinct error types identified |
| Fixes applied | Scheduler UI, HealthMonitor grace period, Startup timing, OCO CancelPending |

---

## 2. Error Inventory (2026-02-19)

### Error 1: RECONCILIATION_QTY_MISMATCH

**When:** 17:38:58 UTC (11:38 CT)  
**Severity:** CRITICAL  
**Notification:** Pushover sent

**What happened:**  
Broker position quantity did not match execution journal quantity for one or more instruments. The robot freezes execution for that instrument until reconciled.

**Code path & reasoning:**

1. **Emission:** `ReconciliationRunner.Run()` compares `accountQtyByInstrument` (from broker snapshot) with `_journal.GetOpenJournalQuantitySumForInstrument()` (from execution journal).

   ```
   RobotCore_For_NinjaTrader/Execution/ReconciliationRunner.cs:95-106
   if (journalQty != accountQty)
   {
       _log.Write(..., "RECONCILIATION_QTY_MISMATCH", ...);
       _onQuantityMismatch?.Invoke(inst, utcNow, ...);
   }
   ```

2. **Callback:** `RobotEngine` registers `onQuantityMismatch` when creating `ReconciliationRunner`:

   ```
   RobotEngine.cs:926-933
   _reconciliationRunner = new ReconciliationRunner(..., onQuantityMismatch: (instrument, now, reason) =>
   {
       StandDownStreamsForInstrument(instrument, now, reason);
       _healthMonitor?.ReportCritical("RECONCILIATION_QTY_MISMATCH", ...);
   });
   ```

3. **Execution block:** `_frozenInstruments` is populated; `RiskGate.CheckGates()` blocks execution:

   ```
   RiskGate.cs:56-61
   if (_isInstrumentFrozen != null && _isInstrumentFrozen(instrument))
       return (false, "INSTRUMENT_FROZEN_RECONCILIATION_MISMATCH", failedGates);
   ```

**Root cause:**  
Broker position and journal are out of sync. Common causes: manual trades, prior run without clean shutdown, or journal not updated after fill/cancel.

**Resolution:**  
Operator must reconcile (verify broker vs journal) and use manual override if account is correct. No code change needed; this is a safety mechanism.

---

### Error 2: ENGINE_TICK_STALL_DETECTED (17 notifications)

**When:** 17:39:03–17:39:31 UTC  
**Severity:** ERROR  
**Notification:** 17 Pushover notifications sent

**What happened:**  
`Tick()` was not called for 120+ seconds. HealthMonitor fired stall detection repeatedly.

**Code path & reasoning:**

1. **Stall check:** `HealthMonitor.EvaluateEngineTickStall()` runs every 5 seconds (EVALUATION_INTERVAL_SECONDS):

   ```
   HealthMonitor.cs:505-543
   private void EvaluateEngineTickStall(DateTimeOffset utcNow)
   {
       if (_lastEngineTickUtc == DateTimeOffset.MinValue) return;
       var elapsed = (utcNow - _lastEngineTickUtc).TotalSeconds;
       if (elapsed >= ENGINE_TICK_STALL_SECONDS)  // 120
       {
           if (!_engineTickStallActive)
           {
               _engineTickStallActive = true;
               _log.Write(..., "ENGINE_TICK_STALL_DETECTED", ...);
               SendNotification("ENGINE_TICK_STALL", title, message, priority: 2, ...);
           }
       }
   }
   ```

2. **Tick update:** `_lastEngineTickUtc` is set when `SetEngineTickUtc()` is called from `RobotEngine.Tick()`:

   ```
   RobotEngine.cs:1341
   _lastEngineTickUtc = utcNow;
   ```

3. **Why stall during startup:**  
   Strategy started ~11:38 CT. During hydration (bars loading for multiple instruments), `Tick()` is not called because NinjaTrader does not invoke it until real-time data flows. `_lastEngineTickUtc` was set from an earlier tick, then 120+ seconds passed without a new tick.

**Fix applied:**  
Added `ENGINE_START_GRACE_PERIOD_SECONDS = 180` so stall detection is suppressed for 3 minutes after engine start:

```
HealthMonitor.cs:508-512
if (_engineStartUtc != DateTimeOffset.MinValue &&
    (utcNow - _engineStartUtc).TotalSeconds < ENGINE_START_GRACE_PERIOD_SECONDS)
    return;
```

**Recovery:**  
Engine recovered by 17:42:12 (`DATA_STALL_RECOVERED`, `ENGINE_TICK_CALLSITE` resumed).

---

### Error 3: ORDER_REJECTED / ORDER_SUBMIT_FAIL (CancelPending)

**When:** 17:39:44–17:39:45 UTC  
**Instruments:** MNQ, M2K, MES, MYM, MCL  
**Error message:** "Order can't be submitted: order status is CancelPending"

**What happened:**  
NinjaTrader reported entry stop orders as Rejected with this message. These are OCO sibling cancellations, not broker rejections.

**Code path & reasoning:**

1. **OCO flow:** Breakout strategy submits buy stop + sell stop in same OCO group (`RobotOrderIds.EncodeEntryOco()`). When one fills, the other is cancelled by NinjaTrader.

2. **NinjaTrader behavior:** The cancelled sibling is reported as `Rejected` with "order status is CancelPending" — NinjaTrader’s way of indicating OCO cancellation.

3. **Original handling:** `HandleOrderUpdateReal` treated all Rejected as true rejections:

   ```
   NinjaTraderSimAdapter.NT.cs:1209-1465 (original)
   else if (orderState == OrderState.Rejected)
   {
       ...
       _executionJournal.RecordRejection(...);
       _log.Write(..., "ORDER_REJECTED", ...);
   }
   ```

4. **ORDER_SUBMIT_FAIL:** Emitted when `Submit()` returns an order with `OrderState.Rejected`:

   ```
   NinjaTraderSimAdapter.NT.cs:5339-5372
   if (submitResult.OrderState == OrderState.Rejected)
   {
       _log.Write(..., "ORDER_SUBMIT_FAIL", ...);
       return OrderSubmissionResult.FailureResult(error, utcNow);
   }
   ```

   The first callback (17:39:44) had `orderUpdate_OrderState=CancelPending` — the order was already cancelling when the callback arrived. NinjaTrader may have reported the initial Rejected state before our Submit() returned, or the Submit path saw a Rejected result from a prior OCO cancellation.

**Fix applied:**  
Treat Rejected + "CancelPending" as OCO sibling cancellation:

```
NinjaTraderSimAdapter.NT.cs:1354-1398
var isOcoSiblingCancel = (fullErrorMsg?.IndexOf("CancelPending", ...) ?? -1) >= 0;
if (isOcoSiblingCancel)
{
    orderInfo.State = "CANCELLED";
    _log.Write(..., "OCO_SIBLING_CANCELLED", new {
        oco_group_id, sibling_order_id, filled_order_id,
        execution_instrument_key, intent_id, ...
    });
    return;  // Skip RecordRejection
}
```

---

### Error 4: CONNECTION_LOST / DISCONNECT_FAIL_CLOSED_ENTERED

**When:** 11:31:38 UTC (05:31 CT)  
**Severity:** ERROR / CRITICAL

**What happened:**  
Connection to broker (Live) was lost. Robot entered fail-closed mode. Multiple strategy instances each received `OnConnectionStatusUpdate`, causing multiple CONNECTION_LOST logs.

**Code path & reasoning:**

1. **NinjaTrader callback:** `Connection.ConnectionStatusUpdate` fires for each strategy instance:

   ```
   RobotSimStrategy.cs (subscribes to Connection.ConnectionStatusUpdate)
   RobotEngine.OnConnectionStatusUpdate(ConnectionStatus status, string connectionName)
   ```

2. **State transition:** `RobotEngine.OnConnectionStatusUpdate()` transitions to `DISCONNECT_FAIL_CLOSED`:

   ```
   RobotEngine.cs:3825-3856
   if (wasConnected && !isConnected)
   {
       if (_recoveryState == CONNECTED_OK || _recoveryState == RECOVERY_COMPLETE)
       {
           _recoveryState = ConnectionRecoveryState.DISCONNECT_FAIL_CLOSED;
           ...
           LogEvent(..., "DISCONNECT_FAIL_CLOSED_ENTERED", ...);
           _healthMonitor?.ReportCritical("DISCONNECT_FAIL_CLOSED_ENTERED", payload);
       }
   }
   ```

3. **HealthMonitor:** Logs CONNECTION_LOST; uses shared state to dedupe notifications:

   ```
   HealthMonitor.cs:261-330
   _log.Write(..., "CONNECTION_LOST", ...);
   // After CONNECTION_LOST_SUSTAINED_SECONDS (60), sends notification
   ```

4. **CRITICAL_NOTIFICATION_SKIPPED:** Emergency rate limiter (5 min) prevented duplicate Pushover for same event type. First instance notified; others skipped.

**Root cause:**  
Broker/data feed disconnect (network, broker maintenance, etc.). Expected behavior; fail-closed mode blocks execution until reconnect.

---

### Error 5: STARTUP_TIMING_WARNING

**When:** 17:38:56 (YM2), 17:39:00 (CL2), and similar  
**Severity:** WARN

**What happened:**  
Strategy started after the range window had begun. Historical bars for the range may be missing.

**Code path & reasoning:**

1. **Emission:** `RobotEngine` iterates streams and checks if `utcNow >= stream.RangeStartUtc`:

   ```
   RobotEngine.cs:1015-1040
   foreach (var stream in _streams.Values)
   {
       if (utcNow >= stream.RangeStartUtc)
       {
           LogEvent(..., "STARTUP_TIMING_WARNING", new {
               warning = "Strategy started after range window — range may be incomplete or unavailable",
               stream_id, instrument, range_start_utc, slot_time_chicago,
               note = "Ensure NinjaTrader 'Days to load' setting includes historical data for the range window",
               fix_hint = "Chart → Right-click → Data Series → set 'Days to load' to cover range window (e.g. 5–10 days)"
           });
       }
   }
   ```

2. **Impact:**  
   Range may be built from incomplete data. Breakout levels could be wrong.

**Fix applied:**  
Added `fix_hint` with exact NinjaTrader UI path. No logic change; guidance only.

---

### Error 6: "unable to retrieve type add on for ninjascript base"

**When:** After deploying new Robot.Core.dll  
**Context:** NinjaTrader failed to load AddOns

**Root cause:**  
`Robot.Core.dll` depends on `Robot.Contracts.dll`. Only Robot.Core was deployed; Robot.Contracts was missing from NinjaTrader Custom. Assembly load failed, leading to the AddOn error.

**Fix applied:**  
Deployed `Robot.Contracts.dll` and `Robot.Contracts.pdb` to `%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\`.

---

## 3. Code Reference Map

| Error | Primary File | Key Lines |
|-------|--------------|-----------|
| RECONCILIATION_QTY_MISMATCH | `Execution/ReconciliationRunner.cs` | 95-106 |
| RECONCILIATION_QTY_MISMATCH callback | `RobotEngine.cs` | 926-933 |
| RECONCILIATION freeze | `Execution/RiskGate.cs` | 56-61 |
| ENGINE_TICK_STALL_DETECTED | `HealthMonitor.cs` | 505-543 |
| ENGINE_START_GRACE_PERIOD | `HealthMonitor.cs` | 103, 508-512 |
| ORDER_REJECTED / OCO_SIBLING_CANCELLED | `Execution/NinjaTraderSimAdapter.NT.cs` | 1209-1398 |
| ORDER_SUBMIT_FAIL | `Execution/NinjaTraderSimAdapter.NT.cs` | 5339-5372, 5407-5412 |
| CONNECTION_LOST | `HealthMonitor.cs` | 217-330 |
| DISCONNECT_FAIL_CLOSED_ENTERED | `RobotEngine.cs` | 3825-3856 |
| STARTUP_TIMING_WARNING | `RobotEngine.cs` | 1015-1040 |

---

## 4. Fixes Applied Today

| Fix | File(s) | Purpose |
|-----|---------|---------|
| Scheduler error parsing | `usePipelineState.js` | Show backend `detail` instead of raw JSON on permission errors |
| Engine start grace period | `HealthMonitor.cs` (NT + modules/robot) | Suppress stall detection for 3 min after start |
| Startup fix hint | `RobotEngine.cs` | Add NinjaTrader UI path for "Days to load" |
| OCO CancelPending handling | `NinjaTraderSimAdapter.NT.cs` | Treat as OCO_SIBLING_CANCELLED, skip RecordRejection |
| OCO forensic fields | `NinjaTraderSimAdapter.NT.cs` | Log oco_group_id, sibling_order_id, filled_order_id, execution_instrument_key, intent_id |
| Robot.Contracts deployment | NinjaTrader Custom | Resolve AddOn load failure |

---

## 5. Recommendations

1. **Deploy:** Rebuild Robot.Core and deploy to NinjaTrader Custom (includes all fixes).
2. **Reconciliation:** If RECONCILIATION_QTY_MISMATCH recurs, reconcile broker vs journal and use manual override if appropriate.
3. **Startup:** Start NinjaTrader before the range window, or set "Days to load" to cover the range.
4. **Connection:** CONNECTION_LOST is expected during disconnects; fail-closed behavior is correct.
