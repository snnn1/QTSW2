# IEA: loops, periodic checks, and what retriggers them

**Scope:** `InstrumentExecutionAuthority` (IEA) + NT adapter. This answers “is something looping in the IEA?” in terms of **actual code paths**.

## 1. The IEA worker thread (not a busy spin)

**File:** `InstrumentExecutionAuthority.cs` — `WorkerLoop()`

- `while (_workerRunning)` with `_executionQueue.TryTake(out work, **1000**)` → blocks up to **1 second** when the queue is empty. **Not** a tight CPU loop.
- **Each** dequeued item runs one `Action` (typically `ProcessExecutionUpdate`, `ProcessOrderUpdate`, adoption retry, flatten work, etc.).

So CPU correlates with **how often work is enqueued** and **how heavy each handler is**, not with an idle spin.

## 2. Per-order / per-account loops (the expensive ones)

### A. Adoption scan — **`ScanAndAdoptExistingOrders`**

**File:** `InstrumentExecutionAuthority.NT.cs`

- **Pass 1:** `foreach (Order o in account.Orders)` — count same-instrument QTSW2 working orders (`~657+`).
- **Pass 2:** `foreach (Order o in account.Orders)` — main adoption / stale / foreign-skip / convergence logic (`~746+`).

**Cost:** **O(all account working orders)** twice per scan, **per IEA instance**, and each IEA walks the **entire** `account.Orders` collection (foreign instruments are skipped quickly but still iterated).

**Triggers:**

| Trigger | Notes |
|--------|--------|
| First path in `EnqueueExecutionUpdate` | Once `_hasScannedForAdoption` is false and bootstrap/recovery gates pass (`InstrumentExecutionAuthority.NT.cs`). |
| `TryRecoveryAdoption()` | From reconciliation / gate recovery (`RobotEngine`). **Throttled to at most once per 10s per IEA** (recent change). |
| `TryRetryDeferredAdoptionScanIfDeferred()` | When adoption was deferred (candidates empty but QTSW2 orders present); engine timer / registry paths. |
| `RunBootstrapAdoption` | Bootstrap ADOPT path. |

**Convergence / “loop feeling”:** `AdoptionReconciliationConvergence` (`modules/robot/core/Execution/AdoptionReconciliationConvergence.cs`) re-sees the **same** broker order until fingerprint changes or quarantine kicks in (unchanged streak → **suppress heavy work** + **120s cooldown**). That reduces *work* per order but the **foreach still walks all orders** unless quarantined short-circuits early (quarantine is checked **inside** the loop per order).

### B. Registry integrity — **`VerifyRegistryIntegrity`**

**File:** `InstrumentExecutionAuthority.OrderRegistryPhase2.cs`

- `foreach` all working/accepted `account.Orders` → build `brokerOrderIds`.
- `foreach` working registry IDs → registry vs broker.
- `foreach` `account.Orders` again → broker order missing from registry → adopt / recovery requests.

**Trigger:** **`EmitHeartbeatIfDue()`** after a queue item completes, but the **heavy** heartbeat body runs at most every **`HEARTBEAT_INTERVAL_SECONDS` (60s)** per IEA (`InstrumentExecutionAuthority.cs`).

So this is a **periodic** O(orders) + O(registry) check, not every execution update.

## 3. Other periodic timers on the IEA

| Mechanism | Interval | Role |
|-----------|----------|------|
| `_stallCheckTimer` | **2s** | `CheckCommandStall` — only logs if a **single** queue work item exceeds **COMMAND_STALL_CRITICAL_MS**; does not walk orders. |
| `EmitHeartbeatIfDue` | **60s** | Flatten latch checks, recovery/supervisory metric hooks, **`RunRegistryCleanup`**, **`VerifyRegistryIntegrity`**, registry / ordering metrics, **`IEA_HEARTBEAT`**. |

## 4. Execution / order update storm (NinjaTrader → queue)

**Path:** NT `OnExecutionUpdate` / `OnOrderUpdate` → `NinjaTraderSimAdapter` → **`_iea.EnqueueExecutionUpdate`** / **`EnqueueOrderUpdate`** → worker runs **`ProcessExecutionUpdate`** / **`ProcessOrderUpdate`**.

