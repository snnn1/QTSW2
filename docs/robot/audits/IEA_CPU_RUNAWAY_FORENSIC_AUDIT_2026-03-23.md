# IEA CPU runaway & NinjaTrader destabilization — forensic audit (2026-03-23)

**Method:** Read-only code trace. Every **confirmed** claim cites file + method. Items marked **Hypothesis** lack direct proof in logs/runtime.  
**Axes:** Each risk is scored **Likelihood** (low / medium / high) and **Impact** (low / medium / high) **separately**.

---

## Executive summary

The IEA is **event-driven** on a dedicated worker (`WorkerLoop` + `BlockingCollection<Action>`). There is **no tight `while(true)` spin** on the worker; idle wait uses `TryTake(..., 1000)` (`InstrumentExecutionAuthority.cs`).

**Confirmed high-CPU / stall drivers** are overwhelmingly:

1. **O(N) work over NinjaTrader `account.Orders`** (often **twice per adoption scan**, plus **two passes in `VerifyRegistryIntegrity`**) multiplied by **many IEA instances** (one per chart / execution key) and **high NT callback rates** (execution/order updates → one queue item each).
2. **Synchronous adoption on non-IEA threads**: `TryRecoveryAdoption()` runs `ScanAndAdoptExistingOrders()` **inline on the caller** (engine reconciliation path and **mismatch audit timer thread**), not serialized behind the IEA queue — so heavy scans can run **concurrently** with the IEA worker and **block timer / pool threads** for tens–hundreds of ms per call.
3. **Practically unbounded queue**: `BlockingCollection<Action>` is constructed with **no capacity limit** (`InstrumentExecutionAuthority.cs`) — under an execution storm, **memory grows** until OOM or extreme latency (**Hypothesis** for hard crash; **confirmed** mechanism for backlog / timeout / fail-closed).
4. **`EnqueueAndWait`**: strategy-thread (or any non-worker) callers **block** on `ManualResetEventSlim.Wait` while the IEA drains ahead work — if the worker is in a long scan or poisoned backlog, **blocking extends** up to timeout (`InstrumentExecutionAuthority.cs`).

**Crash vs hurt NT:**  
- **High CPU / freezes:** strongly supported by scan cost × parallelism × callbacks.  
- **Process crash (unhandled OOM / fatal):** not proven in code paths reviewed; **plausible** via unbounded queue + logging pressure (**Hypothesis**).

---

## Ranked suspect list (Likelihood × Impact)

| Rank | Mechanism | Likelihood | Impact | Type |
|------|-----------|------------|--------|------|
| 1 | Adoption + integrity scans over **all** `account.Orders` × **N IEAs** × **callbacks** | **High** | **High** | Scan storm + event storm |
| 2 | `TryRecoveryAdoption` / `ScanAndAdoptExistingOrders` on **timer / reconciliation thread** (not IEA queue) | **High** | **High** | Parallel scan storm + lock/thread contention |
| 3 | **Unbounded** `_executionQueue` → backlog, timeouts, memory | **Medium** | **High** | Queue amplification → OOM **(Hypothesis)** / stall |
| 4 | `EnqueueAndWait` blocks **strategy thread** behind long worker items | **Medium** | **High** | Synchronous wait pileup |
| 5 | Engine **5s timer** `RetryDeferredAdoptionScansForAccount` while `_adoptionDeferred` | **Medium** | **Medium** | Retry storm (bounded 5s, but stacks per IEA) |
| 6 | `VerifyRegistryIntegrity` **per-order** `RequestRecovery` + `RequestSupervisoryAction` + logs (no inner rate limit) | **Medium** | **Medium** | Scan + log storm when divergences are many |
| 7 | `MismatchEscalationCoordinator` **5s** audit → `AssembleMismatchObservations` → `TryRecoveryAdoption` | **High** | **Medium** | Periodic full reconciliation work |
| 8 | `EXECUTION_COMMAND_STALLED` / poison: single log + callback per stall episode | **Low** | **Medium** | Not a tight loop; callback side effects depend on engine |
| 9 | `ExecuteSubmitProtectives` **`Thread.Sleep(100)`** on **strategy thread** (adapter, not IEA) | **Medium** | **Medium** | Blocking NT strategy thread (adjacent handoff) |

---

## Confirmed high-risk mechanisms (detail)

