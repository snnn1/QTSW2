# Execution Anomaly Monitoring — Implementation Summary

**Date:** 2026-03-14  
**Status:** Complete

## 1. Files Changed

### Robot (C#)

| File | Changes |
|------|---------|
| `RobotCore_For_NinjaTrader/RobotEventTypes.cs` | Added 8 anomaly event types to `_levelMap` and `_allEvents` |
| `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` | `EmitUnmappedFill`: emit `EXECUTION_GHOST_FILL_DETECTED`; `ENTRY_ORDER_DUPLICATE_BLOCKED`: emit `DUPLICATE_ORDER_SUBMISSION_DETECTED` |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` | Emit `PROTECTIVE_DRIFT_DETECTED` at 3 sites (stop qty mismatch, target qty mismatch, price mismatch) |
| `RobotCore_For_NinjaTrader/Execution/ReconciliationRunner.cs` | Emit `POSITION_DRIFT_DETECTED` alongside `RECONCILIATION_QTY_MISMATCH` |

### Watchdog (Python)

| File | Changes |
|------|---------|
| `modules/watchdog/config.py` | Added anomaly thresholds; added 8 anomaly types + `RECONCILIATION_QTY_MISMATCH` to `LIVE_CRITICAL_EVENT_TYPES` |
| `modules/watchdog/state_manager.py` | Added `_pending_orders`, `_pending_derived_events`; `record_order_submitted`, `record_order_filled`, `record_order_cancelled`, `check_stuck_orders`, `drain_pending_derived_events` |
| `modules/watchdog/event_processor.py` | Handlers for `ORDER_SUBMIT_SUCCESS`, `EXECUTION_FILLED`, `ORDER_CANCELLED`; clear `_pending_orders` on `ENGINE_START` |
| `modules/watchdog/aggregator.py` | Added anomaly types to `important_types`; `MANUAL_OR_EXTERNAL_ORDER_DETECTED`, `UNOWNED_LIVE_ORDER_DETECTED` for orphan mapping; drain `drain_pending_derived_events` after processing; `check_stuck_orders` in `_check_alert_conditions`; severity mapping for anomalies |

### Documentation

| File | Changes |
|------|---------|
| `docs/robot/EXECUTION_ANOMALY_ARCHITECTURE.md` | Phase 1 architecture mapping (pre-existing) |
| `docs/robot/EXECUTION_ANOMALY_IMPLEMENTATION_SUMMARY.md` | This file |

---

## 2. Event Names Added or Reused

| Event Name | Origin | Trigger Condition |
|------------|--------|-------------------|
| `EXECUTION_GHOST_FILL_DETECTED` | Robot | Fill arrives for order not in registry/adopted; emitted from `EmitUnmappedFill` |
| `PROTECTIVE_DRIFT_DETECTED` | Robot | IEA detects stop/target qty or price mismatch during protective validation |
| `ORPHAN_ORDER_DETECTED` | Watchdog (mapped) | `MANUAL_OR_EXTERNAL_ORDER_DETECTED`, `UNOWNED_LIVE_ORDER_DETECTED` treated as orphan for anomaly feed |
| `DUPLICATE_ORDER_SUBMISSION_DETECTED` | Robot | `ENTRY_ORDER_DUPLICATE_BLOCKED`; second entry submission for same intent |
| `POSITION_DRIFT_DETECTED` | Robot | `RECONCILIATION_QTY_MISMATCH`; broker_qty ≠ journal_qty |
| `ORDER_STUCK_DETECTED` | Watchdog (derived) | Order submitted but no fill/cancel within threshold (entry 120s, protective 90s) |
| `EXECUTION_LATENCY_SPIKE_DETECTED` | Watchdog (derived) | Submit→fill latency ≥ 5000 ms |
| `RECOVERY_LOOP_DETECTED` | Watchdog (derived) | ≥ 3 `DISCONNECT_RECOVERY_STARTED` in 600s window |

---

## 3. Robot vs Watchdog Origin

| Anomaly | Robot-Emitted | Watchdog-Derived |
|---------|---------------|------------------|
| Ghost fill | ✓ | |
| Protective drift | ✓ | |
| Orphan order | (existing: MANUAL_OR_EXTERNAL, UNOWNED_LIVE) | Mapped in feed |
| Duplicate order | ✓ | |
| Position drift | ✓ | |
| Stuck order | | ✓ |
| Latency spike | | ✓ |
| Recovery loop | | ✓ |

---

## 4. Configuration / Thresholds

| Config | Default | Purpose |
|--------|---------|---------|
| `DUPLICATE_ORDER_WINDOW_MS` | 5000 | Duplicate submission window (informational in payload) |
| `ORDER_STUCK_ENTRY_THRESHOLD_SECONDS` | 120 | Entry order working too long |
| `ORDER_STUCK_PROTECTIVE_THRESHOLD_SECONDS` | 90 | Stop/target working too long |
| `EXECUTION_LATENCY_SPIKE_THRESHOLD_MS` | 5000 | Submit→fill latency threshold |
| `RECOVERY_LOOP_COUNT_THRESHOLD` | 3 | Recovery entries in window |
| `RECOVERY_LOOP_WINDOW_SECONDS` | 600 | Rolling window for recovery loop |

---

## 5. Reused Existing Events

- **Orphan:** `MANUAL_OR_EXTERNAL_ORDER_DETECTED`, `UNOWNED_LIVE_ORDER_DETECTED` — no new robot emission; watchdog maps them into orphan anomaly feed.
- **Position drift:** `RECONCILIATION_QTY_MISMATCH` — kept; `POSITION_DRIFT_DETECTED` added as alias with structured fields.
- **Protective drift:** `PROTECTIVE_MISMATCH_FAIL_CLOSE`, `PROTECTIVE_QUANTITY_MISMATCH_FAIL_CLOSE` — kept; `PROTECTIVE_DRIFT_DETECTED` added with structured payload.

---

## 6. Schema Summary

| Event | Origin | Trigger | Key Fields | Severity |
|-------|--------|---------|------------|----------|
| EXECUTION_GHOST_FILL_DETECTED | Robot | Unmapped fill | timestamp, account, instrument, broker_order_id, quantity, price, side, mapped, reason, stream_key, intent_id | Critical |
| PROTECTIVE_DRIFT_DETECTED | Robot | Protective mismatch | drift_type, position_qty, expected/actual stop/target price/qty, stream_key, intent_id | Critical |
| ORPHAN_ORDER_DETECTED | Mapped | MANUAL_OR_EXTERNAL / UNOWNED_LIVE | (from source event) | High |
| DUPLICATE_ORDER_SUBMISSION_DETECTED | Robot | Duplicate blocked | intent_id, instrument, role, side, qty, price, first_order_id, window_ms, reason | High |
| POSITION_DRIFT_DETECTED | Robot | Qty mismatch | broker_qty, engine_qty, journal_qty, drift_class, intent_ids | Critical |
| ORDER_STUCK_DETECTED | Watchdog | Working > threshold | broker_order_id, role, working_duration_seconds, threshold_seconds, intent_id, instrument | Medium |
| EXECUTION_LATENCY_SPIKE_DETECTED | Watchdog | Latency > threshold | order_id, submitted_at, executed_at, latency_ms, threshold_ms, qty, price | Info |
| RECOVERY_LOOP_DETECTED | Watchdog | Recovery count in window | count, window_seconds, current_recovery_state | High |

---

## 7. Testing

- **Unit tests:** Not added in this pass; detection logic is straightforward and additive.
- **Manual validation:**
  1. **Ghost fill:** Simulate fill for unknown order (e.g. manual fill) and confirm `EXECUTION_GHOST_FILL_DETECTED` in feed.
  2. **Protective drift:** Restart with mismatched protective qty/price and confirm `PROTECTIVE_DRIFT_DETECTED`.
  3. **Duplicate order:** Trigger second entry for same intent and confirm `DUPLICATE_ORDER_SUBMISSION_DETECTED`.
  4. **Position drift:** Induce broker/journal qty mismatch and confirm `POSITION_DRIFT_DETECTED` and `RECONCILIATION_QTY_MISMATCH`.
  5. **Stuck order:** Leave entry order working > 120s and confirm `ORDER_STUCK_DETECTED` from watchdog.
  6. **Latency spike:** Submit order and delay fill > 5s (e.g. sim) and confirm `EXECUTION_LATENCY_SPIKE_DETECTED`.
  7. **Recovery loop:** Trigger 3+ recoveries in 10 minutes and confirm `RECOVERY_LOOP_DETECTED`.

---

## 8. Watchdog Integration

All eight anomalies:

- Appear in live feed and important events
- Included in ring buffer
- Available via REST and WebSocket
- Not filtered as repetitive noise (except safe dedupe for recovery loop)
- Severity mapped for alerts (critical / warning / info)
