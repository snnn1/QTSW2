# IEA CPU proof instrumentation — plan & reference (2026-03-23)

**Purpose:** Prove or falsify the top hypotheses from `IEA_CPU_RUNAWAY_FORENSIC_AUDIT_2026-03-23.md` (scan storm, retry storm, queue amplification, synchronous wait pileup, off-worker `TryRecoveryAdoption`).

**Scope (code):** `InstrumentExecutionAuthority*.cs`, `InstrumentExecutionAuthorityRegistry.cs`, `RobotEngine.cs`, `RobotEventTypes.cs` (and mirrored entries in `modules/robot/core/RobotEventTypes.cs`).

**Note:** If you maintain `NT_ADDONS/RobotEngine.cs` separately from `RobotCore_For_NinjaTrader/RobotEngine.cs`, port the `AssembleMismatchObservations` instrumentation there as well.

---

## A. Queue instrumentation (`InstrumentExecutionAuthority.cs`)

| Field / metric | Meaning |
|----------------|---------|
| `queue_depth_current` | `BlockingCollection.Count` after each successful enqueue (via pressure event only). |
| `queue_depth_high_water_mark` | Max depth observed since process start (per IEA). |
| `current_work_item_age_ms` | Age of active worker item when pressure event fires (0 if idle). |
| `max_work_item_age_ms` | Max wall time of any completed queue item (per IEA). |
| `enqueue_rate_10s` / `dequeue_rate_10s` | Counts in the **last completed** 10s wall window (rotated under lock on enqueue/dequeue). |
| `enqueue_wait_timeout_count` | `Interlocked` increments on `IEA_ENQUEUE_AND_WAIT_TIMEOUT`. |
| `enqueue_wait_slow_count` | Increments when wait completes but **≥ 1000 ms** (`IEA_ENQUEUE_AND_WAIT_TIMING`). |

**Event:** `IEA_QUEUE_PRESSURE_DIAG` (INFO), emitted from `MaybeEmitQueuePressureDiag` after each enqueue when **any** of:

| Threshold | Value |
|-----------|--------|
| `QueuePressureDepthThreshold` | **35** |
| `QueuePressureWorkAgeMsThreshold` | **4000** ms (current item age at enqueue time) |
| Enqueue vs dequeue skew | `enqueue_rate_10s > dequeue_rate_10s + 28` (last completed window) |

**Rate limit:** Minimum **45 s** between `IEA_QUEUE_PRESSURE_DIAG` emissions per IEA (`_lastQueuePressureEmitUtc`).

**Also:** `IEA_HEARTBEAT` (DEBUG) payload extended with `queue_depth_high_water_mark`, `max_work_item_age_ms`, `enqueue_rate_10s_last_window`, `dequeue_rate_10s_last_window`, `enqueue_wait_*_count` for long-window views when DEBUG is enabled.

---

## B. Adoption episode (`InstrumentExecutionAuthority.NT.cs`)

- **`adoption_episode_id`:** GUID (32 hex, no dashes) created on first `EnsureAdoptionEpisode` (defer path or `TryRecoveryAdoption`).
- **End:** `EndAdoptionEpisode` on: `ADOPTION_GRACE_EXPIRED_UNOWNED` (then immediately `EnsureAdoptionEpisode("post_grace_scan")` for the continuation), `ADOPTION_CANDIDATES_EMPTY_NO_BROKER_ORDERS`, `ADOPTION_NON_CONVERGENCE_ESCALATED`, end of `ADOPTION_SCAN_SUMMARY` (`adoption_success` vs `scan_summary_complete`).

**Fingerprint on adoption logs:** `account_orders_total`, `same_instrument_qtsw2_working_count`, `candidate_intent_count`, `deferred`, `adoption_scan_in_progress`, plus `adoption_episode_id` on:

- `ADOPTION_SCAN_START`, `ADOPTION_SCAN_SUMMARY`, `ADOPTION_DEFERRED_CANDIDATES_EMPTY`, `ADOPTION_GRACE_EXPIRED_UNOWNED`, `ADOPTION_CANDIDATES_EMPTY_NO_BROKER_ORDERS`, `ADOPTION_NON_CONVERGENCE_ESCALATED`