### H1 — Worker queue + unbounded backlog  
**File / method:** `InstrumentExecutionAuthority.cs` — field `private readonly BlockingCollection<Action> _executionQueue = new();` (no capacity); `EnqueueCore` → `_executionQueue.Add(...)`.  
**Trigger:** Any burst of `EnqueueExecutionUpdate` / `EnqueueOrderUpdate` / commands exceeding worker throughput.  
**Why hot:** Each item runs user delegate; recovery path never disabled for execution/order updates.  
**Classification:** Event storm → **queue backlog amplification**; **Hypothesis:** OOM if sustained.  
**Persists when:** Market / NT emits high-frequency partial fills and modifies.  
**Likelihood:** Medium · **Impact:** High  

### H2 — Adoption scan: O(N) and double pass  
**File / method:** `InstrumentExecutionAuthority.NT.cs` — `ScanAndAdoptExistingOrdersCore`.  
**Evidence:**  
- `account.Orders.Count(o => ...)` (LINQ) — full enumeration.  
- `foreach (Order o in account.Orders)` for QTSW2 count — second pass.  
- `foreach (Order o in account.Orders)` main adoption — third pass over same collection in worst case.  
**Trigger:** First `EnqueueExecutionUpdate` when `!_hasScannedForAdoption` (lines 536–539); `TryRetryDeferredAdoptionScanIfDeferred`; `TryRecoveryAdoption` (sync); `RunBootstrapAdoption`.  
**Classification:** **Scan storm** (not a tight loop).  
**Persists when:** Large `Account.Orders` (many instruments on one account).  
**Likelihood:** High · **Impact:** High  

### H3 — Deferred adoption retry (5s engine timer)  
**Files / methods:**  
- `RobotEngine.cs` — `EmitTimerHeartbeatUnsafe` (timer **5s**): calls `InstrumentExecutionAuthorityRegistry.RetryDeferredAdoptionScansForAccount`.  
- `InstrumentExecutionAuthorityRegistry.cs` — `RetryDeferredAdoptionScansForAccount`: for each IEA with `HasDeferredAdoptionScanPending`, `TryRetryDeferredAdoptionScanIfDeferred()`.  
- `InstrumentExecutionAuthority.NT.cs` — `TryRetryDeferredAdoptionScanIfDeferred`: sets `_adoptionScanInProgress`, `EnqueueRecoveryEssential(..., "AdoptionDeferredRetry")`.  
**Trigger:** `_adoptionDeferred == true` after `ADOPTION_DEFERRED_CANDIDATES_EMPTY` path (`ScanAndAdoptExistingOrdersCore` lines 701–723).  
**Why hot:** While journal candidates stay empty and QTSW2 orders remain, **every ~5s** per affected IEA enqueues another full scan (subject to `_adoptionScanInProgress` — at most one in flight per IEA).  
**Re-fire without new external info:** **Confirmed** — timer-driven.  
**Classification:** **Retry storm** (rate-limited by 5s + in-flight guard).  
**Likelihood:** Medium · **Impact:** Medium  

### H4 — `TryRecoveryAdoption` synchronous full scan off worker  
**File / method:** `InstrumentExecutionAuthority.NT.cs` — `TryRecoveryAdoption` (lines 612–623): throttle **10s** (`TryRecoveryAdoptionMinIntervalSeconds`), then **`ScanAndAdoptExistingOrders()` on current thread**.  
**Callers (confirmed):**  
- `RobotEngine.cs` — `AssembleMismatchObservations` (lines 5177–5202) when broker working > 0 and IEA working 0.  
- `RobotEngine.cs` — `RunInstrumentGateReconciliation` (lines 5350–5355) `ieaRecover.TryRecoveryAdoption()`.  
**Why hot:** Same O(N) scan as worker, but runs on **engine tick context** or **mismatch timer** — **parallel** with IEA worker and other audits.  
**Classification:** **Scan storm** + **thread contention**.  
**Likelihood:** High · **Impact:** High  

