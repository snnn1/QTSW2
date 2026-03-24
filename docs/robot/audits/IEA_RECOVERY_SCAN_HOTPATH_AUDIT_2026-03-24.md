# IEA recovery adoption scan — hot-path timing audit (2026-03-24)

**Scope:** `RecoveryAdoption` → gated adoption scan → `ScanAndAdoptExistingOrders` / `ScanAndAdoptExistingOrdersCore` in `InstrumentExecutionAuthority.NT.cs`. **Evidence:** code-path map + new event contract; **runtime phase splits** appear only after deploy when **`IEA_ADOPTION_SCAN_PHASE_TIMING`** is present in logs.

**Runtime anchor (pre-instrumentation):** `EXECUTION_COMMAND_STALLED` at **`2026-03-24T02:29:36Z`**, MNQ, **`current_command_type=RecoveryAdoptionScan`**, **`runtime_ms≈14136`** — stall threshold **8000 ms** (`InstrumentExecutionAuthority.cs`).

---

## A. Code-path breakdown (request → completed)

| Step | Location | Notes |
|------|----------|--------|
| Request accepted / throttled / queued | `RequestAdoptionScan`, `ExecuteAdoptionScanFromReservedQueueItem` | `IEA_ADOPTION_SCAN_REQUEST_*` |
| Worker picks work | `InstrumentExecutionAuthority.WorkerLoop` | Sets stall clock; mutation epilog |
| **Recovery fingerprint + no-progress skip** | `RunGatedAdoptionScanBody` | `TryBuildRecoveryScanFingerprint`, `ShouldSkipRecoveryNoProgress`, optional `IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED` |
| **Fingerprint wall time** | Same | **`FingerprintBuildMs`** (single `Stopwatch` around recovery try, `finally` assigns) |
| Execution started | `RunGatedAdoptionScanBody` | `IEA_ADOPTION_SCAN_EXECUTION_STARTED` |
| **Heavy body** | `ScanAndAdoptExistingOrders` → `ScanAndAdoptExistingOrdersCore` | Worker-only |
| Phase: **candidates** | `ScanAndAdoptExistingOrdersCore` | `Executor.GetAdoptionCandidateIntentIds(ExecutionInstrumentKey)` |
| Phase: **precount** | Same | LINQ `Count` working/accepted on **`account.Orders`** + full **`foreach`** for same-instrument QTSW2 working |
| Phase: **journal diagnostics** | Same | `Executor.GetJournalDiagnostics(ExecutionInstrumentKey)` |
| Phase: **pre-loop log** | Same | `TrackAdoptionSameStateRetry` + **`ADOPTION_SCAN_START`** (large payload) |
| Branches | Same | Deferral / empty-candidate early **returns** (no main loop) |
| Phase: **main loop** | Same | **`foreach (Order o in account.Orders)`** — QTSW2 filter, `GetOrderTag`, `TryResolveByBrokerOrderId`, `activeIntentIds.Contains`, convergence, adoption / stale / **`RequestRecovery`** / **`RequestSupervisoryAction`**, multiple `LogIeaEngine` paths |
| Phase: **summary** | Same | `EndAdoptionEpisode` + **`ADOPTION_SCAN_SUMMARY`** |
| Execution completed + mutation align (recovery) | `RunGatedAdoptionScanBody` `finally` | `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED`, optional **`IEA_ADOPTION_SCAN_MUTATION_TIME_ALIGNED`** |

---

## B/C. Phase timing instrumentation (implemented)

**Struct:** `AdoptionScanPhaseTelemetry` (private in `InstrumentExecutionAuthority.NT.cs`).

**Event:** **`IEA_ADOPTION_SCAN_PHASE_TIMING`** (INFO; registered in `RobotEventTypes` + NT `_allEntries`).

**Payload (flat):**

- `adoption_scan_episode_id`, `scan_request_source`, `iea_instance_id`, `execution_instrument_key`
- `total_wall_ms` — full `RunGatedAdoptionScanBody` stopwatch (includes fingerprint, START log, scan body, `postScanOnWorker`)
- `fingerprint_build_ms` — recovery path only (0 for non-recovery sources)
- `phase_candidates_ms`, `phase_precount_ms`, `phase_journal_diag_ms`, `phase_pre_loop_log_ms`, `phase_main_loop_ms`, `phase_summary_ms`
- `scan_body_phases_sum_ms` — sum of the six scan-body phases (excludes fingerprint and gate overhead)
- `adopted_delta` — registry delta from `GetOwnedPlusAdoptedWorkingCount` (same as `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED`)
- `adopted_in_loop` — count from scan loop (`scanAdopted`)
- `account_orders_total`, `candidate_intent_count`, `qtsw2_same_instrument_working`, `main_loop_orders_seen`

