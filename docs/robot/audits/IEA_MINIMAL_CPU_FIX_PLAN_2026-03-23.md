# IEA minimal CPU fix — adoption scan single-flight (2026-03-23)

## Exact problem being fixed

**Symptom:** CPU spikes and “adoption storms” under reconciliation / mismatch audit load.

**Root mechanism (highest likelihood):** `TryRecoveryAdoption()` ran `ScanAndAdoptExistingOrders()` **inline on the caller thread** (engine / timer / gate recovery), while the same instrument’s IEA **worker** could also run adoption from first execution update, deferred retry, or bootstrap. That **broke the intended serialization boundary**: multiple threads could execute the same **O(account orders)** scan concurrently, amplifying work and contending with NinjaTrader’s `account.Orders` access.

See: `IEA_CPU_RUNAWAY_FORENSIC_AUDIT_2026-03-23.md` (H4 — off-worker `TryRecoveryAdoption`).

## Why off-worker scans were unsafe

1. **Parallelism:** Heavy scan body is not designed for concurrent execution per IEA instance.
2. **Thread affinity:** NT APIs and collections are observed from arbitrary pool/timer threads → harder reasoning and higher cost under load.
3. **Scheduling amplification:** Reconciliation + gate recovery + heartbeat deferred retry could **stack** multiple scans logically “for the same recovery epoch” without a single queue.

## State model — single-flight adoption scan gating

Per IEA instance, authoritative gate (`AdoptionScanSingleFlightGate`):

| State   | Meaning |
|--------|---------|
| **Idle**   | No adoption scan reserved or running. |
| **Queued** | Exactly one off-worker request **reserved** a slot; a worker item will transition to Running. |
| **Running**| Worker is executing the heavy scan body (`ScanAndAdoptExistingOrders` → `ScanAndAdoptExistingOrdersCore`). |

**Rules:**

- **Idle → Queued:** successful off-worker `RequestAdoptionScan` after throttle (recovery) and duplicate checks.
- **Queued → Running:** worker entry `TryBeginQueuedRun()` at start of the enqueued lambda.
- **Running → Idle:** `EndRun()` in `finally` after scan (and optional bootstrap post-action).
- **Idle → Running (inline):** on-worker `RequestAdoptionScan` when gate was Idle (`TryBeginInlineRun()`), e.g. first execution update on worker.

**Explicit outcomes** (logged / returned):

- `accepted_and_queued` / `accepted_inline`
- `skipped_already_running`
- `skipped_already_queued`
- `skipped_throttled` (recovery path only, **before** reserving Queued — throttle semantics preserved)

**Recovery throttle:** Still **at most one new recovery scheduling attempt per 10s per IEA** when state was Idle and the request would reserve a new scan. Duplicate requests while **Queued/Running** do **not** consume the throttle window.

## Exact call sites changed

| Location | Change |
|----------|--------|
| `InstrumentExecutionAuthority.NT.cs` | `RequestAdoptionScan` is the **only** gate for heavy adoption; `TryRecoveryAdoption` → schedules worker; new `TryScheduleRecoveryAdoptionScan`; deferred retry and bootstrap go through gate; first execution update uses `RequestAdoptionScan`; `ScanAndAdoptExistingOrders` **fails closed** if called off worker. |
| `RobotCore_For_NinjaTrader/RobotEngine.cs` | `AssembleMismatchObservations` uses `TryScheduleRecoveryAdoptionScan`; skips mismatch for that pass when scan accepted / already in flight; throttle path unchanged for classification. |
| `NT_ADDONS/RobotEngine.cs` | Same reconciliation / gate recovery updates. |
| `modules/robot/core/Execution/AdoptionScanSingleFlightGate.cs` | New shared gate helper (linked into NT `Robot.Core.csproj`). |
| `modules/robot/core/RobotEventTypes.cs` + `RobotCore_For_NinjaTrader/RobotEventTypes.cs` | New event types for proof logging. |
| `modules/robot/core/Tests/IeaAdoptionScanSingleFlightGateTests.cs` + harness | Regression tests for gate invariants. |