- **No fixed “loop”** here: **one queue item per callback**. With many partial fills, modifies, and instruments, **queue depth** and **per-item cost** dominate.
- **`ProcessExecutionUpdate`** implementation (e.g. `HandleExecutionUpdateReal` / continuation / unresolved retries in `NinjaTraderSimAdapter.NT.cs`) can chain more work and **retries** (e.g. `MAX_RETRIES` with `Thread.Sleep` on protective submit path — separate from adoption).

## 5. Why it *feels* like a loop

1. **Many IEAs** (one per chart / execution instrument) × **shared `Account.Orders`** → same global order list scanned repeatedly across instances.  
2. **Adoption scan** = **two full passes** over `account.Orders` each time it runs.  
3. **Reconciliation / mismatch** used to call recovery adoption very often; **10s throttle** limits that; **5s** mismatch audit still drives other engine work (outside IEA queue).  
4. **`VerifyRegistryIntegrity`** every **60s** per IEA still does **two** order enumerations + registry walks — visible under load with large order counts.

## 6. Observability: “what is the IEA doing right now?”

**Partial, not line-level.** Useful signals:

| Signal | What it tells you |
|--------|-------------------|
| **`IEA_HEARTBEAT`** (`InstrumentExecutionAuthority.EmitHeartbeatIfDue`) | `queue_depth`, `enqueue_sequence` / `last_processed_sequence`, **`current_command_type`** (semantic work kind while the worker runs a queue item), `current_command_runtime_ms` if work is in flight at heartbeat time. Emitted **at most every 60s** per IEA **after** a queue item finishes (not a continuous trace). |
| **`EXECUTION_COMMAND_STALLED`** | **2s** stall timer: if a **single** queue item exceeds `COMMAND_STALL_CRITICAL_MS`, logs the same **`current_command_type`** + runtime. |
| **`ADOPTION_SCAN_START` / `ADOPTION_SCAN_SUMMARY`** | Adoption scan lifecycle; **`scan_wall_ms`** on summary when `engine_cpu_profile.enabled` is on. |
| **`ENGINE_CPU_PROFILE`** | Engine tick breakdown (stream vs reconciliation vs OnBar lock). |

**Log level:** `IEA_HEARTBEAT` is mapped to **DEBUG** in `RobotEventTypes` — default INFO pipelines often **omit** it; enable DEBUG for `robot_ENGINE` (or equivalent) to tail it.

**Work kinds (queue item labels):** Each enqueued item sets **`current_command_type`** for the duration of that item, e.g. `ExecutionUpdate`, `OrderUpdate`, `ExecutionCommand`, `EnqueueAndWait` / `Flatten`, `BreakEvenEvaluate`, `AdoptionDeferredRetry`, `ExecutionUnresolvedRetry`, `RecoveryWork` (default for unlabeled recovery enqueue), `NormalWork`, `Unknown`. While **`ScanAndAdoptExistingOrders`** runs **on the IEA worker**, the label is **`AdoptionScan`** (nested inside `ExecutionUpdate` when both run in one item).

## 7. Full event inventory (loops vs errors)

See **`IEA_LOGGING_AND_LOOPS_REFERENCE.md`** for a structured list of IEA-related log events, levels, and grep recipes (including **`ADOPTION_DEFERRAL_HEARTBEAT_RETRY`** and stall / poison paths).

## 8. Possible follow-up optimizations (not implemented here)

- Merge adoption **pass 1 + pass 2** into a **single** `foreach` over `account.Orders` where safe.  
- Rate-limit **`VerifyRegistryIntegrity`** separately from 60s heartbeat (trade risk vs CPU).  
- Debounce **`TryAdoptBrokerOrderIfNotInRegistry`** when many foreign/missing-registry orders exist to avoid recovery storms.

---

**Bottom line:** The IEA **does** repeatedly **iterate `account.Orders`** in adoption and (every 60s) in registry integrity. The worker itself is **event-driven** (queue + 1s wait), not an infinite tight loop; high CPU usually means **high callback rate** and/or **frequent adoption scans** / **large order lists** across **multiple IEAs**.

**Maintainer:** If triggers or intervals change, update this doc and **`IEA_LOGGING_AND_LOOPS_REFERENCE.md`**.