**Rate limit:** Per IEA, **60 s** between emissions **unless** `total_wall_ms > 2000` — then **always** emit (slow scans are never suppressed).

---

## Timing attribution (code-level, pre-log)

| Phase | Dominant work | Likely nature |
|--------|----------------|---------------|
| **fingerprint_build_ms** | One pass `account.Orders` + candidate count + `GetOwnedPlusAdoptedWorkingCount` | CPU + broker order collection + tag reads |
| **phase_candidates_ms** | `GetAdoptionCandidateIntentIds` | **Journal / filesystem / adapter** (highest risk of non-CPU latency) |
| **phase_precount_ms** | Full-order LINQ + full-order `foreach` | CPU + **per-order `GetOrderTag`** |
| **phase_journal_diag_ms** | `GetJournalDiagnostics` | **I/O / adapter** |
| **phase_pre_loop_log_ms** | Same-state retry tracker + **`ADOPTION_SCAN_START`** | CPU + **logging pipeline** |
| **phase_main_loop_ms** | **`foreach (Order o in account.Orders)`** — tag decode, registry resolution, `HashSet`/candidate checks, convergence, recovery/supervisory requests, conditional logs | **CPU + broker property access + logging + enqueue side effects** |
| **phase_summary_ms** | `EndAdoptionEpisode` + **`ADOPTION_SCAN_SUMMARY`** | CPU + logging |

**Top 3 wall-time contributors (static ranking for a “fat” account with many working orders and large candidate set):**

1. **`phase_main_loop_ms`** — single O(N) pass over **all** account orders with **per-order** `GetOrderTag`, `TryResolveByBrokerOrderId`, `activeIntentIds.Contains`, and branching into logs / `RequestRecovery`.
2. **`phase_candidates_ms`** — journal-backed candidate discovery (can dwarf CPU if disk or adapter is slow).
3. **`phase_precount_ms`** — second full pass over **`account.Orders`** (redundant with fingerprint pass for recovery, but still a separate measured block).

**CPU vs broker/API vs logging vs locks:**

- **Broker/API / thread handoff:** Dominated by **`GetOrderTag`**, **`GetAccount()` / `Orders`**, and property reads on **`Order`** inside loops — not async waits, but **NT API cost** scales with **N**.
- **Logging:** **`ADOPTION_SCAN_START`**, **`ADOPTION_SCAN_SUMMARY`**, and **conditional** `LogIeaEngine` inside the loop can add **queue backlog** under high log volume (same logging service as other engine events).
- **Locks:** Not explicitly timed; registry maps under scan are **not** separately broken out — possible contention is **inside** `TryResolveByBrokerOrderId` / `RegisterAdoptedOrder` (included in **main loop**).

---

## Single biggest reason a scan can still take 10–14 seconds (answer before first `PHASE_TIMING` row)

**Structural:** The **main adoption loop** walks **every** `account.Orders` entry (with QTSW2 / instrument filtering) and does **non-trivial work per candidate order** (tags, registry, convergence, optional recovery/supervisory + logs). For **tens to hundreds** of working orders and **large** `activeIntentIds`, **CPU + per-order broker access + logging** compound into **multi-second** `phase_main_loop_ms`. **Confirm** by checking **`phase_main_loop_ms` / `total_wall_ms`** on the first slow **`IEA_ADOPTION_SCAN_PHASE_TIMING`** after deploy.

---

## One recommended next fix (only after log proof)

**If `phase_main_loop_ms` dominates:** Reduce **per-order work** in the main loop (e.g. avoid repeated **`GetOrderTag`** / redundant registry lookups, or narrow enumeration to **same-instrument** working orders only) **without** changing no-progress or fail-closed semantics — **after** one slow-scan `IEA_ADOPTION_SCAN_PHASE_TIMING` sample confirms the split.

**If `phase_candidates_ms` or `phase_journal_diag_ms` dominates:** Treat as **journal / I/O** path — optimize **`GetAdoptionCandidateIntentIds`** / **`GetJournalDiagnostics`** caching or call frequency, not the adoption gate.

---

## Post-deploy checklist

1. Find **`IEA_ADOPTION_SCAN_PHASE_TIMING`** where **`total_wall_ms ≥ 8000`**.  
2. Rank `phase_*_ms` fields.  
3. Compare **`scan_body_phases_sum_ms`** to **`total_wall_ms − fingerprint_build_ms`** to see overhead outside the core (START log, `postScanOnWorker`).
