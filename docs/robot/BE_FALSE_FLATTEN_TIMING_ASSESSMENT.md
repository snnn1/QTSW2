# BE False Flatten Timing Assessment (IEA)

**Date:** 2026-02-18  
**Scope:** Facts only. No fixes. No refactoring.

---

## 1. Latency Chain After Entry Fill (IEA Path)

| Stage | Description | Current Logging | Event | File:Line |
|-------|-------------|-----------------|-------|-----------|
| **(a) Entry fill time** | Execution update processed; journal EntryFilled set | Yes | `EXECUTION_FILLED` | `NinjaTraderSimAdapter.NT.cs:2405` |
| | | | `ts_utc` in payload (via `RobotEvents.ExecutionBase(utcNow, ...)`) | `RobotLogger.cs:442` |
| **(b) NtSubmitProtectivesCommand enqueued** | IEA HandleEntryFill → EnqueueNtAction(cmd) | Yes | `NT_ACTION_ENQUEUED` | `StrategyThreadExecutor.cs:213` |
| | | | `action_type=SUBMIT_PROTECTIVES`, `intent_id`, `correlation_id`, `ts_utc` | |
| **(c) NtSubmitProtectivesCommand executed** | DrainNtActions runs ExecuteSubmitProtectives | Yes | `NT_ACTION_START` | `StrategyThreadExecutor.cs:238` |
| | | | `NT_ACTION_SUCCESS` | `StrategyThreadExecutor.cs:249` |
| | | | Same `correlation_id`, `intent_id` | |
| **(d) Stop visible in account.Orders** | Stop found by tag lookup in ModifyStopToBreakEvenReal | **No explicit event** | — | — |
| | Best proxy: when Submit returns | Yes | `ORDER_SUBMIT_SUCCESS` with `order_type=PROTECTIVE_STOP` | `NinjaTraderSimAdapter.NT.cs:3069` |
| | Alternative: when OrderUpdate fires for STOP | Yes | `BE_PREFLIGHT_STOP_ORDER_UPDATE` | `NinjaTraderSimAdapter.NT.cs:1239` |

**Missing instrumentation:** No event that explicitly marks "stop found in account.Orders by tag lookup." The first successful BE attempt logs `BE_TRIGGERED` or `BE_SKIP_STOP_ALREADY_TIGHTER`; the negative case logs `BE_MODIFY_RETRY` with `error` containing "not found".

**Latency computation from existing logs:**
- (a)→(b): `EXECUTION_FILLED` ts_utc → `NT_ACTION_ENQUEUED` ts_utc (filter `action_type=SUBMIT_PROTECTIVES`, same `intent_id`)
- (b)→(c): `NT_ACTION_ENQUEUED` ts_utc → `NT_ACTION_SUCCESS` ts_utc (same `correlation_id`)
- (c)→(d): `NT_ACTION_SUCCESS` ts_utc → `ORDER_SUBMIT_SUCCESS` ts_utc (same `intent_id`, `order_type=PROTECTIVE_STOP`)

---

## 2. BE "Stop Not Found" Behavior (IEA Enabled)

### Where is the 25-retry loop?

**Not a loop.** Retries are event-driven. Location: `NinjaTraderSimAdapter.NT.cs:3514–3599` (`EvaluateBreakEvenCoreImpl`).

Each **OnMarketData(Last)** tick:
1. `GetActiveIntentsForBEMonitoring` returns intents (journal EntryFilled, !IsBEModified, !HasPendingBEForIntent).
2. For each intent with `tickPrice >= beTriggerPrice` (Long) or `<=` (Short):
3. **Throttle:** Skip if `(utcNow - _lastBeModifyAttemptUtcByIntent[intentId]).TotalMilliseconds < 200` (`BE_MODIFY_ATTEMPT_INTERVAL_MS`).
4. Call `ModifyStopToBreakEven`.
5. If failure with "not found" or "Stop order": increment `_beModifyFailureCountByIntent[intentId]`, log `BE_MODIFY_RETRY`.
6. If `failCount >= 25`: log `BE_MODIFY_FAILED`, then `Flatten(intentId, ...)` when `stopMissing`.

### Retry trigger and max wall-clock

| Aspect | Value |
|--------|-------|
| **Retry trigger** | Ticks only. OnMarketData(Last) fires. No timer. |
| **Throttle** | 200 ms between attempts per intent (`BE_MODIFY_ATTEMPT_INTERVAL_MS`). |
| **Max attempts** | 25 (`BE_MODIFY_MAX_RETRIES`). |
| **Min wall-clock** | 25 × 200 ms = 5 s (if ticks ≥ 5/sec). |
| **Max wall-clock** | Unbounded. If ticks are sparse (e.g. 1/sec), 25 attempts ≈ 25 s. |

### After retries, does it ALWAYS flatten if stop still not found?

**Yes.** `NinjaTraderSimAdapter.NT.cs:3579–3593`:

