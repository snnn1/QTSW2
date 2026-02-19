# IEA Runtime Deconstruction for Replay Design

**Purpose:** Describe EXACTLY how Instrument Execution Authority (IEA) behaves at runtime for deterministic replay harness design. No design opinions or improvements—only what the code currently does.

---

## 1. Authority Scope

### Where is IEA instantiated?

- **Location:** `InstrumentExecutionAuthorityRegistry.GetOrCreate(accountName, executionInstrumentKey, factory)`
- **Caller:** `NinjaTraderSimAdapter.SetNTContext()` when `_useInstrumentExecutionAuthority` is true
- **Factory:** `() => new InstrumentExecutionAuthority(accountName, executionInstrumentKey, this, _log, _aggregationPolicy)` where `this` is the adapter (IIEAOrderExecutor)

### Exact registry key

- **Type:** `(string Account, string ExecutionInstrumentKey)` tuple
- **Account:** From `GetAccountName(account)` — NT account name (e.g., "Sim101")
- **ExecutionInstrumentKey:** From `ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(accountName, instrument, engineExecutionInstrument)` — trimmed, uppercased (e.g., "MNQ", "MGC")
- **Format:** `(accountName ?? "", (executionInstrumentKey ?? "").Trim().ToUpperInvariant())`
- **Invariant:** MNQ and NQ are distinct keys; no cross-authority merge

### When is binding performed?

- **Trigger:** `SetNTContext(account, instrument, engineExecutionInstrument)` called by `RobotSimStrategy` after NT context is available
- **Order:** After `SetUseInstrumentExecutionAuthority(true)` and `SetAggregationPolicy()` (both called by RobotEngine before SetNTContext)
- **One-time per (account, instrument):** `GetOrAdd` semantics — first call creates, subsequent calls return existing

### What callbacks are wired at bind time?

- **`_onEnqueueFailureCallback`:** Set via `_iea.SetOnEnqueueFailureCallback(_blockInstrumentCallback)` — invoked when `EnqueueAndWait` fails (timeout or queue overflow)
- **`_blockInstrumentCallback`:** From RobotEngine: `(instrument, now, reason) => { FlattenEmergency(instrument, now); StandDownStreamsForInstrument(instrument, now, reason); ReportCritical(...) }`

### What dependencies does IEA hold references to?

| Dependency | Type | Purpose |
|------------|------|---------|
| `Executor` | `IIEAOrderExecutor?` | Adapter; all broker ops (CreateOrder, CancelOrders, SubmitOrders, SubmitProtectiveStop, SubmitTargetOrder, ModifyStopToBreakEven, Flatten, etc.) |
| `Log` | `RobotLogger?` | Audit events |
| `AggregationPolicy` | `AggregationPolicy?` | Bracket tolerance ticks, aggregation defaults |
| `OrderMap` | `ConcurrentDictionary<string, OrderInfo>` | intentId → OrderInfo (shared) |
| `IntentMap` | `ConcurrentDictionary<string, Intent>` | intentId → Intent (shared) |
| `IntentPolicy` | `Dictionary<string, IntentPolicyExpectation>` | intentId → policy (shared) |

---

## 2. All Public Entry Points Into IEA

### EnqueueExecutionUpdate

| Attribute | Value |
|-----------|-------|
| **Who calls** | `NinjaTraderSimAdapter.HandleExecutionUpdate()` |
| **Caller thread** | NinjaTrader event thread (Account.ExecutionUpdate callback) |
| **Enqueue or inline** | Enqueues via `Enqueue()` |
| **Blocks** | No |
| **Mutates state** | Indirectly: work runs on worker; `ProcessExecutionUpdate` mutates OrderMap, triggers HandleEntryFill, etc. |
| **Calls executor** | Yes, via `Executor.ProcessExecutionUpdate(execution, order)` on worker |
| **Special** | First execution update triggers `ScanAndAdoptExistingProtectives()` before `ProcessExecutionUpdate` |

### EnqueueOrderUpdate

