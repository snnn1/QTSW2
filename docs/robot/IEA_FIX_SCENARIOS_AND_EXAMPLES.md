# IEA Fix: How It Addresses Key Failure Scenarios

This document outlines how the Phase 2/3 IEA changes (queue serialization + idempotency) fix the issues with protective orders not being placed, race conditions, and break-even detection across multiple streams.

---

## 1. No Limit and Stop Orders Not Being Placed

### Root cause (before fix)

- **Duplicate execution callbacks**: NinjaTrader can fire multiple execution updates for the same fill (e.g. partial fill + full fill, or reconnect/replay).
- **Double submission**: First call submits stop + target successfully. Second call tries to submit again → duplicate orders or broker rejection → `PROTECTIVE_ORDERS_FAILED_FLATTENED` → position flattened even though protectives were already working.
- **OCO conflicts**: Second submission might collide with the first OCO group, causing one leg to fail.

### How the fix addresses it

**Idempotency check before submission:**

```
HandleEntryFill(intentId, intent, ...)
  → if Executor.HasWorkingProtectivesForIntent(intentId)
       → GetWorkingProtectivePrices(intentId)
       → if prices match policy (within BracketToleranceTicks): ADOPT, return
       → if prices mismatch: FAIL-CLOSE (safety)
  → else: submit protectives as before
```

**Example:**

| Step | Before fix | After fix |
|------|-------------|-----------|
| T1 | Entry fills. NT sends execution update #1. | Same. |
| T2 | HandleEntryFill runs. Submits stop + target. Both working. | Same. |
| T3 | NT sends duplicate execution update #2 (same fill). | Same. |
| T4 | HandleEntryFill runs again. Tries to submit stop + target again. | HandleEntryFill runs again. |
| T5 | Submission fails (duplicate/OCO conflict) or creates duplicate orders. | `HasWorkingProtectivesForIntent` returns true. |
| T6 | Fail-closed → flatten position. | `GetWorkingProtectivePrices` returns existing prices. |
| T7 | Position flattened despite having working protectives. | Prices match → adopt, return. No second submission. |

**Result:** Duplicate callbacks no longer cause double submission or false fail-close. Existing protectives are adopted.

---

## 2. Race Condition: Two Streams, Same Instrument, Same Price

### Root cause (before fix)

- **Concurrent execution updates**: NT can call `OnExecutionUpdate` from different threads. Two streams (e.g. Stream A and Stream B) both have entry fills on MNQ at the same price.
- **Interleaved mutations**: Thread 1 processes Stream A fill → starts submitting protectives. Thread 2 processes Stream B fill → starts submitting protectives. Both touch `OrderMap`, `IntentMap`, and broker state.
- **Non-deterministic outcome**: One stream’s protectives might overwrite or conflict with the other’s; OCO groups can collide; one submission can fail due to the other’s in-flight state.

### How the fix addresses it

**Per-instrument queue serialization:**

All broker-state mutations for an instrument go through a single queue:

```
OnExecutionUpdate (any thread)
  → IEA.EnqueueExecutionUpdate(execution, order)
  → _executionQueue.Add(() => ProcessExecutionUpdate(...))

Worker thread (single thread per IEA instance):
  → Take work from queue (one at a time)
  → ProcessExecutionUpdate → HandleExecutionUpdateReal
  → On entry fill: HandleEntryFill
```

**Example:**

| Step | Before fix | After fix |
|------|-------------|-----------|
| T1 | Stream A entry fills on MNQ. NT calls OnExecutionUpdate. | Same. |
| T2 | Stream B entry fills on MNQ. NT calls OnExecutionUpdate. | Same. |
| T3 | Thread 1: HandleExecutionUpdateReal for A → HandleEntryFill(A). | Both updates enqueued. |
| T4 | Thread 2: HandleExecutionUpdateReal for B → HandleEntryFill(B). | Worker takes update #1 (A). |
| T5 | Both submit protectives concurrently. Possible OCO/order conflicts. | HandleEntryFill(A) runs. Submits protectives for A. |
| T6 | One or both may fail; state may be inconsistent. | Worker takes update #2 (B). |
| T7 | | HandleEntryFill(B) runs. Submits protectives for B (or adopts if aggregated). |

**Result:** No concurrent mutations. One fill is fully processed before the next. Order of processing is deterministic (queue order).

