# IEA Audit Gaps — Fix Plan (Revised)

Plan to fix the 7 structural gaps from [IEA_AUDIT_ANSWERS.md](IEA_AUDIT_ANSWERS.md), incorporating critical review feedback.

---

## Implementation Order (Revised)

1. **Gap 3** (execution dedup) — protects journal integrity first
2. **Gap 1** (entry enqueue) — requires `EnqueueAndWait`
3. **Gap 7** (flatten enqueue) — reuses `EnqueueAndWait`
4. **Gap 2** (adoption quantity) — localized to adoption path
5. **Gap 4** (hydration scan) — ledger completeness
6. **Gap 5 + 6** (observability) — sequence numbers, CRITICAL context

---

## Gap 1: Entry Order Submission Outside Queue (Medium)

**Problem:** `TryAggregateWithExistingOrders` and `SubmitStopEntryOrder` run on caller thread, not the queue.

**Solution:** Add `EnqueueAndWait<T>(Func<T> work)` to IEA. Route entry submission through it when IEA enabled.

**Critical Invariants:**

- **Deadlock guard:** `EnqueueAndWait` must NEVER be called from inside the worker thread. If `Thread.CurrentThread == _workerThread`, execute `work()` inline and return. Without this, any future refactor will deadlock.
- **Fail-fast:** If worker is dead, queue stopped, or cancellation triggered, `EnqueueAndWait` must return failure within bounded time (timeout 5–10s), not hang.
- **Queue depth threshold:** If `QueueDepth > MAX_QUEUE_DEPTH_FOR_ENQUEUE` (e.g. 500), enter fail-closed mode — do not enqueue. Return failure. Institutional systems fail closed when worker is overloaded.

**Changes:**

1. **InstrumentExecutionAuthority.cs**
   - Add `EnqueueAndWait<T>(Func<T> work, int timeoutMs = 5000)` using `TaskCompletionSource<T>` or `ManualResetEvent` + shared result.
   - At entry: if `Thread.CurrentThread == _workerThread`, execute `work()` inline and return.
   - Before enqueue: if `QueueDepth > 500`, return failure immediately.
   - Worker runs `work()`, captures result, signals. Caller blocks with timeout. On timeout: return failure, log `IEA_ENQUEUE_AND_WAIT_TIMEOUT`.
   - Add `_workerThread` reference for thread identity check.
2. **InstrumentExecutionAuthority.NT.cs**
   - Add `SubmitStopEntryOrderEnqueued(...)` that wraps existing logic. Public `SubmitStopEntryOrder` calls `EnqueueAndWait(() => SubmitStopEntryOrderCore(...))` when invoked from outside worker.
3. **NinjaTraderSimAdapter.NT.cs**
   - In `SubmitStopEntryOrderReal`, when IEA enabled: call `_iea.EnqueueAndWait(() => _iea.SubmitStopEntryOrder(...))` instead of direct call.

---

## Gap 2: Adoption Does Not Validate Quantity (Medium)

**Problem:** Adoption checks prices only. Wrong quantity is adopted without validation.

**Solution:** Extend adoption to validate quantity; fail-close on mismatch.

**Documented Invariant:** Quantity mismatch after restart → fail-close (not resize). Risk coverage is ambiguous when protective qty differs from journal filled qty; institutional approach is to flatten. Document explicitly so future changes do not "fix" it to resize.

**Changes:**

1. **IIEAOrderExecutor.cs**
   - Extend `GetWorkingProtectivePrices` to `GetWorkingProtectiveState(string intentId)` returning `(decimal? stopPrice, decimal? targetPrice, int? stopQty, int? targetQty)`.
2. **NinjaTraderSimAdapter.NT.cs**
   - Implement `GetWorkingProtectiveState` by reading `Quantity` from stop/target orders.
3. **InstrumentExecutionAuthority.NT.cs**
   - In `HandleEntryFill` adoption path: after price check, compare `stopQty` and `targetQty` to `totalFilledQuantity`. If either differs: fail-close with `PROTECTIVE_QUANTITY_MISMATCH_FAIL_CLOSE`. If quantity cannot be read: adopt conservatively.
   - Add comment: "Quantity mismatch after restart → fail-close. Do not resize during adoption."

---

## Gap 3: No Execution-ID Deduplication (Low)

**Problem:** Duplicate execution callbacks can double-count in journal. Non-negotiable for forensic correctness.

**Solution:** Track processed execution IDs; skip duplicates before any state mutation. Dedup must occur inside worker, before any journal mutation or allocation.

**Dedup Key Logic (document exactly):**

- Primary: `Execution.ExecutionId` (string)
- Fallback when ExecutionId null/empty: composite for maximum entropy — `order.OrderId + "|" + execution.Time.Ticks + "|" + execution.Quantity + "|" + execution.MarketPosition + "|" + execution.OrderId`. Avoid `orderId+price+qty+time` — can collide for legitimate separate fills.

**Eviction (avoid O(n) per execution):**

- Use `ConcurrentDictionary<string, DateTimeOffset>` keyed by dedup key, value = first-seen UTC.
- Periodic sweep every 100 inserts (increment `_dedupInsertCount`, when % 100 == 0 run eviction). Remove entries older than 5 minutes. Do NOT iterate full dictionary on every check.