| Attribute | Value |
|-----------|-------|
| **Who calls** | `NinjaTraderSimAdapter.HandleOrderUpdate()` |
| **Caller thread** | NinjaTrader event thread (Account.OrderUpdate callback) |
| **Enqueue or inline** | Enqueues via `Enqueue()` |
| **Blocks** | No |
| **Mutates state** | Indirectly: `ProcessOrderUpdate` mutates OrderMap on worker |
| **Calls executor** | Yes, via `Executor.ProcessOrderUpdate(order, orderUpdate)` on worker |

### EvaluateBreakEven

| Attribute | Value |
|-----------|-------|
| **Who calls** | `NinjaTraderSimAdapter.EvaluateBreakEven()` ← `RobotSimStrategy.OnMarketData()` |
| **Caller thread** | NinjaTrader tick thread |
| **Enqueue or inline** | Enqueues via `Enqueue(() => EvaluateBreakEvenCore(...))` |
| **Blocks** | No |
| **Mutates state** | Yes: `_lastTickPriceForBE`, `_lastTickTimeFromEvent`, `_lastBeCheckUtc`, `_lastBeModifyAttemptUtcByIntent`, `_beModifyFailureCountByIntent`, `_beTriggerReachedPendingModification`; may call `Executor.ModifyStopToBreakEven`, `Executor.Flatten`, `Executor.StandDownStream` |
| **Calls executor** | Yes: `GetActiveIntentsForBEMonitoring`, `ModifyStopToBreakEven`, `Flatten`, `StandDownStream` |

### SubmitStopEntryOrder

| Attribute | Value |
|-----------|-------|
| **Who calls** | Inside `EnqueueAndWait` lambda from `NinjaTraderSimAdapter.SubmitStopEntryOrderReal()` |
| **Caller thread** | IEA worker (via EnqueueAndWait) |
| **Enqueue or inline** | Runs inline on worker (called from EnqueueAndWait work item) |
| **Blocks** | No (worker already owns the lane) |
| **Mutates state** | Yes: OrderMap (aggregation path), IntentMap iteration |
| **Calls executor** | Yes: `CancelOrders`, `CreateStopMarketOrder`, `SubmitOrders`, `RecordSubmission`, `GetOrderId`, etc. |
| **Returns** | `OrderSubmissionResult?` — null means caller does single-order fallback |

### EnqueueAndWait

| Attribute | Value |
|-----------|-------|
| **Who calls** | `NinjaTraderSimAdapter.SubmitStopEntryOrderReal()` (entry submission), `EnqueueFlattenAndWait` (flatten) |
| **Caller thread** | StreamStateMachine / engine (entry), FlattenIntent path (flatten) — typically NT event or timer thread |
| **Enqueue or inline** | Enqueues work; caller blocks on `ManualResetEventSlim.Wait(timeoutMs)` |
| **Blocks** | Yes — caller blocks up to timeout (5000ms default, 12000ms for entry, 10000ms for flatten) |
| **Mutates state** | May set `_instrumentBlocked` on timeout/overflow; work item mutates per work type |
| **Calls executor** | Via work lambda (e.g., SubmitStopEntryOrder, FlattenWithRetryCore) |
| **Deadlock guard** | If `Thread.CurrentThread == _workerThread`, runs work inline and returns immediately |
| **Fail-closed** | On timeout or queue overflow: sets `_instrumentBlocked = true`, invokes `_onEnqueueFailureCallback`, returns `(false, default)` |

### EnqueueFlattenAndWait

| Attribute | Value |
|-----------|-------|
| **Who calls** | `NinjaTraderSimAdapter.FlattenWithRetry()` when IEA enabled |
| **Caller thread** | FlattenIntent path (coordinator, stream state machine, or fail-closed handlers) |
| **Enqueue or inline** | Wraps `EnqueueAndWait(work, 10000)` |
| **Blocks** | Yes — up to 10s |
| **Mutates state** | Via `FlattenWithRetryCore` → `Flatten` (broker + OrderMap) |
| **Calls executor** | Yes: `Flatten` |

### RegisterIntent