**Note:** If A and B are aggregated (same bracket policy), they may share one entry order. In that case, there is a single fill event and one HandleEntryFill for the primary intent with total quantity.

---

## 3. Break-Even Detection: Two Streams, Same Instrument, Different Price

### Root cause (before fix)

- **Concurrent BE evaluation**: OnMarketData fires on every tick. Multiple streams with different entry prices (e.g. Stream A @ 5000, Stream B @ 5010) both need BE.
- **Race with fills**: BE evaluation can run while an execution update is still being processed.
- **Wrong intent modified**: BE logic might pick the wrong intent’s stop or use stale state.
- **Duplicate BE moves**: Same tick could trigger BE for multiple intents, causing repeated or conflicting stop modifications.

### How the fix addresses it

**Queue serialization (BE and fills share the same queue):**

```
OnMarketData(tick)
  → adapter.EvaluateBreakEven(tickPrice, tickTime, instrument)
  → IEA.EvaluateBreakEven(...)
  → Enqueue(() => EvaluateBreakEvenCore(...))

Worker thread:
  → Processes queue: [ExecUpdate1, ExecUpdate2, BECheck1, BECheck2, ...]
  → One at a time: fill processing and BE never run concurrently
```

**Tick de-duplication:**

```
EvaluateBreakEvenCore(tickPrice, eventTime, ...)
  → if (tickPrice == _lastTickPriceForBE && eventTime == _lastTickTimeFromEvent) return;
  → or: 50ms fallback window when event timestamp unavailable
```

**Example:**

| Step | Before fix | After fix |
|------|-------------|-----------|
| T1 | Stream A: entry 5000, BE trigger 5010. Stream B: entry 5010, BE trigger 5020. | Same. |
| T2 | Tick 5015 arrives. Both intents’ triggers reached (5015 >= 5010 and 5015 >= 5020? No – 5015 < 5020). Only A’s trigger reached. | Same. |
| T3 | BE logic runs (possibly concurrent with fill processing). | BE work enqueued. |
| T4 | ModifyStopToBreakEven(A) runs. May race with fill for B. | Worker processes BE. No fill processing at same time. |
| T5 | If B’s fill was in flight, stop might be wrong size or wrong intent. | BE runs after all pending fills for that instrument. |
| T6 | Duplicate ticks could cause multiple BE modifications. | Tick de-dupe skips identical (price, time) ticks. |

**Result:** BE evaluation is serialized with fills. No races between “move stop to BE” and “new fill changes position.” Tick de-dupe avoids redundant BE work.

**Two streams, different prices – BE semantics:**

- Each intent has its own `beTriggerPrice` and `entryPrice`.
- `EvaluateBreakEvenCore` iterates over `GetActiveIntentsForBEMonitoring` and checks each intent’s trigger.
- First intent whose trigger is reached gets `ModifyStopToBreakEven` called.
- For aggregated positions (one physical stop), the first trigger moves the shared stop; subsequent intents may already be covered by that move.
- Per-intent throttling (`BE_MODIFY_ATTEMPT_INTERVAL_MS`) limits how often we try to modify each intent’s stop.

---

## Summary Table

| Issue | Mechanism | Effect |
|-------|-----------|--------|
| No limit/stop placed (duplicate callbacks) | Idempotency: `HasWorkingProtectivesForIntent` + adopt | Second callback adopts existing protectives; no double submit or false fail-close |
| Race: 2 streams, same instrument, same price | Per-instrument queue | Fills processed one at a time; no concurrent protective submission |
| BE: 2 streams, same instrument, different price | Queue + tick de-dupe | BE and fills serialized; no BE/fill race; no duplicate BE moves on same tick |

---

## Related Code Paths

- **Queue:** `InstrumentExecutionAuthority._executionQueue`, `Enqueue()`, `EnqueueExecutionUpdate()`
- **Idempotency:** `HandleEntryFill` → `HasWorkingProtectivesForIntent`, `GetWorkingProtectivePrices`
- **BE serialization:** `EvaluateBreakEven` → `Enqueue(() => EvaluateBreakEvenCore(...))`
- **Tick de-dupe:** `_lastTickPriceForBE`, `_lastTickTimeFromEvent`, 50ms fallback in `EvaluateBreakEvenCore`
