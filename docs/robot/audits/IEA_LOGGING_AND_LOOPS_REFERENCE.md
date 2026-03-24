# IEA logging reference — what to tail for loops vs errors

**Scope:** `InstrumentExecutionAuthority` (IEA) + adapter binding + order registry / bootstrap / supervisory partials.  
**Log stream:** Most events below use `state: "ENGINE"` (robot ENGINE JSONL) or execution events (`ExecutionBase`).

Correlating rows: filter by **`execution_instrument_key`** and **`iea_instance_id`** (present on most IEA engine events).

---

## 1. Queue / worker health (errors & “stuck” work)

| Event | Level | Meaning |
|--------|--------|---------|
| **`IEA_QUEUE_WORKER_ERROR`** | ERROR | Exception escaped a dequeued work item; includes `consecutive_failures`, sequences. |
| **`EXECUTION_QUEUE_POISON_DETECTED`** | CRITICAL | Too many consecutive worker errors → **`IEA_POISON_BLOCK_INSTRUMENT`**. |
| **`EXECUTION_COMMAND_STALLED`** | CRITICAL | **2s** timer: one queue item exceeded **`COMMAND_STALL_CRITICAL_MS`**; payload has **`current_command_type`** (semantic work kind, e.g. `ExecutionUpdate`, `AdoptionScan`, `Flatten`). |
| **`IEA_ENQUEUE_AND_WAIT_TIMEOUT`** | CRITICAL | `EnqueueAndWait` timed out → instrument often **blocked** fail-closed. |
| **`IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW`** | CRITICAL | Queue depth over threshold before `EnqueueAndWait`. |
| **`IEA_ENQUEUE_REJECTED_INSTRUMENT_BLOCKED`** | WARN | Normal `Enqueue` rejected (instrument already blocked). |
| **`RECOVERY_GUARD_BLOCKED_NORMAL_MANAGEMENT`** | WARN | Normal enqueue / `EnqueueAndWait` blocked while IEA in recovery. |
| **`IEA_BYPASS_ATTEMPTED`** | ERROR | Adapter: IEA enabled but not bound — submission blocked. |

**Slow path (not an error):** **`IEA_ENQUEUE_AND_WAIT_TIMING`** (INFO) — fires when `EnqueueAndWait` took **≥ 1s**; includes `context` (e.g. `Flatten`).

**Periodic observability (DEBUG by default):** **`IEA_HEARTBEAT`** — ~**60s** after a queue item completes; includes **`queue_depth`**, **`current_command_type`**, **`last_processed_sequence`**. Enable DEBUG on ENGINE stream to see it.

---

## 2. Adoption “loops” (repeated scans & convergence)

These are **not** infinite loops in code; they are **repeated scans** or **re-evaluation** when broker/registry state does not converge.

| Event | Level | Loop / repeat signal |
|--------|--------|----------------------|
| **`ADOPTION_SCAN_START`** | INFO | Beginning of **`ScanAndAdoptExistingOrders`** (per trigger). |
| **`ADOPTION_SCAN_SUMMARY`** | INFO | End of scan; **`scan_wall_ms`** when `data/engine_cpu_profile.enabled` exists. High rate × many IEAs → CPU. |
| **`ADOPTION_DEFERRED_CANDIDATES_EMPTY`** | INFO | Journal candidates empty but QTSW2 working orders on instrument → adoption **deferred**; **`retry_count`**, **`elapsed_since_first_scan_ms`**. |
| **`ADOPTION_DEFERRAL_HEARTBEAT_RETRY`** | INFO | Engine heartbeat tried **`TryRetryDeferredAdoptionScanIfDeferred`** per IEA; payload **`iea_retries`** with **`scan_enqueued`**. (Explicit **INFO** in `RobotEventTypes` — name contains `HEARTBEAT` and would otherwise match the DEBUG heuristic.) |
| **`ADOPTION_GRACE_EXPIRED_UNOWNED`** | WARN | Grace ended; proceeding to classify UNOWNED / stale. |
| **`ADOPTION_NON_CONVERGENCE_ESCALATED`** | CRITICAL | Same broker order fingerprint too many times → quarantine / cooldown path. |
| **`FOREIGN_INSTRUMENT_QTSW2_ORDER_SKIPPED`** | DEBUG | Sampled skip (every 50th) — many foreign QTSW2 tags still cost **foreach** iterations. |
| **`BOOTSTRAP_ADOPTION_ATTEMPT`** | INFO | Bootstrap ADOPT path entering adoption. |

**Recovery-driven adoption (throttled):** **`TryRecoveryAdoption`** (from engine/reconciliation) runs **`ScanAndAdoptExistingOrders`** on the **calling thread** (not always the IEA worker); **10s min interval** per IEA — no dedicated log line for “skipped by throttle”; absence of **`ADOPTION_SCAN_START`** in that window is expected.