| Attribute | Value |
|-----------|-------|
| **Who calls** | `StreamStateMachine` via `ntAdapter.RegisterIntent(intent)` |
| **Caller thread** | Engine/stream thread |
| **Enqueue or inline** | Inline — writes directly to `IntentMap` (adapter's IntentMap is IEA's when IEA enabled) |
| **Blocks** | No |
| **Mutates state** | Yes: `IntentMap[intentId] = intent` |
| **On worker** | No — mutates from caller thread |

### RegisterIntentPolicy

| Attribute | Value |
|-----------|-------|
| **Who calls** | `StreamStateMachine` via `ntAdapter.RegisterIntentPolicy(...)` |
| **Caller thread** | Engine/stream thread |
| **Enqueue or inline** | Inline — writes directly to `IntentPolicy` |
| **Blocks** | No |
| **Mutates state** | Yes: `IntentPolicy[intentId] = ...` |
| **On worker** | No — mutates from caller thread |

### AddOrUpdateOrder

| Attribute | Value |
|-----------|-------|
| **Who calls** | Adapter (SubmitProtectiveStop, SubmitTargetOrder, SubmitSingleEntryOrderCore, etc.) when adding orders to map |
| **Caller thread** | IEA worker (when called from ProcessExecutionUpdate, HandleEntryFill, aggregation) or adapter path |
| **Enqueue or inline** | Inline |
| **Mutates state** | Yes: `OrderMap[intentId] = orderInfo` |

### TryMarkAndCheckDuplicate

| Attribute | Value |
|-----------|-------|
| **Who calls** | `NinjaTraderSimAdapter.HandleExecutionUpdateReal()` — before routing to IEA (when IEA enabled) |
| **Caller thread** | NinjaTrader event thread (before enqueue) |
| **Enqueue or inline** | Inline — dedup check happens on NT thread before enqueue |
| **Mutates state** | Yes: `_processedExecutionIds.TryAdd(key, DateTimeOffset.UtcNow)`; may trigger `EvictDedupEntries()` |
| **On worker** | No — runs on NT event thread |

### HandleEntryFill

| Attribute | Value |
|-----------|-------|
| **Who calls** | `HandleExecutionUpdateReal` (adapter) when entry fill detected — called from IEA worker via `ProcessExecutionUpdate` |
| **Caller thread** | IEA worker |
| **Enqueue or inline** | Inline (already on worker) |
| **Mutates state** | Yes: OrderMap (EntryFillTime, ProtectiveStopAcknowledged, etc.), may add stop/target OrderInfo |
| **Calls executor** | Yes: `SubmitProtectiveStop`, `SubmitTargetOrder`, `FailClosed`, `Flatten`, `HasWorkingProtectivesForIntent`, `GetWorkingProtectiveState`, etc. |

---

## 3. Worker Execution Model

### Data structure backing the queue

- **Type:** `BlockingCollection<Action>`
- **Default:** Unbounded (no capacity limit in constructor)
- **Operations:** `Add(work)` for enqueue; `TryTake(out work, 1000)` for dequeue (1s timeout)

### How is the worker thread created?

- **Location:** `InstrumentExecutionAuthority` constructor
- **Code:** `_workerThread = new Thread(WorkerLoop) { IsBackground = true, Name = $"IEA-{ExecutionInstrumentKey}-{_instanceId}" }; _workerThread.Start();`
- **Start:** Immediately in constructor

### What guarantees ordering?

- **Single worker thread:** One thread drains the queue; FIFO order of `BlockingCollection`
- **Sequence numbers:** `_enqueueSequence` (Interlocked.Increment on enqueue), `_lastProcessedSequence` (set in work item finally block) — for diagnostics only, not ordering

### What happens if an exception occurs inside the worker?

- **Caught:** `catch (Exception ex)` in `WorkerLoop`
- **Behavior:** Logs `IEA_QUEUE_WORKER_ERROR` with `error`, `iea_instance_id`, `execution_instrument_key`, `enqueue_sequence`, `last_processed_sequence`
- **Continues:** Loop continues; `_workerRunning` remains true; no rethrow
- **State:** Work item may have partially executed; `_lastProcessedSequence` still updated (in finally)

### What logs are emitted during worker errors?

