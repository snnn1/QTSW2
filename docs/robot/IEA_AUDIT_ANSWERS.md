# IEA Implementation Audit — Answers to Structural Correctness Questions

This document answers the 10 audit questions to prove structural correctness of the Instrument Execution Authority (IEA) implementation.

---

## 1. Serialization Integrity (Queue Discipline)

### Q: List every method in InstrumentExecutionAuthority that mutates position, bracket, or BE state. Confirm each one is only called from inside the per-instrument queue worker.

**State-mutating methods in IEA:**

| Method | Mutates | Called from |
|--------|---------|-------------|
| `HandleEntryFill` | OrderMap (EntryFillTime, Protective*), protective submission | Worker (via `ProcessExecutionUpdate` → `HandleExecutionUpdateReal`) |
| `EvaluateBreakEvenCore` | BE state, calls `ModifyStopToBreakEven`, `Flatten` | Worker (via `Enqueue` from `EvaluateBreakEven`) |
| `TryAggregateWithExistingOrders` | OrderMap, cancels/submits orders | **Caller thread** (via `SubmitStopEntryOrder` from StreamStateMachine) |
| `AllocateFillToIntents` | `orderInfo.AggregatedFilledByIntent` | Worker (inside `HandleExecutionUpdateReal`) |
| `AddOrUpdateOrder` | OrderMap | Adapter (when submitting); adapter calls from worker for protectives |

**Gap:** `TryAggregateWithExistingOrders` and `SubmitStopEntryOrder` run on the **caller thread** (StreamStateMachine/OnBarUpdate), not the queue. Entry order submission is **outside** the queue.

**Mitigation:** Entry orders are submitted before any fills. The queue serializes execution updates (fills) and BE. Aggregation races with fills only if an entry fill arrives while aggregation is in progress — unlikely because aggregation happens at RANGE_LOCKED (before breakout), and fills occur after breakout.

### Q: Is there any code path where HandleEntryFill, EvaluateBreakEven, ResizeBracket, or protective submission runs outside the queue?

- **HandleEntryFill:** Only called from `HandleExecutionUpdateReal`, which runs on the worker. ✓
- **EvaluateBreakEven:** Public method enqueues; `EvaluateBreakEvenCore` runs on worker. ✓
- **ResizeBracket:** No explicit `ResizeBracket` method. Bracket resize = `SubmitProtectiveStop` with new quantity when partial fill detected. That runs inside `HandleEntryFill` → worker. ✓
- **Protective submission:** Only from `HandleEntryFill` → worker. ✓

### Q: During hydration or restart, do any state mutations occur directly instead of being enqueued?

**Answer:** No hydration-specific IEA logic found. IEA does not reconstruct bracket state from journals on restart. Adoption logic runs only during `HandleEntryFill` when an execution update indicates an entry fill. On restart, if protectives already exist (e.g. from pre-restart), the next execution callback would trigger `HandleEntryFill` → adoption path.

**Risk:** If restart occurs with working protectives but no execution callback is replayed, IEA does not proactively adopt. Adoption is reactive (on fill callback).

### Q: Does the queue guarantee FIFO processing, and is there exactly one worker per (account, executionInstrumentKey)?

- **FIFO:** `BlockingCollection<Action>` with `TryTake` — FIFO. ✓
- **One worker per (account, executionInstrumentKey):** `InstrumentExecutionAuthorityRegistry.GetOrCreate` returns one IEA per key. Each IEA has one `_workerThread`. ✓

---

## 2. Adoption Logic Correctness

### Q: In HasWorkingProtectivesForIntent and adoption logic, do we validate quantity, side, instrument, and order state — not just price?

**Current validation:**
- **Price:** Stop and target prices checked against intent within `BracketToleranceTicks`. ✓
- **Quantity:** Not validated. Adoption assumes existing protectives cover the position.
- **Side:** Implicit — tag is `QTSW2:{intentId}:STOP`; intentId encodes direction.
- **Instrument:** Not explicitly checked; adapter is per-instrument.
- **Order state:** `HasWorkingProtectivesForIntent` requires `OrderState.Working` or `OrderState.Accepted`. ✓

**Gap:** Quantity is not validated. If existing protectives have wrong quantity (e.g. from partial fill before restart), we adopt anyway.

### Q: If only one leg (stop or target) exists, what happens?

**Answer:** `HasWorkingProtectivesForIntent` returns true only when **both** stop and target exist and are Working/Accepted. If only one exists, it returns false → we proceed to submit protectives (may create duplicate for the missing leg, or fail on OCO).

### Q: If quantities differ from expected filled quantity, do we resize or fail-close?

