# Execution Anomaly Monitoring — Architecture Mapping

**Date:** 2026-03-14  
**Status:** Phase 1 Complete

## 1. Existing Components

| Component | Location | Purpose |
|-----------|----------|---------|
| **OrderRegistry** | `RobotCore_For_NinjaTrader/Execution/OrderRegistry.cs` | Order-centric registry; broker order id primary; TryResolveByBrokerOrderId, TryResolveByAlias; OWNED/ADOPTED/TERMINAL |
| **InstrumentExecutionAuthority** | `InstrumentExecutionAuthority.NT.cs`, `OrderRegistry.cs`, `RecoveryPhase3.cs` | Registry ownership, adoption, recovery decisions |
| **ReconciliationRunner** | `ReconciliationRunner.cs` | broker_qty vs journal_qty; emits RECONCILIATION_QTY_MISMATCH |
| **NinjaTraderSimAdapter.NT.cs** | Execution update handling | HandleExecutionUpdateReal, fill processing, orphan detection |
| **event_processor.py** | `modules/watchdog/event_processor.py` | Records EXECUTION_*, PROTECTIVE_*, RECONCILIATION_* timestamps |
| **state_manager.py** | `modules/watchdog/state_manager.py` | compute_unprotected_positions, record_*_events |

## 2. Existing Events (Reuse vs New)

| Anomaly | Existing Event(s) | Action |
|---------|-------------------|--------|
| **Ghost fill** | EXECUTION_FILL_UNMAPPED, EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL, ORPHAN_FILL_CRITICAL | Add EXECUTION_GHOST_FILL_DETECTED in robot at same detection point; structured payload |
| **Protective drift** | PROTECTIVE_MISMATCH_FAIL_CLOSE, PROTECTIVE_QUANTITY_MISMATCH_FAIL_CLOSE | Add PROTECTIVE_DRIFT_DETECTED as canonical; IEA already validates |
| **Orphan order** | MANUAL_OR_EXTERNAL_ORDER_DETECTED, EXECUTION_UNOWNED, COMPLETED_INTENT_ORDER_UPDATE | Add ORPHAN_ORDER_DETECTED when live order has no owner/adoption |
| **Duplicate order** | ENTRY_ORDER_DUPLICATE_BLOCKED, EXECUTION_DUPLICATE_SKIPPED | Add DUPLICATE_ORDER_SUBMISSION_DETECTED with structured fields |
| **Position drift** | RECONCILIATION_QTY_MISMATCH | Add POSITION_DRIFT_DETECTED as alias in ReconciliationRunner; ensure watchdog surfaces |
| **Stuck order** | (none) | Add ORDER_STUCK_DETECTED; robot has order timestamps |
| **Latency spike** | (none) | Add EXECUTION_LATENCY_SPIKE_DETECTED; correlate ORDER_SUBMIT_SUCCESS + EXECUTION_FILLED |
| **Recovery loop** | DISCONNECT_RECOVERY_STARTED, RECOVERY_* | Add RECOVERY_LOOP_DETECTED in watchdog (derived) |

## 3. Implementation Points

| Anomaly | Robot | Watchdog |
|---------|-------|----------|
| Ghost fill | NinjaTraderSimAdapter: Emit EXECUTION_GHOST_FILL_DETECTED when fill unmapped/unknown | important_types, LIVE_CRITICAL, severity critical |
| Protective drift | IEA or NinjaTraderSimAdapter: Emit PROTECTIVE_DRIFT_DETECTED | important_types, LIVE_CRITICAL |
| Orphan order | NinjaTraderSimAdapter: Emit ORPHAN_ORDER_DETECTED when MANUAL_OR_EXTERNAL or unowned | important_types |
| Duplicate order | NinjaTraderSimAdapter: Emit DUPLICATE_ORDER_SUBMISSION_DETECTED when duplicate blocked | important_types |
| Position drift | ReconciliationRunner: Emit POSITION_DRIFT_DETECTED (alias for RECONCILIATION_QTY_MISMATCH) | important_types, handler |
| Stuck order | NinjaTraderSimAdapter or periodic check: Emit ORDER_STUCK_DETECTED | important_types |
| Latency spike | NinjaTraderSimAdapter: Emit on fill when submit→fill > threshold | important_types |
| Recovery loop | — | event_processor: count RECOVERY_STARTED in window; emit RECOVERY_LOOP_DETECTED |

## 4. Event Schema (New Events)

All use `timestamp_utc`, `run_id`, `account`, `instrument` where available. See Phase 11 in implementation.