- **Event:** `IEA_QUEUE_WORKER_ERROR`
- **Payload:** `error`, `iea_instance_id`, `execution_instrument_key`, `enqueue_sequence`, `last_processed_sequence`

---

## 4. State Mutation Inventory

| Field | Mutated By | Always on Worker? | Read Off-Thread? |
|-------|------------|-------------------|------------------|
| `OrderMap` | AddOrUpdateOrder, HandleEntryFill, ProcessExecutionUpdate (via HandleExecutionUpdateReal), ScanAndAdoptExistingProtectives, aggregation (TryAggregateWithExistingOrders), SubmitProtectiveStop/SubmitTargetOrder (adapter) | No — aggregation and HandleEntryFill run on worker; RegisterIntent does not touch OrderMap. Adapter's Flatten/SubmitProtectiveStop run on worker when called from ProcessExecutionUpdate. | Yes — GetPositionState, GetBracketState, coordinator, adapter lookups |
| `IntentMap` | RegisterIntent (caller thread), HandleEntryFill (worker) | No — RegisterIntent from StreamStateMachine | Yes — ProcessExecutionUpdate, HandleEntryFill, GetActiveIntentsForBEMonitoring, etc. |
| `IntentPolicy` | RegisterIntentPolicy (caller thread), aggregation (worker) | No | Yes — SubmitStopEntryOrder, AllocateFillToIntents, etc. |
| `_instrumentBlocked` | EnqueueAndWait (timeout, overflow), EnqueueAndWait (reject path reads only) | No — set from caller thread when timeout/overflow | Yes — EnqueueAndWait, EnqueueFlattenAndWait |
| `_processedExecutionIds` | TryMarkAndCheckDuplicate (NT event thread), EvictDedupEntries (worker, called from TryMarkAndCheckDuplicate via Interlocked) | No — TryMarkAndCheckDuplicate on NT thread | Yes — TryMarkAndCheckDuplicate |
| `_enqueueSequence` | Enqueue (Interlocked.Increment) | No — any enqueuer | Yes — logs, diagnostics |
| `_lastProcessedSequence` | Work item finally block (Interlocked.Exchange) | Yes — worker only | Yes — logs, diagnostics |
| `_hasScannedForAdoption` | EnqueueExecutionUpdate work item (first execution update) | Yes — worker only | No |
| `_lastMutationUtc` | WorkerLoop after each work item | Yes | Yes — heartbeat, observability |
| `_lastHeartbeatUtc` | EmitHeartbeatIfDue (worker) | Yes | No |
| `_lastTickPriceForBE` | EvaluateBreakEvenCore (worker) | Yes | No |
| `_lastTickTimeFromEvent` | EvaluateBreakEvenCore (worker) | Yes | No |
| `_lastBeCheckUtc` | EvaluateBreakEvenCore (worker) | Yes | No |
| `_lastBeModifyAttemptUtcByIntent` | EvaluateBreakEvenCore (worker) | Yes | No |
| `_beModifyFailureCountByIntent` | EvaluateBreakEvenCore (worker) | Yes | No |
| `_beTriggerReachedPendingModification` | EvaluateBreakEvenCore (worker) | Yes | No |
| `_dedupInsertCount` | TryMarkAndCheckDuplicate (Interlocked.Increment) | No — NT thread | No |
| `_onEnqueueFailureCallback` | SetOnEnqueueFailureCallback (bind time) | N/A | Invoked from EnqueueAndWait (caller thread) |

---

## 5. Broker Interaction Map