```csharp
if (failCount >= BE_MODIFY_MAX_RETRIES)
{
    // ...
    if (stopMissing)
        Flatten(intentId, intent.Instrument ?? "", utcNow);
    else
        _standDownStreamCallback?.Invoke(...);
}
```

`stopMissing` is true when `errorMsg` contains "not found" or "Stop order" (case-insensitive). No other condition.

### Does it check "protectives pending" or "fill unresolved" before flattening?

**No.** The flatten path does not check:
- Pending `NtSubmitProtectivesCommand` in the NT action queue
- Pending unresolved execution (grace)
- Any "protectives pending" flag

### Confirmation

**BE can flatten purely because stop submission/acceptance has not completed yet.** If the stop is still in the NT action queue, or the IEA worker has not processed the entry fill, or DrainNtActions has not run, the stop will not be in `account.Orders`. BE will get "Stop order not found", count retries, and after 25 failures will flatten.

---

## 3. State BE Path Can Read (Without OrderMap)

| Source | Field/Method | Meaning | Update Thread | BE Path Access |
|--------|--------------|---------|---------------|----------------|
| **NT action queue** | `StrategyThreadExecutor.PendingCount` | Total pending actions | N/A (read-only) | Adapter has `_ntActionQueue`; `PendingCount` is public. |
| | `_pendingCorrelationIds` | Correlation IDs of pending actions | N/A | **Private.** No per-intent "protectives pending" query. |
| **Journal** | `ExecutionJournal.GetEntry(...).EntryFilled` | Entry filled | IEA worker (RecordEntryFill) | Yes, via `GetActiveIntentsForBEMonitoring`. |
| | `ExecutionJournal.IsBEModified(...)` | BE already applied | Strategy thread (RecordBEModification) | Yes. |
| | "Protectives pending" | — | — | **Does not exist.** |
| **IEA** | `OrderMap`, `IntentMap`, `IntentPolicy` | Order/intent state | IEA worker, strategy thread | BE does **not** use OrderMap for stop lookup. Uses `account.Orders`. |
| | "Protectives pending" | — | — | **Does not exist.** |
| **Adapter** | `HasPendingBEForIntent(intentId)` | Pending BE *modification* (Change) confirmation | Strategy thread | Yes. Used in `GetActiveIntentsForBEMonitoring` to skip intents with pending BE modify. **Not** "protectives submission pending." |
| | `_pendingUnresolvedExecutions` | Unresolved execution records | Non-IEA only; IEA path returns early from `ProcessPendingUnresolvedExecutions` | **Not applicable for IEA.** Unresolved retries run on IEA worker; adapter has no visibility. |

**Conclusion:** There is no state the BE path can read to know "protectives are pending for this intent." The NT queue does not expose per-intent pending actions; the journal and IEA have no such flag.

---

## 4. Deliverable: Can BE Flatten Due Only to Timing Gaps?

**Yes.** BE flatten can occur purely because of timing between fill and stop visibility.

### Evidence

1. **No guard before flatten:** `EvaluateBreakEvenCoreImpl` flattens when `failCount >= 25` and `stopMissing`. It does not check pending protective submission or unresolved execution.

2. **Retry window vs. latency:** Retry window is at least 5 s (25 × 200 ms) and can be much longer with sparse ticks. If p95 latency from entry fill to stop visibility exceeds that window (e.g. IEA queue backlog, slow DrainNtActions, NT callback delay), BE will exhaust retries and flatten.

3. **IEA deserialization:** Entry fill is processed on the IEA worker; protectives are enqueued there. DrainNtActions runs on the strategy thread in OnMarketData/OnBarUpdate. Ordering is non-deterministic. A tick can fire before the IEA worker has processed the fill, so protectives may not yet be enqueued when BE runs.

4. **No "protectives pending" signal:** The BE path cannot observe that protectives are pending. It only sees `account.Orders` and the absence of the stop.

### Evidence against (mitigating factors)

1. **DrainNtActions before BE:** OnMarketData runs `DrainNtActions` → `ProcessPendingUnresolvedExecutions` → `EvaluateBreakEven`. So any protectives already in the queue from a previous tick are drained before BE. The risk is when the fill and BE trigger occur in the same tick before the IEA worker has run.

2. **Existing logs:** `EXECUTION_FILLED`, `NT_ACTION_ENQUEUED`, `NT_ACTION_SUCCESS`, `ORDER_SUBMIT_SUCCESS` (PROTECTIVE_STOP) provide timestamps to compute fill→stop latency. p50/p95/p99 can be derived from logs; no new instrumentation is required for that.

### Summary

| Question | Answer |
|----------|--------|
| Can BE flatten due only to fill→stop timing gaps? | **Yes** |
| Does BE check "protectives pending" before flatten? | **No** |
| Does BE check "fill unresolved" before flatten? | **No** |
| Can BE path read "protectives pending"? | **No** — no such state exists |
| Evidence for false flatten risk | Code path, no guards, non-deterministic ordering |
| Evidence against | Drain-before-BE ordering; existing logs for latency analysis |
