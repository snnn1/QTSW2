# IEA Invariants Verification

Verification of core invariants against the current implementation.

---

## 1) Invariant A — Single Mutation Lane Per Instrument

### What must be true
Entry submission, aggregation, protectives, BE, and flatten all execute on the IEA worker when IEA enabled.

### Verification

**Broker mutation paths when IEA enabled:**

| Path | Routing | Queue? |
|------|---------|--------|
| **Execution updates** (fills, partials) | `HandleExecutionUpdate` → `_iea.EnqueueExecutionUpdate` → `Enqueue` → worker runs `ProcessExecutionUpdate` → `HandleExecutionUpdateReal` | Yes |
| **BE evaluation** | `EvaluateBreakEven` → `Enqueue` → worker runs `EvaluateBreakEvenCore` | Yes |
| **Entry submission (aggregation)** | `SubmitStopEntryOrderReal` → `EnqueueAndWait` → worker runs `SubmitStopEntryOrder` (IEA) | Yes |
| **Entry submission (single-order fallback)** | When IEA returns null, adapter falls through to single-order path. **This runs on caller thread** — not on worker. | **No** |
| **Flatten** | `FlattenWithRetry` → `EnqueueFlattenAndWait` → worker runs `FlattenWithRetryCore` | Yes |
| **Protective submission** | Via `HandleEntryFill` (called from `HandleExecutionUpdateReal` on worker) | Yes |

**Fixed:** When IEA has no aggregation candidates, `SubmitStopEntryOrder` returns null. The adapter's `EnqueueAndWait` lambda then calls `SubmitSingleEntryOrderCore` on the worker. Single-order fallback runs on IEA worker (Gap 1 fixed).

**OrderUpdate:** `HandleOrderUpdate` is not routed through IEA. Order state changes (rejections, etc.) are processed on the strategy thread. The adapter's `HandleOrderUpdateReal` may mutate shared state. Flatten (when protective rejected) is routed through `FlattenWithRetry` → `EnqueueFlattenAndWait`, so the actual flatten runs on the worker.

---

## 2) Invariant B — EnqueueAndWait Safety

### Verification

**Deadlock guard:** `InstrumentExecutionAuthority.cs` lines 152–153:
```csharp
if (Thread.CurrentThread == _workerThread)
    return (true, work());
```
Confirmed: inline execution when called from worker thread.

**Timeout:** Lines 167–168:
```csharp
if (!done.Wait(timeoutMs))
{
    Log?.Write(RobotEvents.EngineBase(..., eventType: "IEA_ENQUEUE_AND_WAIT_TIMEOUT", state: "ENGINE",
        new { iea_instance_id = InstanceId, execution_instrument_key = ExecutionInstrumentKey, timeout_ms = timeoutMs }));
    return (false, default);
}
```
Confirmed: timeout returns `(false, default)` and emits `IEA_ENQUEUE_AND_WAIT_TIMEOUT` with `iea_instance_id` and `execution_instrument_key`. Note: `enqueue_sequence` and `last_processed_sequence` are not included in this log.

**Queue depth threshold:** Lines 146–149:
```csharp
if (QueueDepth > MAX_QUEUE_DEPTH_FOR_ENQUEUE)
{
    Log?.Write(..., eventType: "IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW", ...);
    return (false, default);
}
```
Confirmed: returns failure immediately when queue depth > 500.

---

## 3) Invariant C — Duplicate Execution Callbacks

### Verification

**Call order in `HandleExecutionUpdateReal`** (`NinjaTraderSimAdapter.NT.cs` lines 1633–1642):

1. `TryMarkAndCheckDuplicate` — first (line 1634)
2. If duplicate: return early (line 1638)
3. `encodedTag`, `intentId`, `fillPrice`, `fillQuantity` — after dedup
4. Journal writes / allocation / allocation logic — later in the method

Confirmed: `TryMarkAndCheckDuplicate` runs before any journal mutation, allocation, or bracket logic. Duplicate callbacks log `EXECUTION_DUPLICATE_SKIPPED` and return.

---

## 4) Invariant D — Adoption Path Correctness

### Verification

**Both legs required:** `HasWorkingProtectivesForIntent` (`NinjaTraderSimAdapter.NT.cs` lines 119–137):
```csharp
return hasStop && hasTarget;
```
Confirmed: `true` only when both stop and target exist and are Working/Accepted.

**Order state:** `OrderState.Working` or `OrderState.Accepted` used for both stop and target. Confirmed.

**Fail-close on mismatch:** `HandleEntryFill` adoption path:
- `PROTECTIVE_QUANTITY_MISMATCH_FAIL_CLOSE` when `stopQty` or `targetQty` != `totalFilledQuantity`
- `PROTECTIVE_MISMATCH_FAIL_CLOSE` when prices differ beyond tolerance
- Logs include `iea_instance_id` (via `InstanceId` in IEA logs)