| Executor Method | Where Invoked | Always on Worker? | Retry Logic? | Can Throw? | Failure Handling |
|----------------|---------------|-------------------|---------------|------------|-------------------|
| `CreateStopMarketOrder` | SubmitStopEntryOrder (IEA), SubmitSingleEntryOrderCore (adapter) | Yes (via EnqueueAndWait or ProcessExecutionUpdate) | No | Yes | Aggregation: returns FailureResult, logs ENTRY_AGGREGATION_FAILED; single-order: propagates to caller |
| `CancelOrders` | TryAggregateWithExistingOrders, HandleEntryFill (opposite cancel) | Yes | No | Yes | Caught in aggregation try/catch → FailureResult |
| `SubmitOrders` | TryAggregateWithExistingOrders, SubmitSingleEntryOrderCore | Yes | No | Yes | Same |
| `SubmitProtectiveStop` | HandleEntryFill (IEA) | Yes | Yes — 3 retries, 100ms delay | Yes | After retries: FailClosed |
| `SubmitTargetOrder` | HandleEntryFill (IEA) | Yes | Same loop as stop | Yes | Same |
| `ModifyStopToBreakEven` | EvaluateBreakEvenCore | Yes | BE_MODIFY_MAX_RETRIES (25) via _beModifyFailureCountByIntent | Yes | stopMissing → Flatten; else StandDownStream |
| `Flatten` | HandleEntryFill (FailClosed), EvaluateBreakEvenCore (BE failure), HandleExecutionUpdateReal (untracked/unknown fill) | Yes when from ProcessExecutionUpdate/EvaluateBreakEvenCore; FlattenIntent uses EnqueueFlattenAndWait | FlattenWithRetryCore: 3 retries, 200ms | Yes | POSITION_FLATTEN_FAIL_CLOSED, stand down, notification |
| `StandDownStream` | HandleEntryFill (FailClosed), EvaluateBreakEvenCore | Yes | No | Yes | Logged, callback invoked |
| `FailClosed` | HandleEntryFill (multiple paths) | Yes | No | Yes | Flattens, stands down, alerts, persists incident |
| `ProcessExecutionUpdate` | EnqueueExecutionUpdate work item | Yes | No | Yes | Worker catches, logs IEA_QUEUE_WORKER_ERROR |
| `ProcessOrderUpdate` | EnqueueOrderUpdate work item | Yes | No | Yes | Same |
| `RecordSubmission` | TryAggregateWithExistingOrders, SubmitProtectiveStop, SubmitTargetOrder | Yes | No | Yes | Best-effort in some paths |
| `GetActiveIntentsForBEMonitoring` | EvaluateBreakEvenCore | Yes | No | Yes | Returns empty list on failure |
| `HasWorkingProtectivesForIntent` | HandleEntryFill | Yes | No | Yes | Adoption / fail-close logic |
| `GetWorkingProtectiveState` | HandleEntryFill | Yes | No | Yes | Same |

---

## 6. Adoption & Hydration Flow

### When does ScanAndAdoptExistingProtectives run?

- **Trigger:** First execution update after process start
- **Location:** Inside `EnqueueExecutionUpdate` work item: `if (!_hasScannedForAdoption) { _hasScannedForAdoption = true; ScanAndAdoptExistingProtectives(); }`
- **Before:** `Executor.ProcessExecutionUpdate(execution, order)`

### What conditions trigger adoption?

- **Per order:** `account.Orders` iteration; order must be Working/Accepted; tag starts with "QTSW2:"; tag decodes to intentId in `activeIntentIds`; tag ends with ":STOP" or ":TARGET"; `OrderMap` does not already have key `{intentId}:STOP` or `{intentId}:TARGET`
- **activeIntentIds:** From `Executor.GetActiveIntentsForBEMonitoring(ExecutionInstrumentKey)` — intents with entry filled per journal, BE not yet applied

### Does adoption mutate OrderMap?

- **Yes:** `OrderMap[mapKey] = oi` for each adopted protective (mapKey = `{intentId}:STOP` or `{intentId}:TARGET`)

### What assumptions does adoption rely on?

- Journal has correct entry-fill state for intents
- Broker orders have QTSW2 tags with decodable intentId
- Tag format: `...:STOP` or `...:TARGET`
- `GetActiveIntentsForBEMonitoring` returns intents that match journal + OrderMap state

### What happens if journal and broker disagree?

- **Not explicitly handled** — adoption populates OrderMap from broker; if journal says filled but broker has no working protective, adoption adds nothing for that intent
- **HandleEntryFill** checks `HasWorkingProtectivesForIntent`; if broker has working protectives but journal/intent disagree on qty or prices, fail-close (PROTECTIVE_QUANTITY_MISMATCH, PROTECTIVE_MISMATCH)