**Answer:** Adoption path does not check quantity. We adopt if prices match. Resize happens only when `HandleEntryFill` is called again with a new `totalFilledQuantity` and we go through the normal submission path (not adoption) — `SubmitProtectiveStop` detects `existingStop.Quantity != quantity` and cancel/recreates.

**Gap:** In adoption path, we never resize. If we adopt with wrong quantity, we leave it.

### Q: If protective prices cannot be read, do we adopt or fail-close?

**Answer:** Adopt. Code: "Has working protectives but couldn't read prices - adopt anyway (conservative)". We do not fail-close when prices are unreadable.

---

## 3. Idempotency & Duplicate Protection

### Q: If NT sends duplicate execution callbacks for the same execution ID, do we detect and ignore duplicates before mutating state?

**Answer:** No execution-ID deduplication. We do not track `Execution.Id` or similar. Each callback is processed. Duplicate callbacks → duplicate `HandleEntryFill` invocations. **Adoption** mitigates: second call sees working protectives and adopts. So duplicate callbacks do not cause double submission, but we do process them (wasted work, no state corruption).

### Q: Are execution IDs tracked to prevent double-processing of the same fill?

**Answer:** No. No `HashSet<executionId>` or equivalent. Adoption handles duplicate protective submission. Execution journal's `RecordEntryFill` is **not** idempotent for duplicate callbacks — it adds delta to cumulative; duplicate callback would double-count. **Risk:** Duplicate execution callbacks could inflate journal fill quantity.

---

## 4. Partial Fill Handling

### Q: If an entry partially fills, protectives are working, and another partial fill occurs, what code path resizes the bracket?

**Answer:** Each execution update (partial or full) is enqueued. Worker runs `HandleExecutionUpdateReal` → on entry fill, calls `HandleEntryFill(intentId, ..., totalFilledQuantity)`. `HandleEntryFill` calls `Executor.SubmitProtectiveStop(..., totalFilledQuantity, ...)`. Adapter's `SubmitProtectiveStop` checks `existingStop.Quantity != quantity`; if changed, cancels protectives and recreates with new quantity. ✓

### Q: Is BE evaluation blocked while a bracket is in transitional state (resizing/cancel-replace)?

**Answer:** No explicit "bracket transitional" state. BE runs on the same queue as fills, so it cannot run concurrently with `HandleEntryFill` (which does the resize). BE and resize are serialized. ✓

---

## 5. Break-Even Semantics

### Q: Does BE reference IEA ledger net position state, or does it still read Strategy Position.MarketPosition?

**Answer:** BE uses `GetActiveIntentsForBEMonitoring`, which uses **IntentMap + execution journal** (entry filled, BE not modified). It does **not** use `Strategy.Position.MarketPosition` or IEA's `GetPositionState()`. Journal is the source of truth for "entry filled" and "BE modified". ✓

### Q: Is BE idempotent — meaning if stop already at or beyond BE level, no further modification occurs?

**Answer:** Yes. `ModifyStopToBreakEvenReal` checks `stopAlreadyTighter` (Long: currentStop >= beStopPrice; Short: currentStop <= beStopPrice). If true, logs `BE_SKIP_STOP_ALREADY_TIGHTER`, records BE in journal, returns success. ✓

### Q: Is tick de-duplication applied before enqueue or inside the queue?

**Answer:** Inside the queue. `EvaluateBreakEven` enqueues `EvaluateBreakEvenCore`. De-dupe runs at the start of `EvaluateBreakEvenCore` (same price + same timestamp, or 50ms fallback). So de-dupe reduces work but does not affect correctness — we still process one representative tick per unique (price, time). ✓

---

## 6. OCO Determinism

### Q: How exactly is trading_date derived for OCO naming? Is it Chicago session date?

**Answer:** `GenerateProtectiveOcoGroup` uses `intent.TradingDate` from the intent. Intent's TradingDate comes from the engine (America/Chicago session resolution). Spec says use Chicago. ✓

### Q: Are OCO group names deterministic across restart?

**Answer:** 
- **Protective OCO:** `QTSW2:{ExecutionInstrumentKey}_{tradingDate}_{intentId}_PROTECTIVE_A{attempt}` — deterministic (intentId is hash of canonical fields). ✓
- **Entry OCO:** `EncodeEntryOco` uses `Guid.NewGuid()` — **not** deterministic. Entry OCO is for entry orders (long/short bracket); protective OCO is separate and deterministic. ✓

### Q: Can two charts ever generate different OCO names for the same bracket?

**Answer:** intentId is computed from canonical fields (tradingDate, stream, instrument, etc.). Same intent → same intentId. Same IEA (per account+instrument) → same `GenerateProtectiveOcoGroup`. Different charts for same instrument share the same IEA. ✓