---

## 5) Invariant E — Flatten Serialization

### Verification

**FlattenWithRetry call sites:**

| Call site | IEA routing |
|-----------|--------------|
| `FailClosed` (line 795) | `FlattenWithRetry` → when IEA: `EnqueueFlattenAndWait` |
| `FlattenIntent` (line 1782) | `FlattenWithRetry` → when IEA: `EnqueueFlattenAndWait` |
| `TriggerQuantityEmergency` (line 1586) | `FlattenWithRetry` → when IEA: `EnqueueFlattenAndWait` |
| Protective rejection in `HandleOrderUpdateReal` (line 1288) | `FlattenWithRetry` → when IEA: `EnqueueFlattenAndWait` |
| `HandleExecutionUpdateReal` (untracked fill, unknown order) | Calls `Flatten` directly → `FlattenWithRetry` → when IEA: `EnqueueFlattenAndWait` |

Confirmed: all `FlattenWithRetry` paths go through `EnqueueFlattenAndWait` when IEA is enabled.

---

## 6) Invariant F — Hydration Scan Runs Once

### Verification

**Guard logic** (`InstrumentExecutionAuthority.NT.cs` lines 502–510):

```csharp
Enqueue(() =>
{
    if (!_hasScannedForAdoption)
    {
        _hasScannedForAdoption = true;
        ScanAndAdoptExistingProtectives();
    }
    Executor.ProcessExecutionUpdate(execution, order);
});
```

Confirmed: scan runs once before the first execution update is processed. `_hasScannedForAdoption` ensures it only runs once per IEA lifetime.

---

## 7) Invariant G — Scan Cannot Adopt Non-QTSW2 Orders

### Verification

**Tag parsing** (`ScanAndAdoptExistingProtectives()` lines 528–535):

```csharp
var tag = Executor.GetOrderTag(o);
if (string.IsNullOrEmpty(tag) || !tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) continue;
var intentId = RobotOrderIds.DecodeIntentId(tag);
if (string.IsNullOrEmpty(intentId) || !activeIntentIds.Contains(intentId)) continue;
var isStop = tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase);
var isTarget = tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase);
if (!isStop && !isTarget) continue;
```

Confirmed:
- `tag.StartsWith("QTSW2:")` — rejects non-QTSW2 orders
- `DecodeIntentId` returns null for malformed tags
- `!activeIntentIds.Contains(intentId)` — rejects intents not in journal
- `isStop || isTarget` — only STOP/TARGET protective orders

**Note:** `DecodeIntentId` returns null for tags without `QTSW2:` prefix. No diagnostic log for malformed tags; adoption is skipped.

---

## 8) Observability — CRITICAL Events

### Verification

**Context injectors:** `LogCriticalWithIeaContext` and `LogCriticalEngineWithIeaContext` merge `iea_instance_id` and `execution_instrument_key` when `_iea != null`.

**Gap:** `FailClosed` (line 841) uses `_log.Write` directly with `logData`. It does not use `LogCriticalWithIeaContext`, so `iea_instance_id` and `execution_instrument_key` are not added when IEA is enabled.

**IEA-originated logs:** `IEA_ENQUEUE_AND_WAIT_TIMEOUT` and `IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW` include `iea_instance_id` and `execution_instrument_key`. They do not include `enqueue_sequence` or `last_processed_sequence`.

---

## 9) EnqueueAndWait Failure Policy

### Current behavior

**On timeout or queue overflow:**
- Returns `(false, default)` to caller
- Logs `IEA_ENQUEUE_AND_WAIT_TIMEOUT` or `IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW`

**Caller behavior:**

| Caller | On failure |
|--------|------------|
| `SubmitStopEntryOrderReal` | Returns `OrderSubmissionResult.FailureResult(...)`. No explicit stand-down or instrument block. |
| `EnqueueFlattenAndWait` (used by `FlattenWithRetry`) | Returns `FlattenResult.FailureResult(...)`. Caller (e.g. `FailClosed`) may have already stood down the stream. |

**Policy not defined:** There is no explicit policy for “stand down instrument only” vs “flatten + global engine halt” when `EnqueueAndWait` fails. The behavior is implicit: return failure and let the caller handle it. No deterministic fail-closed action is documented.

---

## Summary of Gaps

1. ~~**Single-order fallback:**~~ **Fixed.** Single-order path now runs on IEA worker via `EnqueueAndWait` → `SubmitSingleEntryOrderCore`.
2. **FailClosed log:** Does not use `LogCriticalWithIeaContext`; missing `iea_instance_id` when IEA enabled.
3. **Timeout/overflow logs:** `IEA_ENQUEUE_AND_WAIT_TIMEOUT` and `IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW` do not include `enqueue_sequence` or `last_processed_sequence`.
4. **EnqueueAndWait failure policy:** Not explicitly documented; no defined stand-down or halt behavior.