---

## 7. Determinism Audit

| Source | Location | Affects State? | Breaks Replay? |
|--------|----------|----------------|----------------|
| `DateTimeOffset.UtcNow` | WorkerLoop (_lastMutationUtc), EnqueueAndWait (timeout check, block callback), EnqueueFlattenAndWait, TryMarkAndCheckDuplicate, EvictDedupEntries, EvaluateBreakEvenCore, EmitHeartbeatIfDue, logs | Yes — _lastMutationUtc, _processedExecutionIds values, BE throttle/modify intervals, dedup eviction | Yes — event ordering and timing would differ; BE logic is time-throttled |
| `Guid.NewGuid()` | `RobotOrderIds.EncodeEntryOco` | Yes — OCO group ID affects broker order identity | Yes — OCO group must be deterministic for replay |
| `Random` | Not used in IEA | N/A | N/A |
| Non-ordered iteration | `foreach (var kvp in IntentMap)`, `foreach (var kvp in OrderMap)`, `foreach (var kvp in _processedExecutionIds)` | TryAggregateWithExistingOrders, FindOppositeEntryIntentId, GetPositionState, GetBracketState, EvictDedupEntries — iteration order undefined for ConcurrentDictionary/Dictionary | Yes — aggregation candidates, position/bracket aggregation could vary |
| `Thread.Sleep` | HandleEntryFill (RETRY_DELAY_MS 100), FlattenWithRetryCore (200), EvaluateBreakEvenCore (none), adapter retry paths | No direct IEA state | Yes — timing of retries affects when broker calls occur |
| Time-based logic | BE_SCAN_THROTTLE_MS 200, BE_MODIFY_ATTEMPT_INTERVAL_MS 200, BE_DEDUPE_FALLBACK_MS 50, DEDUP_MAX_AGE_MINUTES 5 | Throttles BE evaluation and modify attempts | Yes — must use event timestamps for replay |
| `Interlocked.Increment` (_instanceCounter) | IEA constructor | InstanceId | Yes — instance IDs must be stable for replay |

---

## 8. Snapshot Requirements for Replay

### Minimum state to serialize at replay start

- **OrderMap:** intentId → OrderInfo (IntentId, Instrument, OrderId, OrderType, Direction, Quantity, Price, State, NTOrder ref, IsEntryOrder, FilledQuantity, AggregatedIntentIds, AggregatedFilledByIntent, EntryFillTime, ProtectiveStopAcknowledged, ProtectiveTargetAcknowledged)
- **IntentMap:** intentId → Intent (full Intent)
- **IntentPolicy:** intentId → IntentPolicyExpectation
- ** _processedExecutionIds:** key → first-seen UTC (for dedup)
- ** _hasScannedForAdoption:** boolean
- ** _instrumentBlocked:** boolean
- ** _enqueueSequence, _lastProcessedSequence:** long
- **BE state:** _lastTickPriceForBE, _lastTickTimeFromEvent, _lastBeCheckUtc, _lastBeModifyAttemptUtcByIntent, _beModifyFailureCountByIntent, _beTriggerReachedPendingModification
- **Execution journal:** entry fills, exit fills, BE modified, submissions, rejections
- **Registry key:** (accountName, executionInstrumentKey)

### Broker state to simulate

- Working orders (entry stops, protective stops, targets) with tags, prices, quantities
- Position (net qty, direction, avg price) per execution instrument
- Order state machine (Submitted, Working, Filled, Cancelled, etc.)

### Internal IEA state to restore

- All fields in §4; worker thread restarted with same queue semantics
- `_onEnqueueFailureCallback` re-wired

### Event types to replay

- **ExecutionUpdate** (fill events) — in order
- **OrderUpdate** (order state changes) — in order
- **Tick/market data** (for BE evaluation) — with timestamps
- **Entry submission requests** (SubmitStopEntryOrder) — with intent, price, qty, ocoGroup
- **Flatten requests** — with intentId, instrument

---

## 9. Restart Semantics

### What happens on process restart?