### H5 — `VerifyRegistryIntegrity` two-pass + per-order side effects  
**File / method:** `InstrumentExecutionAuthority.OrderRegistryPhase2.cs` — `VerifyRegistryIntegrity`.  
**Evidence:**  
- Pass 1: `foreach (Order o in account.Orders)` build `brokerOrderIds`.  
- `GetWorkingOrderIds().ToHashSet()` — allocates; underlying `GetWorkingOrderIds` uses LINQ `Where(...).Select(...).ToList()` (`OrderRegistry.cs` lines 117–119).  
- Pass 2: `foreach (var regId in workingRegistryIds)` — may log + `RequestRecovery` + `RequestSupervisoryAction` **per** missing broker id.  
- Pass 3: `foreach (Order o in account.Orders)` — per broker order not in registry: log, `TryAdoptBrokerOrderIfNotInRegistry`, optional log, `RequestRecovery`, `RequestSupervisoryAction`.  
**Trigger:** `EmitHeartbeatIfDue` (`InstrumentExecutionAuthority.cs` lines 440–456) after **≥60s** since `_lastHeartbeatUtc`, but only when invoked from `WorkerLoop` after a **completed** queue item.  
**Classification:** **Scan storm**; **log/recovery storm** if hundreds of divergences (edge case).  
**Persists when:** Permanent registry/broker skew (bad adoption, manual orders, NT ghost state).  
**Likelihood:** Medium · **Impact:** Medium (High if divergence count huge)  

### H6 — Heartbeat body cost (journal + registry + metrics)  
**File / method:** `InstrumentExecutionAuthority.cs` — `EmitHeartbeatIfDue`.  
**Evidence:** Builds `HashSet` from `Executor.GetActiveIntentsForBEMonitoring(ExecutionInstrumentKey).Select(...)` (journal/adapter work); then `RunRegistryCleanup`, `VerifyRegistryIntegrity`, `EmitRegistryMetrics`, `EmitExecutionOrderingMetrics`, `IEA_HEARTBEAT` log.  
**Trigger:** Wall clock **60s** gate + worker just finished an item.  
**Classification:** **Bounded interval** but **heavy** when fired; not every callback.  
**Likelihood:** Medium · **Impact:** Medium  

### H7 — `EnqueueAndWait` / stall supervisor  
**File / method:** `InstrumentExecutionAuthority.cs` — `EnqueueAndWait<T>`: `done.Wait(timeoutMs)`; `CheckCommandStall` timer **2s** fires `EXECUTION_COMMAND_STALLED` once per `_currentWorkStartedUtc` (dedup `_lastStallEmittedForStartUtc`).  
**Trigger:** Slow worker item (>8s) or huge queue depth.  
**Classification:** **Synchronous wait pileup** on callers; stall logging **not** a loop.  
**Callback:** `_onEnqueueFailureCallback` (adapter sets `_blockInstrumentCallback` in `NinjaTraderSimAdapter.SetNTContext` / `SetEngineCallbacks`) — **exact engine behavior** not traced here (**Hypothesis** for downstream storm).  
**Likelihood:** Medium · **Impact:** High (when triggered)  

### H8 — Mismatch coordinator 5s global audit  
**Files / methods:** `MismatchEscalationCoordinator.cs` — `OnAuditTick` every `MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_MS` (**5000** ms, `MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_MS` in `MismatchEscalationModels.cs`); calls `_getMismatchObservations(snapshot, utcNow)` → `RobotEngine.AssembleMismatchObservations`.  
**Classification:** **Periodic scan storm** independent of IEA worker.  
**Likelihood:** High · **Impact:** Medium  

---

## Plausible but unconfirmed mechanisms (Hypothesis)

| ID | Hypothesis | Missing evidence |
|----|------------|------------------|
| U1 | **OOM** from unbounded queue + large `Action` closures | Need memory dump / ETW under load |
| U2 | **RequestRecovery** / supervisory callbacks **re-enqueue** IEA work in a tight positive feedback loop | Trace `OnRecoveryRequested` implementation end-to-end |
| U3 | **Logging pipeline** backpressure causes thread pool growth / deadlocks | Logger implementation + queue metrics |
| U4 | **Re-entrancy:** `EnqueueAndWait` from worker runs work **inline** (`Thread.CurrentThread == _workerThread`) — risk of **nested** long work | Specific call stack under `Flatten`/entry |

---

## Exonerated areas (within scope)

| Area | Reason |
|------|--------|
| IEA worker idle loop | `TryTake(..., 1000)` — **blocks**, does not spin (`InstrumentExecutionAuthority.cs` `WorkerLoop`). |
| `CheckCommandStall` | At most **one** `EXECUTION_COMMAND_STALLED` per stall episode (dedup by start time). |
| `ADOPTION_NON_CONVERGENCE_ESCALATED` | Convergence helper **quarantines** heavy work after threshold (`AdoptionReconciliationConvergence.cs`). |
| Foreign QTSW2 skips | Log sample **1/50** (`InstrumentExecutionAuthority.NT.cs` lines 772–782). |