---

## 7. Fail-Close Boundaries

### Q: List every condition that triggers PROTECTIVE_MISMATCH_FAIL_CLOSE.

**Answer:** One condition only: `HasWorkingProtectivesForIntent` is true, we read prices, and **prices do not match** (stop or target outside `BracketToleranceTicks`). Logs `PROTECTIVE_MISMATCH_FAIL_CLOSE` and calls `Executor.FailClosed`. ✓

### Q: Are there any mismatch cases that log a warning but continue?

**Answer:** No. On price mismatch we always fail-close. The only "adopt anyway" path is when we have working protectives but **cannot read prices** — we adopt conservatively (no warning, no fail-close).

---

## 8. Authority Isolation

### Q: When use_instrument_execution_authority is true, is there any remaining path where Strategy or Adapter can call Account.Submit, Change, or Cancel directly?

**Answer:** Adapter still calls NT APIs (`account.Submit`, `account.Change`, `account.Flatten`, etc.) — but only when invoked by IEA's Executor interface. Strategy does not call Account directly. Flow: Strategy → Adapter → (when IEA) IEA enqueues → Worker → `Executor.ProcessExecutionUpdate` → Adapter's `HandleExecutionUpdateReal` → Adapter's NT calls. All NT mutations go through adapter, and when IEA enabled, execution updates are routed through the queue. Entry order submission goes through adapter's `SubmitStopEntryOrder` (which may call IEA's `SubmitStopEntryOrder` for aggregation) — that path is **not** enqueued but is still through adapter, not Strategy. ✓

### Q: Is IEA_BYPASS_ATTEMPTED enforced at runtime or only logged?

**Answer:** **Enforced.** When IEA enabled and `_iea == null`, adapter returns `OrderSubmissionResult.FailureResult` and does **not** submit. Order submission is blocked. ✓

---

## 9. Restart & Hydration

### Q: On restart, how does IEA reconstruct bracket state before first fill?

**Answer:** IEA does **not** reconstruct bracket state on restart. No hydration logic in IEA. OrderMap and IntentMap are in-memory; they start empty on process start. Journals persist, but IEA does not read them at startup to repopulate state.

### Q: Is adoption logic invoked during rebind/hydration or only during HandleEntryFill?

**Answer:** Only during `HandleEntryFill`. Adoption is reactive: when an execution update indicates an entry fill, we check for existing protectives and adopt if they match. No proactive adoption on rebind/hydration.

**Risk:** After restart with existing working protectives, we rely on NT to send execution callbacks. If NT replays them, adoption works. If not, we might try to submit protectives again (duplicate) or miss adoption.

**Additional gap:** `Flatten` can be called from **outside** the queue: `StreamStateMachine.HandleForcedFlatten` and `RobotEngine.FlattenIntent` (e.g. forced flatten, kill switch). These run on engine/strategy threads and can race with the worker. Flatten is a broker mutation.

---

## 10. Observability

### Q: Do we log queue sequence numbers and enqueue/dequeue times?

**Answer:** No. No sequence numbers. `IEA_HEARTBEAT` logs `queue_depth` and `last_mutation_utc`, but not per-item sequence or enqueue time.

### Q: Does every CRITICAL execution event include iea_instance_id and execution_instrument_key?

**Answer:** Most IEA-originated events include them (e.g. `PROTECTIVE_MISMATCH_FAIL_CLOSE`, `BE_TRIGGER_REACHED`, `IEA_HEARTBEAT`). Adapter-originated events (e.g. `ORDER_REJECTED`, `PROTECTIVE_ORDER_REJECTED_FLATTENED`) may not always include `iea_instance_id` — they are logged from the adapter, which may not have IEA context in all code paths. Partial coverage.

---

## Summary: Gaps and Recommendations

| # | Gap | Severity | Recommendation |
|---|-----|----------|----------------|
| 1 | Entry order submission (TryAggregateWithExistingOrders) runs outside queue | Medium | Consider enqueueing entry submission for full serialization, or document that aggregation races are acceptable |
| 2 | Adoption does not validate quantity | Medium | Add quantity check; if mismatch, fail-close or resize |
| 3 | No execution-ID deduplication | Low | Adoption mitigates; optional: add execution ID tracking |
| 4 | No IEA hydration on restart | Medium | Consider journal-based bracket state reconstruction |
| 5 | No queue sequence numbers | Low | Add for debugging multi-instrument incidents |
| 6 | Some CRITICAL events may lack iea_instance_id | Low | Audit adapter logging paths; add context where missing |
| 7 | Flatten from HandleForcedFlatten/engine bypasses queue | Medium | Enqueue flatten when IEA enabled, or document as intentional emergency path |