**Registry:** `InstrumentExecutionAuthorityRegistry.RetryDeferredAdoptionScansForAccount` calls `RecordDeferralHeartbeatRetryForProof` and adds `adoption_proof` (episode + flags) per row on `ADOPTION_DEFERRAL_HEARTBEAT_RETRY`.

---

## C. Thread attribution (`IEA_EXPENSIVE_PATH_THREAD`)

**Event:** `IEA_EXPENSIVE_PATH_THREAD` (INFO), **≤ 1 / 60 s per (IEA, path)** except `AssembleMismatchObservations` is **per engine**.

| Path key | Where |
|----------|--------|
| `ScanAndAdoptExistingOrders` | Start of `ScanAndAdoptExistingOrdersCore` |
| `TryRecoveryAdoption` | After throttle gate passes, before scan |
| `VerifyRegistryIntegrity` | Start of verify (`OrderRegistryPhase2`) |
| `AssembleMismatchObservations` | After early returns, start of assembly (`RobotEngine`) — `on_iea_worker` omitted as `false` in payload; `note` explains engine thread |

Payload: `thread_id`, `thread_name`, `on_iea_worker` (IEA paths only), `iea_instance_id`, `execution_instrument_key` where applicable.

---

## D. Scan timing & cardinality

| Location | Event / carrier | Metrics |
|----------|-----------------|--------|
| `ScanAndAdoptExistingOrdersCore` | `ADOPTION_SCAN_SUMMARY` | `wall_ms`, `scan_wall_ms` (latter only if `EngineCpuProfile.IsEnabled()`), `orders_scanned_loop`, `account_orders_total`, `recovery_requests_emitted`, `supervisory_requests_emitted` |
| `VerifyRegistryIntegrity` | `IEA_VERIFY_REGISTRY_INTEGRITY_DIAG` | `wall_ms`, `account_orders_total`, `orders_iterated_pass1/2`, `registry_working_items`, `broker_working_ids_count`, `divergence_events_logged`, `recovery_requests_emitted`, `supervisory_requests_emitted` |
| `AssembleMismatchObservations` | `RECONCILIATION_ASSEMBLE_MISMATCH_DIAG` | `wall_ms`, `instruments_scanned`, `recovery_adoption_invocations`, `mismatch_observations_emitted`, snapshot counts |

**Emit rules:**  
- **Verify:** emit if `wall_ms ≥ 75` OR any recovery/supervisory/divergence OR first call OR **≥ 55 s** since last diag.  
- **Assemble:** emit if `wall_ms ≥ 50` OR `recovery_adoption_invocations > 0` OR `mismatch_observations_emitted > 0` OR first call OR **≥ 55 s** since last.

---

## E. Same-state retry (`ADOPTION_SAME_STATE_RETRY_WINDOW`)

- **Fingerprint A:** `scan_start|{account_orders_total}|{qtsw2}|{candidates}|{deferred}` on each `ADOPTION_SCAN_START` (`TrackAdoptionSameStateRetry`).
- **Fingerprint B:** `deferral_retry|{ExecutionInstrumentKey}|{deferred}|{scan_in_progress}|{episode}` on each deferred heartbeat retry row (`RecordDeferralHeartbeatRetryForProof`).

**Emit when:** same fingerprint repeats **≥ 5** times within a **90 s** rolling window; **≤ 1 emit / 60 s** (`ADOPTION_SAME_STATE_RETRY_WINDOW`, WARN).

---

## F. `EnqueueAndWait` attribution

Extended payloads (existing events, no new event type):

- **`IEA_ENQUEUE_AND_WAIT_TIMEOUT`:** `caller_operation`, `caller_thread_id`, `caller_thread_name`, `caller_thread_type` (`iea_worker` / `iea_named` / `ninja_script` / `other`), `worker_busy_at_timeout`, `worker_current_command_type`.
- **`IEA_ENQUEUE_AND_WAIT_TIMING`:** same caller fields, `worker_busy_after_wait`, `worker_current_command_type`.