---

## Hot-path map (repeated under load)

### A. Normal worker processing  
`NT callback` → `NinjaTraderSimAdapter` / router → `EnqueueExecutionUpdate` or `EnqueueOrderUpdate` → `EnqueueRecoveryEssential` → `_executionQueue` → `WorkerLoop` → `Executor.ProcessExecutionUpdate` / `ProcessOrderUpdate`.  
**Bounded?** Per callback **one** item; **unbounded** queue depth under storm.

### B. Adoption scan path  
Triggers: first execution update path; deferred retry enqueue; `TryRecoveryAdoption` (sync); bootstrap.  
**Bounded?** Work bounded per scan O(N); **unbounded** repetitions if triggers fire often (timer 5s + mismatch 5s + execution-driven).

### C. Deferred adoption retry  
`EmitTimerHeartbeatUnsafe` (5s) → `RetryDeferredAdoptionScansForAccount` → `TryRetryDeferredAdoptionScanIfDeferred` → queue item `ScanAndAdoptExistingOrders`.  
**Bounded?** Max **one scan in flight** per IEA (`_adoptionScanInProgress`); still **periodic** while `_adoptionDeferred`.

### D. Recovery-essential enqueue  
Always allowed (`EnqueueRecoveryEssential`); no recovery guard.  
**Bounded?** Ingress unbounded.

### E. Registry verification / integrity  
`WorkerLoop` post-item → `EmitHeartbeatIfDue` (60s) → `VerifyRegistryIntegrity`.  
**Bounded?** Interval bounded; work O(orders + registry).

### F. Heartbeat / snapshot  
`IEA_HEARTBEAT` + metrics; includes journal query for active intents.  
**Bounded?** 60s wall clock.

### G. Enqueue-and-wait supervisor  
Caller blocked; stall timer observes `_currentWorkStartedUtc`.  
**Bounded?** Wait time capped by `timeoutMs`; then fail-closed `_instrumentBlocked`.

---

## Re-trigger analysis (no new meaningful broker state)

| Mechanism | Re-fires? | Evidence |
|-----------|-----------|----------|
| Engine 5s timer + deferred adoption | **Yes** | `RobotEngine.EmitTimerHeartbeatUnsafe` + `_adoptionDeferred` |
| Mismatch audit 5s | **Yes** | `MismatchEscalationCoordinator._auditTimer` |
| `TryRecoveryAdoption` throttle 10s | **Yes** while mismatch persists | `AssembleMismatchObservations` when working count mismatch |
| `VerifyRegistryIntegrity` recovery calls | **Yes** every heartbeat while skew persists | Per divergent order |
| Queue poison / stall | **No tight re-fire** | Single stall log per start time; poison after 3 worker errors |

---

## Complexity / scan audit (explicit)

| Location | Complexity | When |
|----------|------------|------|
| `ScanAndAdoptExistingOrdersCore` | O(N) LINQ + 2× O(N) foreach over `account.Orders` | Each scan |
| `VerifyRegistryIntegrity` | O(N) + O(R) + O(N) + HashSet builds | ≤60s per IEA + worker activity |
| `GetWorkingOrderIds` | O(R) LINQ `ToList` allocation | Each verify |
| `EmitHeartbeatIfDue` active intents | O(intent rows) journal via adapter | Each heartbeat |
| `AssembleMismatchObservations` | O(instruments × (IEA probes + journal)) | Each mismatch audit + reconciliation |
| `GetAdoptionCandidateIntentIds` | Journal hash merge (`NinjaTraderSimAdapter.cs` 2698–2706) | Many callers |

---

## Threading / blocking audit

| Interaction | File / method | Risk |
|-------------|---------------|------|
| Strategy thread → IEA | `EnqueueAndWait` `done.Wait` | **Blocks** until worker runs item or timeout |
| IEA worker → strategy | `Executor` NtActions processed on `StrategyThreadExecutor` (adapter) | Worker **waits** strategy completion for those actions (**adapter design**; not fully expanded here) |
| Same-thread re-entry | `EnqueueAndWait` inline if `CurrentThread == _workerThread` | Avoids deadlock; **can deepen stack** on worker |
| `TryRecoveryAdoption` | Runs on **caller** thread | **Blocks** mismatch timer / engine work during scan |
| `ExecuteSubmitProtectives` | `Thread.Sleep` in retry loop | **Blocks strategy thread** up to ~200ms+ (`NinjaTraderSimAdapter.NT.cs` 501–514) |