- **IEA:** New instance per (account, instrument); new worker thread; empty queue; `_hasScannedForAdoption = false`; `_instrumentBlocked = false`; `_processedExecutionIds` empty; `OrderMap`, `IntentMap`, `IntentPolicy` empty (new instance)
- **Registry:** Same key returns same IEA instance only within process; new process = new registry

### What state is reconstructed?

- **From journal:** Entry fills, exit fills, BE modified, submissions — replayed or reconciled
- **From broker:** ScanAndAdoptExistingProtectives on first execution update repopulates OrderMap for working protectives
- **Intents:** Re-registered by StreamStateMachine on RANGE_LOCKED / bracket submission

### What state is lost?

- **In-memory:** _processedExecutionIds, _enqueueSequence, _lastProcessedSequence, BE throttle state, _lastMutationUtc
- **Volatile:** Any work in queue at shutdown

### What assumptions does the first execution update make?

- Journal may have entries from prior run
- Broker may have working orders (adoption)
- No in-flight EnqueueAndWait; first execution update triggers adoption before ProcessExecutionUpdate

---

## 10. Failure Mode Map

| Path | Triggers | Enqueues? | Blocks Future Work? | CRITICAL? |
|------|----------|-----------|---------------------|-----------|
| **EnqueueAndWait timeout** | Worker does not complete within timeout (e.g., NT CreateOrder/Submit blocks) | N/A (caller blocks) | Yes — `_instrumentBlocked = true`; all EnqueueAndWait/EnqueueFlattenAndWait rejected | Yes — IEA_ENQUEUE_AND_WAIT_TIMEOUT |
| **EnqueueAndWait queue overflow** | QueueDepth > MAX_QUEUE_DEPTH_FOR_ENQUEUE (500) | N/A | Yes — same | Yes — IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW |
| **EnqueueAndWait rejected (already blocked)** | `_instrumentBlocked` already true | N/A | Yes — no new work | WARN — IEA_ENQUEUE_REJECTED_INSTRUMENT_BLOCKED |
| **blockInstrumentCallback** | Timeout or overflow | No | Engine freezes instrument, stands down streams, FlattenEmergency | Yes — IEA_ENQUEUE_FAILURE_INSTRUMENT_BLOCKED |
| **Protective submission failure** | SubmitProtectiveStop/SubmitTargetOrder fail after 3 retries | No | FailClosed for that intent; stream stood down | Yes — PROTECTIVE_ORDERS_FAILED_FLATTENED |
| **Protective quantity mismatch** | HasWorkingProtectives but stopQty/targetQty != totalFilledQuantity | No | FailClosed for that intent | Yes — PROTECTIVE_QUANTITY_MISMATCH_FLATTENED |
| **Protective price mismatch** | HasWorkingProtectives but prices don't match intent | No | FailClosed for that intent | Yes — PROTECTIVE_MISMATCH_FLATTENED |
| **Intent incomplete** | Missing Direction/StopPrice/TargetPrice on fill | No | FailClosed for that intent | Yes — INTENT_INCOMPLETE_FLATTENED |
| **Execution blocked (recovery)** | IsExecutionAllowed() false | No | FailClosed for that intent | Yes — PROTECTIVE_ORDERS_BLOCKED_RECOVERY_FLATTENED |
| **BE modify max retries** | ModifyStopToBreakEven fails 25 times | No | stopMissing → Flatten; else StandDownStream | Yes — BE_MODIFY_MAX_RETRIES_EXCEEDED |
| **Worker exception** | Any exception in work item | N/A | No — worker continues | ERROR — IEA_QUEUE_WORKER_ERROR |
| **IEA enabled but not bound** | SubmitEntryOrder/SubmitStopEntryOrder before SetNTContext | N/A | Blocks submission | Yes — IEA_BYPASS_ATTEMPTED |

---

## Sequence Diagram: Entry → Fill → Protective → BE → Flatten