**Changes:**

1. **InstrumentExecutionAuthority.cs**
   - Add `_processedExecutionIds`, `_dedupInsertCount`, eviction logic.
2. **InstrumentExecutionAuthority.NT.cs**
   - Add `bool TryMarkAndCheckDuplicate(object execution, object order) -> bool`. Returns true if duplicate (caller skip). Returns false and marks if new. Implements key logic and eviction.
3. **NinjaTraderSimAdapter.NT.cs**
   - At very start of `HandleExecutionUpdateReal`, before any mutation: call `_iea?.TryMarkAndCheckDuplicate(execution, order)`. If true, return early (log `EXECUTION_DUPLICATE_SKIPPED` at DEBUG).

---

## Gap 4: No IEA Hydration on Restart (Medium)

**Problem:** On restart, IEA ledger (GetPositionState, GetBracketState) is inaccurate until next execution event.

**Solution:** Minimal, deterministic hydration scan. Real value: make IEA internal ledger consistent with broker reality immediately after restart.

**Design Rules:**

- Hydration must NOT mutate broker state. It only reconstructs ledger.
- Do NOT attempt to infer entry orders. Do NOT attempt to re-aggregate.
- Just sync protective presence: populate OrderMap with protective orderId + qty + price.

**Changes:**

1. **InstrumentExecutionAuthority.NT.cs**
   - Add `_hasScannedForAdoption` flag. Add `ScanAndAdoptExistingProtectives()`:
     - On first worker execution (before first ProcessExecutionUpdate): iterate `Executor.GetAccount().Orders`
     - For each QTSW2 protective (tags `*:STOP` or `*:TARGET`): extract intentId
     - If journal says entry filled (use `GetActiveIntentsForBEMonitoring` — intent in list = filled + no BE)
     - Populate OrderMap with protective orderId, qty, price (OrderInfo for stop/target)
   - Run once per IEA lifetime, before first execution update processing.

---

## Gap 5: No Queue Sequence Numbers (Low)

**Solution:** Add monotonic sequence counter. Include in CRITICAL, HEARTBEAT, and worker exception logs.

**Changes:**

1. **InstrumentExecutionAuthority.cs**
   - Add `long _enqueueSequence`, `_lastProcessedSequence`. Increment on Enqueue; update when work completes.
2. **InstrumentExecutionAuthority.cs**
   - In `EmitHeartbeatIfDue`: add `enqueue_sequence`, `last_processed_sequence` to payload.
3. **InstrumentExecutionAuthority.cs**
   - In `IEA_QUEUE_WORKER_ERROR` log: include sequence numbers.
4. When logging CRITICAL events from IEA: include sequence numbers.

---

## Gap 6: CRITICAL Events Lack iea_instance_id (Low)

**Solution:** Add invariant: No CRITICAL event is emitted without `iea_instance_id` when IEA enabled. Add a small wrapper to enforce it centrally.

**Changes:**

1. **NinjaTraderSimAdapter.NT.cs**
   - Add `LogCriticalWithIeaContext(eventType, data)` that, when `_iea != null`, always merges `iea_instance_id` and `execution_instrument_key` into `data`. Use for all CRITICAL/ERROR execution events.
2. Audit all CRITICAL/ERROR `_log.Write` calls: `PROTECTIVE_ORDER_REJECTED`, `ORPHAN_FILL`, `UNTrackED_FILL`, `UNKNOWN_ORDER_FILL`, `INTENT_NOT_FOUND`, `FLATTEN`-related. Route through wrapper when IEA enabled.

---

## Gap 7: Flatten from HandleForcedFlatten Bypasses Queue (Medium)

**Problem:** Flatten runs on engine/strategy threads, can race with worker.

**Solution:** When IEA enabled, route flatten through the queue. Architectural purity: flatten serialized. No emergency override path — if worker is hung, flatten waits (or times out). Document: "Emergency flatten may violate serialization invariant" if we add override later; for now, recommend A (purity).

**Design Rule:** Flatten must execute after current mutation completes. Queue gives this. If flatten requested while fill processing or bracket resize in progress, flatten runs after.

**Changes:**

1. **InstrumentExecutionAuthority.cs**
   - Add `FlattenResult EnqueueFlattenAndWait(string intentId, string instrument, int timeoutMs = 10000)`. Uses same `EnqueueAndWait` pattern as Gap 1. Enqueues `() => Executor.Flatten(...)`, blocks until result.
2. **NinjaTraderSimAdapter.cs**
   - In `Flatten` and `FlattenIntent`, when `_useInstrumentExecutionAuthority && _iea != null`: call `_iea.EnqueueFlattenAndWait(intentId, instrument)` instead of `FlattenWithRetry` directly.

---

## EnqueueAndWait: Blocking vs Fail-Fast

**Decision:** Fail fast if worker queue length exceeds threshold. If `QueueDepth > N`, do not enqueue — return failure. Prevents blocking OnBarUpdate indefinitely when worker is stuck. Institutional approach: enter fail-closed mode when queue backs up.

---

## Post-Implementation Invariants

After implementing:

- One mutation lane per instrument
- No broker mutation off-queue
- No duplicate fill accounting
- Deterministic restart (ledger accurate)
- No split authority

IEA becomes a real execution authority, not just race mitigation.
