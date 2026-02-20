# Error Catalog — Incident Packs & Documentation

Reference for the six primary error types. Replay packs exist where IEA-level determinism applies.

| # | Error | Root Cause | Code Location | Replay Pack |
|---|-------|------------|---------------|-------------|
| 1 | RECONCILIATION_QTY_MISMATCH | Broker position ≠ journal quantity. Safety mechanism freezes instrument. | ReconciliationRunner.cs:95-106 → RobotEngine.cs:926-933 → RiskGate.cs:56-61 | No (RobotEngine-level) |
| 2 | ENGINE_TICK_STALL_DETECTED | Tick() not called for 120+ s during startup/hydration. | HealthMonitor.cs:505-543 (EvaluateEngineTickStall). Fix: 3‑min grace period at HealthMonitor.cs:508-512 | No (HealthMonitor-level) |
| 3 | ORDER_REJECTED / CancelPending | OCO sibling cancellation reported as Rejected by NinjaTrader. | NinjaTraderSimAdapter.NT.cs:1209-1398. Fix: Treat as OCO_SIBLING_CANCELLED, skip RecordRejection | **MNQ_OCO_SIBLING_CANCELLED** |
| 4 | ORDER_SUBMIT_FAIL | Same OCO flow: Submit() saw Rejected for the cancelling sibling. | NinjaTraderSimAdapter.NT.cs:5339-5372 | Same as #3 |
| 5 | CONNECTION_LOST / DISCONNECT_FAIL_CLOSED | Broker disconnect. Fail-closed mode. | RobotEngine.cs:3825-3856, HealthMonitor.cs:261-330 | No (RobotEngine-level) |
| 6 | STARTUP_TIMING_WARNING | Strategy started after range window; historical bars may be missing. | RobotEngine.cs:1015-1040 (STARTUP_TIMING_WARNING) | No (RobotEngine-level) |

---

## 1. RECONCILIATION_QTY_MISMATCH

**Root cause:** `ReconciliationRunner.Run()` compares broker position (`accountQtyByInstrument`) with journal quantity (`_journal.GetOpenJournalQuantitySumForInstrument()`). Mismatch triggers instrument freeze.

**Flow:**
1. `ReconciliationRunner.cs:95-106` — emits `RECONCILIATION_QTY_MISMATCH`
2. `RobotEngine.cs:926-933` — `onQuantityMismatch` callback → `HealthMonitor.ReportCritical`
3. `RiskGate.cs:56-61` — instrument frozen (block execution, allow range building)

**Resolution:** See `docs/robot/execution/RECONCILIATION_QTY_MISMATCH_RESOLUTION.md`

---

## 2. ENGINE_TICK_STALL_DETECTED

**Root cause:** `HealthMonitor.EvaluateEngineTickStall()` — no `Tick()` for 120+ seconds during startup/hydration.

**Fix:** 3‑min grace period at `HealthMonitor.cs:508-512` (startup vs runtime distinction).

**Flow:**
- `HealthMonitor.cs:505-543` — `EvaluateEngineTickStall`
- Logs `ENGINE_TICK_STALL_DETECTED` or `ENGINE_TICK_STALL_STARTUP` (INFO during grace)
- Sends notification after sustained stall

---

## 3 & 4. ORDER_REJECTED / ORDER_SUBMIT_FAIL (OCO)

**Root cause:** NinjaTrader reports OCO sibling cancellation as `Rejected` with comment "CancelPending". Not a true broker rejection.

**Fix:** In `HandleOrderUpdateReal` (NinjaTraderSimAdapter.NT.cs:1353-1397):
- If `orderState == Rejected` and comment contains "CancelPending" and order has OCO group → treat as `OCO_SIBLING_CANCELLED`
- Set `orderInfo.State = "CANCELLED"`, skip `RecordRejection`

**Replay pack:** `modules/robot/replay/incidents/MNQ_OCO_SIBLING_CANCELLED/`
- Synthetic events: long fills, short receives OrderUpdate Rejected+CancelPending
- Invariant: `ORDER_STATE_BY_STEP` — short intent order state = CANCELLED

---

## 5. CONNECTION_LOST / DISCONNECT_FAIL_CLOSED

**Root cause:** Broker disconnect. Robot enters fail-closed mode.

**Flow:**
- `RobotEngine.OnConnectionStatusUpdate()` → `DISCONNECT_FAIL_CLOSED_ENTERED`
- `HealthMonitor.cs:261-330` — logs `CONNECTION_LOST`, after 60s `CONNECTION_LOST_SUSTAINED` + notification

---

## 6. STARTUP_TIMING_WARNING

**Root cause:** Strategy started after range window; historical bars may be missing.

**Location:** `RobotEngine.cs:1015-1040` — logs `STARTUP_TIMING_WARNING` per instrument when startup time is past range window.

---

## Replay Incident Packs (IEA-level)

| Pack | Scenario | Invariants |
|------|----------|------------|
| SAMPLE | Basic fill | (none) |
| MNQ_BE_NO_TRIGGER | Long MNQ, tick below BE | NO_DUPLICATE, INTENT_REQUIRES_POLICY |
| MYM_BE_TRIGGER | Long MYM, tick at BE | NO_DUPLICATE, INTENT_REQUIRES_POLICY, BE_PRICE_CROSSED, BE_TRIGGERED |
| **MNQ_OCO_SIBLING_CANCELLED** | OCO entry: long fills, short CancelPending | NO_DUPLICATE, INTENT_REQUIRES_POLICY, ORDER_STATE_BY_STEP (CANCELLED) |