## What behavior remains unchanged

- **Recovery throttle** cadence for *new* recovery requests (10s) when Idle.
- **Fail-closed** semantics for registry / mismatch when recovery does not apply or is throttled.
- **Execution / order updates / flatten** paths: still use `EnqueueRecoveryEssential` / normal enqueue; **not** blocked by adoption gate.
- **Deferred adoption** semantics: still driven by `_adoptionDeferred`; retry still via heartbeat → `TryRetryDeferredAdoptionScanIfDeferred` (now gate-aware).
- **Bootstrap:** still runs scan then `OnBootstrapAdoptionCompleted`; if off worker, **both** run on the IEA worker inside one gated work item.

## New events (compact proof)

| Event | Role |
|-------|------|
| `IEA_ADOPTION_SCAN_REQUEST_ACCEPTED` | Request accepted (`accepted_and_queued` or `accepted_inline`), `queue_depth_at_accept`, thread attribution. |
| `IEA_ADOPTION_SCAN_REQUEST_SKIPPED` | `skipped_*` / throttled; **rate-limited** (60s per source+reason). |
| `IEA_ADOPTION_SCAN_EXECUTION_STARTED` | Worker scan start; `adoption_scan_episode_id`. |
| `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` | `scan_wall_ms`, `adopted_delta`, fingerprint summary fields. |
| `IEA_ADOPTION_SCAN_GATE_ANOMALY` | Worker expected Queued but state wrong (should be rare). |
| `IEA_ADOPTION_SCAN_OFF_WORKER_VIOLATION` | Defensive: something called `ScanAndAdoptExistingOrders` off worker. |

Existing `ADOPTION_SCAN_START` / `ADOPTION_SCAN_SUMMARY` remain; payloads use `adoption_scan_gate` instead of `adoption_scan_in_progress`.

## Expected log signatures — before vs after

**Before**

- `IEA_EXPENSIVE_PATH_THREAD` with `path: TryRecoveryAdoption` / `ScanAndAdoptExistingOrders` and `on_iea_worker: false` during reconciliation bursts.
- Multiple interleaved full scans without a single `Queued/Running` gate narrative.

**After**

- Recovery scheduling: `IEA_ADOPTION_SCAN_REQUEST_ACCEPTED` with `disposition: accepted_and_queued`, `scan_request_source: RecoveryAdoption`, then worker-only `IEA_ADOPTION_SCAN_EXECUTION_*`.
- Duplicate reconciliation while scan pending: `IEA_ADOPTION_SCAN_REQUEST_SKIPPED` with `skipped_already_queued` or `skipped_already_running` (rate-limited).
- Throttled recovery: `skipped_throttled` without new queue item.

## Risks / regression watch items

1. **Mismatch observation timing:** When recovery scan is **queued**, `AssembleMismatchObservations` **skips** adding a mismatch **for that pass** (intentional: avoid fail-closed storm while worker adopts). Next pass should re-evaluate.
2. **`TryRecoveryAdoption` return value:** Often **0** when scan is async; callers should prefer `TryScheduleRecoveryAdoptionScan` for control flow (engine updated).
3. **Bootstrap latency:** If bootstrap ran scan synchronously on strategy thread before, it is now **queued** when off worker — `OnBootstrapAdoptionCompleted` runs after scan on worker (more correct ordering).
4. **Queue depth:** Under extreme load, adoption work still queues behind other recovery-essential items — watch `IEA_QUEUE_PRESSURE_DIAG` (out of scope for this pass).

## Tests

- `dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test IEA_ADOPTION_GATE` — gate state machine.
- Full IEA threading tests require NinjaTrader host; gate tests lock the coordinator invariants used by production code.

---

## Why this should materially reduce CPU spikes

Reconciliation and timers were **starting heavy work on their own threads** at the same time the IEA worker could be doing the same job. That meant the platform could be **walking the full order list many times over in parallel** for the same instrument. Now there is **only one adoption scan at a time per IEA**, and it **always runs on the worker thread**, so duplicate requests **collapse into a single queued run** instead of turning into **many simultaneous expensive loops**.