**Circular wait (Hypothesis):** Needs full graph of `NtActionQueue` ↔ IEA `EnqueueAndWait` — not proven in this pass.

**Locks during scans:** `VerifyRegistryIntegrity` / adoption run on IEA worker **without** holding `RobotEngine._engineLock` (IEA is separate). Engine lock still held during `RunReconciliationPeriodicThrottle` on tick — separate concern.

---

## Logging storm audit (IEA + adjacent)

### Safe (rate-limited or infrequent)

- `FOREIGN_INSTRUMENT_QTSW2_ORDER_SKIPPED` — sample 1/50.  
- `IEA_HEARTBEAT` — DEBUG + 60s gate.  
- `EXECUTION_COMMAND_STALLED` — once per stall start.  
- Stale QTSW2 detailed logs — gated by `unchangedStreak == 1` for several events (`InstrumentExecutionAuthority.NT.cs`).

### Suspicious (can amplify under load)

- `ADOPTION_SCAN_START` / `ADOPTION_SCAN_SUMMARY` — **every** completed scan (INFO).  
- `RECONCILIATION_ORDER_SOURCE_BREAKDOWN` — when `brokerWorking != localWorking` (`RobotEngine.cs` 5151–5153), throttling **not** shown on that line (may repeat each audit).  
- `REGISTRY_BROKER_DIVERGENCE` — **per order** in integrity loop (no cap in method).  

### Dangerous (many emissions × heavy payload)

- `MANUAL_OR_EXTERNAL_ORDER_DETECTED` from `RegisterUnownedOrder` — **every first registration**; after registry contains id, adoption path mostly short-circuits — **medium** risk during mass stale classification.  
- Full `AssembleMismatchObservations` building large lists + multiple `LogEvent` per instrument mismatch.

**Note:** `RobotEventTypes` levels do not stop **payload construction** if logger filters after build — **Hypothesis** depends on `RobotLogger` implementation.

---

## Reproduction hypotheses

1. **Many charts, one account, large working order count:** Enable IEA on 6+ instruments; load account with 100+ working orders across symbols; run live — expect **multiplicative** `foreach` cost.  
2. **Journal empty / restart with live QTSW2 orders:** Forces `_adoptionDeferred` → **5s** timer retries + execution updates — continuous scans until grace expires (60s) then heavy stale path.  
3. **Persistent registry/broker skew:** Force `VerifyRegistryIntegrity` to hit second loop for many broker ids — **N×** recovery/supervisory + logs every ~60s per IEA.  
4. **Execution storm:** HFT-like partial fills → unbounded queue → `EnqueueAndWait` timeouts → `_instrumentBlocked` and engine callbacks.

---

## Recommended instrumentation additions (minimal, proof-oriented)

1. **Per-IEA counters (rolling window):** `adoption_scans_total`, `adoption_scan_total_ms`, `deferred_retry_enqueues_total`, `recovery_adoption_sync_calls_total` (increment in `TryRecoveryAdoption`, `TryRetryDeferredAdoptionScanIfDeferred`, end of `ScanAndAdoptExistingOrdersCore`).  
2. **Episode id:** GUID on first `ADOPTION_DEFERRED_CANDIDATES_EMPTY`, log same id on each retry until cleared — proves **same episode** vs new.  
3. **Scan cardinality:** Log `account_orders_total`, `working_orders_total`, `registry_working_total` on each scan start (cheap counts).  
4. **Queue HWM:** `Interlocked.Max` on `QueueDepth` sample after each enqueue.  
5. **Work item age:** Already partially `_currentWorkStartedUtc`; add **high-water** `max_work_item_ms` per IEA.  
6. **Unchanged-input retries:** Pair throttle key + consecutive identical `ADOPTION_SCAN_SUMMARY` fingerprint (e.g. deferred + same `qtsw2_working_same_instrument_count`).  
7. **Rate-limited diagnostic:** `SAME_DEFERRED_STATE_TICK` — “`ADOPTION_DEFERRAL_HEARTBEAT_RETRY` fired K times in 60s with zero candidate count change”.  
8. **Separate timer attribution:** Log thread id / name in `TryRecoveryAdoption` (once per minute max) to prove **which thread** ran scan.