---

## 3. Registry / ownership (errors & divergence)

| Event | Level | Notes |
|--------|--------|--------|
| **`REGISTRY_BROKER_DIVERGENCE`** | CRITICAL | Broker vs registry mismatch from **`VerifyRegistryIntegrity`** / cleanup paths. |
| **`REGISTRY_BROKER_DIVERGENCE_ADOPTED`** | INFO | Divergence resolved by adoption. |
| **`STALE_QTSW2_ORDER_DETECTED`** | CRITICAL | Stale / unowned classification during adoption. |
| **`UNOWNED_LIVE_ORDER_DETECTED`** | CRITICAL | Live UNOWNED escalation. |
| **`MANUAL_OR_EXTERNAL_ORDER_DETECTED`** | CRITICAL | Non-robot order on tracked context. |
| **`ORDER_REGISTRY_METRICS`** | INFO | Periodic metrics (with **`IEA_HEARTBEAT`** cadence path). |
| **`ORDER_REGISTRY_CLEANUP`** | DEBUG | Cleanup detail. |
| **`EXECUTION_ORDERING_METRICS`** | DEBUG | Ordering metrics from heartbeat path. |

**Periodic heavy work:** **`VerifyRegistryIntegrity`** runs from **`EmitHeartbeatIfDue`** at most every **60s** per IEA — does **not** log its own event name; watch **`REGISTRY_BROKER_DIVERGENCE*`** and **`ORDER_REGISTRY_METRICS`** around the same wall time.

---

## 4. Bootstrap & supervisory (state machine)

**Bootstrap** (`InstrumentExecutionAuthority.BootstrapPhase4.cs`): **`BOOTSTRAP_STARTED`**, **`BOOTSTRAP_CLASSIFIED`**, **`BOOTSTRAP_DECISION_*`**, **`BOOTSTRAP_ADOPTION_COMPLETED`**, **`BOOTSTRAP_READY_TO_RESUME`**, **`BOOTSTRAP_HALTED`**, **`BOOTSTRAP_SNAPSHOT_STALE`**, **`BOOTSTRAP_METRICS`**, etc.

**Supervisory** (`InstrumentExecutionAuthority.SupervisoryPhase5.cs`): **`SUPERVISORY_*`**, **`INSTRUMENT_COOLDOWN_*`**, **`INSTRUMENT_SUSPENDED`**, **`INSTRUMENT_HALTED`**, **`OPERATOR_ACK_*`**, **`INSTRUMENT_KILL_SWITCH_ACTIVATED`**, **`SUPERVISORY_METRICS`**.

---

## 5. Adapter binding & routing

| Event | Level |
|--------|--------|
| **`IEA_BINDING`** | INFO — account, `execution_instrument_key`, `iea_instance_id`. |
| **`IEA_EXEC_UPDATE_ROUTED`** | INFO — execution update routed to IEA (sampled / diagnostic path in adapter). |

---

## 6. Practical grep / jq recipes (ENGINE JSONL)

Assume JSON lines use a field like **`event`** or **`event_type`** for the semantic name (adjust to your pipeline schema).

```text
# All IEA-prefixed engine events
grep "IEA_" robot_ENGINE.jsonl

# Adoption scan rate per instrument
grep "ADOPTION_SCAN_START" robot_ENGINE.jsonl

# Deferred adoption retries from engine heartbeat
grep "ADOPTION_DEFERRAL_HEARTBEAT_RETRY" robot_ENGINE.jsonl

# Worker / queue failures
grep "IEA_QUEUE_WORKER_ERROR\|EXECUTION_QUEUE_POISON_DETECTED\|EXECUTION_COMMAND_STALLED" robot_ENGINE.jsonl

# Critical ownership / stale
grep "STALE_QTSW2_ORDER_DETECTED\|REGISTRY_BROKER_DIVERGENCE\|UNOWNED_LIVE_ORDER_DETECTED" robot_ENGINE.jsonl
```

---

## 7. Related doc

- **`IEA_LOOPS_AND_TRIGGERS_2026-03-23.md`** — which code paths retrigger adoption, registry integrity, and the worker loop (blocking `TryTake`, not a spin).
- **`ENGINE_CPU_PROFILE.md`** — engine tick attribution + **`scan_wall_ms`** when profiling flag file is present.

---

## 8. `RobotEventTypes` footgun (fixed)

`GetLevel()` treats any event name containing **`HEARTBEAT`** as **DEBUG** unless the type is in the map. **`ADOPTION_DEFERRAL_HEARTBEAT_RETRY`** is now explicitly **INFO** so deferred-adoption retries appear on default INFO ENGINE pipelines.