```
[StreamStateMachine]                    [Adapter]                         [IEA]                           [Worker]
       |                                    |                                |                                 |
       | RegisterIntent(longIntent)         |                                |                                 |
       | RegisterIntent(shortIntent)         |                                |                                 |
       | RegisterIntentPolicy(...)           |                                |                                 |
       |----------------------------------->| IntentMap[intentId]=intent      |                                 |
       |                                    | IntentPolicy[intentId]=...      |                                 |
       |                                    | (writes to IEA maps when IEA)   |                                 |
       |                                    |                                |                                 |
       | SubmitStopEntryOrder(...)           |                                |                                 |
       |----------------------------------->| EnqueueAndWait(()=>{           |                                 |
       |                                    |   agg = IEA.SubmitStopEntryOrder|                                 |
       |                                    |   if agg!=null return agg       |                                 |
       |                                    |   return SubmitSingleEntryOrderCore })                          |
       |                                    |-------------------------------->| Enqueue(work)                   |
       |                                    |                                |----------------------------->  | work()
       |                                    |                                |                                 | SubmitStopEntryOrder OR
       |                                    |                                |                                 | SubmitSingleEntryOrderCore
       |                                    |                                |                                 | -> CreateStopMarketOrder
       |                                    |                                |                                 | -> SubmitOrders
       |                                    |                                |                                 | OrderMap[intentId]=orderInfo
       |                                    |                                |                                 | done.Set()
       |                                    | Wait(12000)                     |                                 |
       |                                    |<---------------------------------------------------------------|
       |                                    | return (success, result)        |                                 |
       |<-----------------------------------|                                |                                 |
       |                                    |                                |                                 |
       | [NT ExecutionUpdate - fill]         |                                |                                 |
       |                                    |                                |                                 |
[NinjaTrader]  OnExecutionUpdate            |                                |                                 |
       |----------------------------------->| HandleExecutionUpdate          |                                 |
       |                                    | TryMarkAndCheckDuplicate (NT thread)                            |
       |                                    | EnqueueExecutionUpdate         |                                 |
       |                                    |-------------------------------->| Enqueue(()=>{                   |
       |                                    |                                |   if !_hasScannedForAdoption    |
       |                                    |                                |     ScanAndAdoptExistingProtectives |
       |                                    |                                |   ProcessExecutionUpdate        |
       |                                    |                                | })                              |
       |                                    |                                |----------------------------->  | work()
       |                                    |                                |                                 | ScanAndAdoptExistingProtectives
       |                                    |                                |                                 | HandleExecutionUpdateReal
       |                                    |                                |                                 | -> entry fill detected
       |                                    |                                |                                 | -> HandleEntryFill
       |                                    |                                |                                 |   -> SubmitProtectiveStop
       |                                    |                                |                                 |   -> SubmitTargetOrder
       |                                    |                                |                                 |   OrderMap updates
       |                                    |                                |                                 |
       | [NT OnMarketData - tick]            |                                |                                 |
       | OnMarketData                        |                                |                                 |
       |----------------------------------->| EvaluateBreakEven               |                                 |
       |                                    |-------------------------------->| Enqueue(()=>EvaluateBreakEvenCore)|
       |                                    |                                |----------------------------->  | work()
       |                                    |                                |                                 | GetActiveIntentsForBEMonitoring
       |                                    |                                |                                 | if beTriggerReached:
       |                                    |                                |                                 |   ModifyStopToBreakEven
       |                                    |                                |                                 |
       | [Flatten request]                   |                                |                                 |
       | FlattenIntent                       |                                |                                 |
       |----------------------------------->| FlattenWithRetry                |                                 |
       |                                    | EnqueueFlattenAndWait           |                                 |
       |                                    |-------------------------------->| EnqueueAndWait(FlattenWithRetryCore, 10000)
       |                                    |                                |----------------------------->  | work()
       |                                    |                                |                                 | FlattenWithRetryCore
       |                                    |                                |                                 |   -> Flatten (Executor)
       |                                    |                                |                                 | done.Set()
       |                                    | Wait(10000)                     |                                 |
       |                                    |<---------------------------------------------------------------|
       |                                    | return FlattenResult            |                                 |
       |<-----------------------------------|                                |                                 |
```

---

*End of IEA Runtime Deconstruction*