---

## New / extended event types (`RobotEventTypes`)

| Event | Level |
|-------|--------|
| `IEA_QUEUE_PRESSURE_DIAG` | INFO |
| `IEA_EXPENSIVE_PATH_THREAD` | INFO |
| `IEA_VERIFY_REGISTRY_INTEGRITY_DIAG` | INFO |
| `ADOPTION_SAME_STATE_RETRY_WINDOW` | WARN |
| `RECONCILIATION_ASSEMBLE_MISMATCH_DIAG` | INFO |

---

## Expected signatures (confirm / falsify hypotheses)

### Scan storm **confirmed** if you see:

- Frequent `ADOPTION_SCAN_START` / `ADOPTION_SCAN_SUMMARY` with high `account_orders_total`, high `wall_ms`, high `orders_scanned_loop`.
- `IEA_EXPENSIVE_PATH_THREAD` for `ScanAndAdoptExistingOrders` with `on_iea_worker: true` **and** high rates of the same with `TryRecoveryAdoption` / `AssembleMismatchObservations` with `on_iea_worker: false` (parallel scans).
- `IEA_VERIFY_REGISTRY_INTEGRITY_DIAG` with large `account_orders_total` and non-trivial `wall_ms` every ~60s heartbeat path.

### Scan storm **falsified** if:

- CPU spike window shows **no** correlation with adoption summary volume or verify diag wall times; `recovery_adoption_invocations` stays 0 while CPU is high elsewhere (e.g. native NT).

### Queue amplification **confirmed** if:

- `IEA_QUEUE_PRESSURE_DIAG` fires with `queue_depth_current` and HWM rising over time.
- `enqueue_rate_10s` ≫ `dequeue_rate_10s` sustained in the same event.
- `IEA_ENQUEUE_AND_WAIT_TIMEOUT` with large `queue_depth_at_start` / `queue_depth_now`.

### Queue amplification **falsified** if:

- Depth and HWM stay low during incidents; pressure events never fire; timeouts happen with `queue_depth ≈ 0` (points to **single-item stall**, not backlog).

### Synchronous wait pileup **confirmed** if:

- Many `IEA_ENQUEUE_AND_WAIT_TIMING` / `TIMEOUT` with `caller_thread_type` = `ninja_script` or `other`, high `elapsed_ms`, `worker_busy_at_timeout` true, `worker_current_command_type` showing long kinds (`AdoptionScan`, `ExecutionUpdate`, `Flatten`).
- `enqueue_wait_slow_count` / `enqueue_wait_timeout_count` climbing in `IEA_HEARTBEAT` (DEBUG) or pressure diag.

### Synchronous wait pileup **falsified** if:

- Waits are fast while CPU is high (CPU burn not on the wait path).

### Retry storm / same episode **confirmed** if:

- Same `adoption_episode_id` across many `ADOPTION_SCAN_*` lines without terminal events.
- `ADOPTION_SAME_STATE_RETRY_WINDOW` fires with stable `fingerprint`.
- `ADOPTION_DEFERRAL_HEARTBEAT_RETRY` rows repeating with `scan_enqueued: true` and unchanged `adoption_proof`.

### Off-worker `TryRecoveryAdoption` **confirmed** if:

- `IEA_EXPENSIVE_PATH_THREAD` for `TryRecoveryAdoption` with `on_iea_worker: false` interleaved with `AssembleMismatchObservations` on the same wall-clock period.
- `RECONCILIATION_ASSEMBLE_MISMATCH_DIAG` with `recovery_adoption_invocations > 0` and non-trivial `wall_ms`.

### Off-worker **falsified** if:

- `TryRecoveryAdoption` thread events always show `on_iea_worker: true` (unexpected — would contradict current call graph; would warrant re-checking call sites).

---

## Related audit

`docs/robot/audits/IEA_CPU_RUNAWAY_FORENSIC_AUDIT_2026-03-23.md`
