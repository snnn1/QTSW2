# Execution Logging Gaps Assessment

**Date**: 2026-02-18  
**Context**: Post Unify Fill Events refactor; audit for remaining gaps in execution logging.

---

## 1. Canonical Fill Schema Gaps

The Unify Fill Events spec requires these fields on every `EXECUTION_FILLED`:

| Field | Required | Current Status | Gap |
|-------|----------|----------------|-----|
| order_id | ✓ | `broker_order_id` | ✓ Present |
| intent_id | ✓ | Top-level | ✓ Present |
| instrument | ✓ | Top-level | ✓ Present |
| execution_instrument_key | ✓ | **Missing** | ❌ Not in fill payloads |
| order_type | ✓ | ✓ | ✓ Present |
| side | ✓ (BUY/SELL) | **Missing** | ❌ Only `direction` (Long/Short) in context, not in payload |
| fill_price | ✓ | ✓ | ✓ Present |
| fill_qty | ✓ | ✓ | ✓ Present |
| filled_total | ✓ | ✓ | ✓ Present |
| remaining_qty | ✓ | ✓ | ✓ Present |
| timestamp_utc | ✓ | `ts_utc` top-level | ✓ Present |
| trading_date | ✓ | ✓ | ✓ Present |
| session_class | ✓ (S1/S2 if known) | **Missing** | ❌ Not in payload; derivable from stream (e.g. ES1→S1) |
| stream_key | ✓ | `stream` | ✓ Present |
| account | ✓ | **Missing** | ❌ Available via `_iea?.AccountName` |
| source | ✓ | Implicit (RobotEngine/ExecutionAdapter) | ⚠️ Could be explicit |

**Optional but preferred**: commission, fees, slippage_ticks, oco_id, nt_order_name — all **missing**.

---

## 2. Fills That Never Emit EXECUTION_FILLED

### 2.1 Broker Flatten Fill (BROKER_FLATTEN_FILL_RECOGNIZED)

**Path**: When we call `Flatten()`, the broker creates a market order. The fill returns via `ExecutionUpdate` with **no QTSW2 tag**. We recognize it within `FLATTEN_RECOGNITION_WINDOW_SECONDS` and log `BROKER_FLATTEN_FILL_RECOGNIZED`, then return early.

**Gap**: No `EXECUTION_FILLED` is emitted. No `RecordExitFill` is called. No `OnExitFill` is sent to the coordinator. The position is closed but:
- Ledger cannot attribute the close to any intent
- PnL cannot be computed from the event stream for flatten closes
- Journal has no exit fill record

**Impact**: Realized PnL from flatten closes is not reconstructable from logs. Journals may show open positions that were actually flattened.

**Mitigation options**:
1. **Pre-flatten journal**: Before calling `account.Flatten()`, record `RecordExitFill` for all open intents on that instrument with `exitOrderType="FLATTEN"` (requires knowing position breakdown by intent).
2. **Post-fill synthetic**: When recognizing broker flatten, infer intents from `IntentMap` + instrument, emit synthetic `EXECUTION_FILLED` per intent with `order_type=FLATTEN`. Complex when multiple intents share one instrument (e.g. ES1 + ES2 both long on MES).

### 2.2 Untracked Fill (EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL)

**Path**: Fill has missing/invalid tag; we flatten immediately and log `EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL`.

**Gap**: No `EXECUTION_FILLED`. We don't know intent_id, so we cannot emit a canonical fill. Ledger cannot reconcile.

### 2.3 Unknown Order Fill (EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL)

**Path**: Fill arrives for order not in OrderMap after grace period; we enqueue flatten and log critical.

**Gap**: We have `intentId` from the order tag, but we never emit `EXECUTION_FILLED` because we fail-closed before reaching the normal fill path. The fill did occur and affects position.

---

## 3. Aggregated Entry Fills

**Path**: One broker order fills multiple intents (e.g. CL1+CL2 same price). We allocate via `AllocateFillToIntents` and call `RecordEntryFill` per intent. We emit **one** `EXECUTION_FILLED` with the primary `intent_id` and total `fill_quantity`.