---

## Immediate mitigations (no refactor — operational)

1. Reduce charts / IEA instances per account during diagnosis.  
2. Temporarily disable or widen mismatch audit interval (**code change** — only if approved; not done here).  
3. Enable `data/engine_cpu_profile.enabled` + tail `scan_wall_ms` (`ADOPTION_SCAN_SUMMARY`).  
4. Monitor `queue_depth` in `IEA_HEARTBEAT` (DEBUG) or add HWM counter per instrumentation above.

---

## Structural fixes (later — design level)

1. **Bound** `_executionQueue` (bounded `BlockingCollection`) + explicit back-pressure policy.  
2. Run **all** `ScanAndAdoptExistingOrders` on IEA worker (including `TryRecoveryAdoption` → enqueue, not inline) — single serialization point.  
3. Merge adoption passes to **one** `foreach` where safe.  
4. Rate-limit `VerifyRegistryIntegrity` side effects (recovery/supervisory) per scan, not per order.  
5. Move heavy journal reads out of **60s** heartbeat or cache with invalidation.

---

## Evidence standard (for owners)

- **Confirmed:** Cited source lines / methods above.  
- **Hypothesis:** OOM, logger allocation cost, recovery feedback loop — need runtime traces.  
- **Falsify H4:** If CPU spike thread stacks never show `ScanAndAdoptExistingOrders` on non-worker threads.  
- **Confirm H4:** Stacks show `TryRecoveryAdoption` on `TimerQueue` / `ThreadPool` during spike.

---

## Most likely crash mechanism

**Single most likely explanation (code-based):**  
**Process instability from combined thread blockage and memory pressure**, not a single null-deref in IEA: (1) **unbounded queue** holding pending work under an execution storm (**H1**), (2) **long synchronous scans** on pool/timer threads (**H4**, **H8**), (3) **strategy thread blocked** by `EnqueueAndWait` and `Thread.Sleep` in protectives (**H7**, adapter), leading to **NinjaTrader UI / host thread starvation** or **OutOfMemoryException** (**Hypothesis U1**).

**What would confirm:** Memory trace showing `BlockingCollection` / delegate backlog growth; thread dump with many blocked threads on `ManualResetEventSlim.Wait` and `ScanAndAdoptExistingOrders` on timer threads.  
**What would falsify:** CPU spike with **shallow** stacks only inside NT native market data (no robot symbols) and **zero** `ADOPTION_SCAN_*` / `TryRecoveryAdoption` frames.

---

## Companion summary (plain English)

**What’s likely going wrong:** The IEA and the reconciliation code are doing **big loops over every order on the account**, and that work can be triggered by **normal market events**, **timers every 5 seconds**, and **recovery logic**. Several of those scans run **on background threads at the same time** as the IEA’s own worker, instead of always being queued behind one serial pipe.

**Why CPU spikes:** Each loop is **O(number of orders)**. With many orders and many instruments/charts, you pay that cost **over and over**. Timers keep retrying adoption while the system is in a “deferred” state, even when nothing new happened on the broker.

**Why that hurts NinjaTrader:** Heavy work on **timer and thread-pool threads** plus **blocking the strategy thread** (waiting on the IEA or sleeping during order retries) makes the host **stutter or freeze**. If work arrives faster than the IEA can process it, the **queue can grow without a hard cap**, which can push memory up and make the whole process unstable (**worst case: out-of-memory** — that part still needs a trace to prove).

**What to check next:**  
1) Thread/memory capture during a spike (look for `ScanAndAdoptExistingOrders`, `VerifyRegistryIntegrity`, `AssembleMismatchObservations`, `Wait` on IEA).  
2) Log rate of `ADOPTION_SCAN_START` / `ADOPTION_SCAN_SUMMARY` and `ADOPTION_DEFERRAL_HEARTBEAT_RETRY`.  
3) Queue depth trend (instrumentation HWM).  
4) Whether spikes correlate with **many working orders** or **many charts** on one account.

---

*Audit artifact path: `docs/robot/audits/IEA_CPU_RUNAWAY_FORENSIC_AUDIT_2026-03-23.md`*

**Proof instrumentation (implemented):** `docs/robot/audits/IEA_CPU_PROOF_INSTRUMENTATION_PLAN_2026-03-23.md`