**Gap**: The single `EXECUTION_FILLED` shows `intent_id=A, fill_quantity=4` when allocation was A=2, B=2. The ledger uses journals for entry, so PnL is correct. But:
- Feed/analytics would misattribute 4 to intent A
- Per-intent audit trail from events alone is wrong

**Recommendation**: Emit `EXECUTION_FILLED` per allocated intent with that intent's `allocQty` when `isAggregated`.

---

## 4. FLATTEN Order Type in Exit Path

**Path**: Protective orders use tags `QTSW2:{intentId}:STOP` or `:TARGET`. Manual flatten or broker flatten orders have **no tag**, so they never reach the `orderTypeForContext == "STOP" || "TARGET"` exit path.

**Gap**: We never log `EXECUTION_FILLED` with `order_type=FLATTEN` for the actual flatten fill. The only FLATTEN-related events are `FLATTEN_REQUESTED`, `FLATTEN_SUBMITTED`, `FLATTEN_VERIFY_PASS/FAIL` — none are fills.

---

## 5. INTENT_EXIT_FILL Missing trading_date

**Path**: `InstrumentIntentCoordinator.OnExitFill` emits `INTENT_EXIT_FILL` via `RobotEvents.EngineBase(utcNow, tradingDate: "", ...)`.

**Gap**: `trading_date` is always `""`. The coordinator doesn't have trading_date in scope. Ledger uses `INTENT_EXIT_FILL` as fallback for exit qty; schema normalizer may not require trading_date for that event, but consistency would help.

---

## 6. Execution Events Not in LIVE_CRITICAL_EVENT_TYPES

These execution events are logged but may not reach `frontend_feed.jsonl` if not in `LIVE_CRITICAL_EVENT_TYPES`:

| Event | In LIVE_CRITICAL? | Note |
|-------|-------------------|------|
| EXECUTION_FILLED | ✓ | Yes |
| EXECUTION_PARTIAL_FILL | ✓ | Yes |
| EXECUTION_EXIT_FILL | ✓ | Yes (migration) |
| BROKER_FLATTEN_FILL_RECOGNIZED | ❌ | Diagnostic only; not in feed |
| EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL | ❌ | Via CRITICAL_EVENT_REPORTED? |
| EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL | ❌ | Via CRITICAL_EVENT_REPORTED? |
| EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL | ❌ | New; should be critical |
| PROTECTIVE_ORDER_FILL_TRACKED_FROM_TAG | ❌ | Diagnostic |

---

## 7. LogCriticalWithIeaContext vs ExecutionBase

**Path**: Many critical events use `LogCriticalWithIeaContext` or `LogCriticalEngineWithIeaContext`, which may route to a different sink (e.g. health/CRITICAL) and might not include the same schema as `RobotEvents.ExecutionBase`.

**Gap**: Inconsistent schema between normal execution logs and critical logs. Some critical events may not have `trading_date`, `execution_instrument_key`, etc. promoted to top level.

---

## 8. NinjaTraderLiveAdapter

**Path**: `NinjaTraderLiveAdapter` is a stub that logs `ORDER_SUBMIT_ATTEMPT`, `STOP_MODIFY_ATTEMPT`, `FLATTEN_ATTEMPT` but does not handle real execution callbacks.

**Gap**: When/if Live adapter is implemented, it must emit the same canonical `EXECUTION_FILLED` schema. No implementation exists yet.

---

## Summary: Priority Fixes

| Priority | Gap | Effort | Impact |
|----------|-----|--------|--------|
| **P0** | Broker flatten fill: no EXECUTION_FILLED, no RecordExitFill | High | PnL broken for flatten closes |
| **P1** | Add execution_instrument_key, side, account to EXECUTION_FILLED | Low | Schema completeness |
| **P1** | Aggregated entry: emit per-intent EXECUTION_FILLED | Medium | Correct per-intent audit |
| **P2** | INTENT_EXIT_FILL: add trading_date | Low | Consistency |
| **P2** | EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL in LIVE_CRITICAL | Trivial | Visibility |
| **P3** | session_class, commission, fees, etc. | Medium | Optional fields |

---

## References

- Unify Fill Events refactor prompt
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`
- `modules/watchdog/pnl/ledger_builder.py`
- `modules/watchdog/config.py` (LIVE_CRITICAL_EVENT_TYPES)
