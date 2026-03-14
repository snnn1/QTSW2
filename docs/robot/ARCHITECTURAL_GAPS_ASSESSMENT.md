# QTSW2 Architectural Gaps Assessment

Assessment of five critical architectural risks against the current codebase. Each section: **What exists**, **Gaps**, **Severity**, **Recommendation**.

---

## 1. State Divergence After Restart

### What Exists

| Component | Location | Behavior |
|-----------|----------|----------|
| **BootstrapPhase4** | `InstrumentExecutionAuthority.BootstrapPhase4.cs`, `BootstrapPhase4Types.cs` | `BeginBootstrapForInstrument` → snapshot callback → `ProcessBootstrapResult` → `BootstrapClassifier.Classify` → `BootstrapDecider.Decide` |
| **BootstrapClassification** | `BootstrapPhase4Types.cs` | 9 buckets: `CLEAN_START`, `RESUME_WITH_NO_POSITION_NO_ORDERS`, `ADOPTION_REQUIRED`, `POSITION_PRESENT_NO_OWNED_ORDERS`, `LIVE_ORDERS_PRESENT_NO_POSITION`, `JOURNAL_RUNTIME_DIVERGENCE`, `REGISTRY_BROKER_DIVERGENCE_ON_START`, `MANUAL_INTERVENTION_PRESENT`, `UNSAFE_STARTUP_AMBIGUITY` |
| **BootstrapDecision** | Same | `RESUME`, `ADOPT`, `RECONCILE_THEN_RESUME`, `FLATTEN_THEN_RECONSTRUCT`, `HALT` |
| **BootstrapSnapshot** | Same | `BrokerPositionQty`, `BrokerWorkingOrderCount`, `JournalQty`, `UnownedLiveOrderCount`, `RegistrySnapshot`, `JournalSnapshot`, `RuntimeIntentSnapshot` |
| **HALT on ambiguity** | `BootstrapDecider` | `MANUAL_INTERVENTION_PRESENT` and `UNSAFE_STARTUP_AMBIGUITY` → `HALT` |

### Gaps

1. **No explicit `UNKNOWN_REQUIRES_SUPERVISOR`** — `UNSAFE_STARTUP_AMBIGUITY` maps to `HALT` but is not explicitly named as supervisor-required.
2. **No Startup Reconciliation Report** — No structured per-instrument report with `broker_qty`, `journal_qty`, `working_order_count`, `protective_status`, `reconstructed_lifecycle_state`, `reconciliation_status`, `action_taken`.
3. **Lifecycle not reconstructed at startup** — IEA `_intentLifecycleByIntentId` is in-memory only; restart clears it. Bootstrap does not rebuild lifecycle from journal/registry.
4. **Protective-status not in snapshot** — `BootstrapSnapshot` has no `protective_status`; adoption logic infers from registry, not explicit stop/target presence.
5. **Per-instrument gating** — Bootstrap is instrument-scoped, but "do not allow startup to continue until every instrument is classified" is not explicitly enforced at engine level (depends on caller).

### Severity

**High.** State divergence after restart is the main "silent kill" mode. Bootstrap exists and HALT is fail-closed, but the report and lifecycle reconstruction are missing.

### Recommendation

1. Add `StartupReconciliationReport` type and emit per instrument after classification.
2. Add `UNKNOWN_REQUIRES_SUPERVISOR` as explicit classification; map to `HALT` and require supervisor before resume.
3. Rebuild lifecycle from journal/registry during bootstrap adoption; populate `_intentLifecycleByIntentId` from persisted facts.
4. Add `protective_status` to snapshot (stop present, target present, qty match) from broker truth.

---

## 2. Command Queue Stall / Poisoned Authority

### What Exists

| Component | Location | Behavior |
|-----------|----------|----------|
| **QueueDepth** | `InstrumentExecutionAuthority.cs` | `QueueDepth => _executionQueue.Count`; exposed in `IEA_HEARTBEAT` |
| **MAX_QUEUE_DEPTH_FOR_ENQUEUE** | Same | 500; overflow → `_instrumentBlocked`, `IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW` |
| **EnqueueAndWait timeout** | Same | 5000 ms default; timeout → `_instrumentBlocked`, `IEA_ENQUEUE_AND_WAIT_TIMEOUT` |
| **Deadlock guard** | Same | If called from worker thread, executes inline |
| **blockInstrumentCallback** | `NinjaTraderSimAdapter`, `RobotEngine` | On timeout/overflow → stand down streams, freeze instrument |
| **enqueue_sequence, last_processed_sequence** | IEA heartbeat | Monotonic sequence for debugging |

### Gaps

1. **No age of oldest command** — Queue depth is logged, but not how long the oldest item has been queued.
2. **No current command type / runtime** — No visibility into what the worker is doing or how long it has been running.
3. **No consecutive failures** — No count of consecutive command failures.
4. **No last successful completion time** — Cannot detect "no successful completion for Z seconds while active."
5. **No poison-command policy** — On timeout, the caller gets `(false, default)` and instrument is blocked; the worker may still be running the hung command. No dead-letter, no explicit "move to failed-command log."
6. **Emergency flatten goes through queue** — `EnqueueFlattenAndWait` uses `EnqueueAndWait`; when instrument is blocked, flatten is rejected. No bypass for emergency flatten.

### Severity

**High.** A hung worker blocks all commands. Instrument block is fail-closed, but there is no way to flatten an open position when blocked.

### Recommendation

1. Add queue health: `oldest_command_enqueued_utc`, `current_command_type`, `current_command_runtime_ms`, `consecutive_failures`, `last_successful_completion_utc`.
2. Add thresholds: command runtime > X ms → warn; oldest age > Y ms → critical; no success for Z s → recovery trigger.
3. Add poison-command policy: on timeout, emit `EXECUTION_COMMAND_STALLED`, log to failed-command store, unblock queue (worker continues; next item processed), trigger supervisor.
4. Add emergency flatten bypass: when instrument blocked and position exists, allow `account.Flatten` from strategy thread (or dedicated emergency path) outside the queue.

---

## 3. Missing or Lost Protective Coverage

### What Exists

| Component | Location | Behavior |
|-----------|----------|----------|
| **ProtectionState** | `NinjaTraderSimAdapter.cs` | `None`, `Enqueued`, `Executing`, `Submitted`, `Working` |
| **BE_STOP_VISIBILITY_TIMEOUT** | `NinjaTraderSimAdapter.NT.cs` | 5 s wall-clock; if stop not visible after entry fill → flatten |
| **CheckEnqueuedBacklog** | Same | Timeout for Enqueued/Executing/Submitted; triggers flatten |
| **ScanAndAdoptExistingProtectives** | `InstrumentExecutionAuthority.NT.cs` | On bootstrap, adopt working stops from `account.Orders` |
| **ProtectiveStopAcknowledged, ProtectiveTargetAcknowledged** | OrderInfo | Set when protective order update received |

### Gaps

1. **Not broker-truth based** — ProtectionState is internal; BE_STOP_VISIBILITY checks `account.Orders` but logic is mixed with journal/OrderMap.
2. **No continuous protective audit** — No periodic "for each open position: broker qty, stop present, target present, stop qty matches exposure, target qty matches exposure."
3. **No PROTECTIVE_COVERAGE_BROKEN event** — BE_STOP_VISIBILITY_TIMEOUT exists but no explicit "coverage broken" classification.
4. **No block-new-entries on broken coverage** — Flatten is triggered; no explicit "block new entries for this instrument until repaired."
5. **Partial fill protectives** — Logic exists for resize on partial fill, but no explicit audit that stop qty == exposure.

### Severity

**Critical.** Unprotected live futures position is the highest-severity case.

### Recommendation

1. Add continuous protective audit: for each open position (from broker), verify stop exists, target exists, qty matches, prices sane.
2. Emit `PROTECTIVE_COVERAGE_BROKEN` when coverage is missing or mismatched.
3. Block new entries for that instrument until repaired or flattened.
4. Use broker position as source of truth; cross-check with journal for intent attribution.

---

## 4. Cross-Source Truth Mismatch That Never Converges

### What Exists

| Component | Location | Behavior |
|-----------|----------|----------|
| **RecoveryPhase3** | `InstrumentExecutionAuthority.RecoveryPhase3.cs` | Reconstruction, classification, FLATTEN/RESUME/ADOPT/HALT |
| **BootstrapClassifier** | `BootstrapPhase4Types.cs` | Detects `JOURNAL_RUNTIME_DIVERGENCE` (brokerQty != journalQty) |
| **ReconstructionClassification** | RecoveryPhase3 | `broker_ahead`, `journal_ahead`, etc. |

### Gaps

1. **No mismatch escalation state machine** — No `first_detected_utc`, `last_detected_utc`, `mismatch_type`, `retry_count`, `current_resolution_phase`.
2. **No transient vs persistent vs irrecoverable** — No time-based classification (e.g. <5 s transient, 5–30 s persistent, >30 s irrecoverable).
3. **No FAIL_CLOSED on persistent** — Recovery retries; no explicit "lock instrument, alert, fail closed" when mismatch persists.
4. **Oscillation risk** — Same instrument can be reclassified repeatedly; no backoff or escalation.

### Severity

**High.** Persistent non-convergence can cause oscillating behavior (cancel → rebuild → flatten → reclassify).

### Recommendation

1. Add MismatchEscalationState: `DETECTED`, `RECOVERY_IN_PROGRESS`, `RECONCILIATION_RETRYING`, `PERSISTENT_MISMATCH`, `FAIL_CLOSED`.
2. Track per instrument: `first_detected_utc`, `last_detected_utc`, `mismatch_type`, `retry_count`.
3. Define thresholds: transient (<5 s), persistent (5–30 s), irrecoverable (>30 s or same mismatch after restart).
4. On persistent: transition to `FAIL_CLOSED`, lock instrument, alert, stop retries.

---

## 5. No Canonical Event Truth for Replay and Forensics

### What Exists

| Component | Location | Behavior |
|-----------|----------|----------|
| **RobotLogger / RobotEvents** | Multiple | Structured JSON logs with event types |
| **ExecutionJournal** | `ExecutionJournal.cs` | Disk lifecycle (EntryFilled, TradeCompleted) |
| **IEA_HEARTBEAT** | `InstrumentExecutionAuthority.cs` | `queue_depth`, `enqueue_sequence`, `last_processed_sequence` |
| **Intent lifecycle events** | `InstrumentExecutionAuthority.IntentLifecycle.cs` | `INTENT_LIFECYCLE_TRANSITION`, `INTENT_LIFECYCLE_TRANSITION_INVALID` |
| **Execution ordering events** | Recent hardening | `EXECUTION_DEFERRED_*`, `EXECUTION_DUPLICATE_DETECTED`, `EXECUTION_RESOLUTION_TIMEOUT` |

### Gaps

1. **Logs are not append-only event stream** — Logs are the primary record; no dedicated event store with sequence numbers.
2. **No replay-driven reconstruction test** — No test that loads event stream, rebuilds state from zero, and asserts equality with persisted state.
3. **Event families not explicit** — Many events exist but not organized as `COMMAND_RECEIVED`, `COMMAND_DISPATCHED`, `ORDER_REGISTERED`, `EXECUTION_OBSERVED`, `LIFECYCLE_TRANSITIONED`, etc.
4. **Restart logic partly trust-based** — Bootstrap uses journal + registry + broker; no proof that replay produces same result.

### Severity

**Medium.** Operational today; forensics and deterministic replay are gaps for prop deployment.

### Recommendation

1. Define canonical event families and ensure all key transitions emit them.
2. Add append-only event stream (file or DB) with sequence numbers per session/instrument.
3. Implement replay-driven reconstruction test: load stream → rebuild state → compare to final state.
4. Document roadmap: "Execution Event Stream" as next phase.

---

## Summary Matrix

| Gap | Exists | Severity | Priority |
|-----|--------|----------|----------|
| 1. State divergence / restart | Bootstrap, classification, HALT | High | Add report, lifecycle rebuild, UNKNOWN_REQUIRES_SUPERVISOR |
| 2. Queue stall / poisoned authority | QueueDepth, timeout, block | High | Add health metrics, poison policy, emergency flatten bypass |
| 3. Protective coverage | ProtectionState, BE_STOP_VISIBILITY | Critical | Add continuous audit, PROTECTIVE_COVERAGE_BROKEN, block entries |
| 4. Mismatch convergence | Recovery, classification | High | Add escalation state machine, persistent → FAIL_CLOSED |
| 5. Canonical event truth | Logs, journal, events | Medium | Add event stream, replay test (roadmap) |

---

## References

- `docs/robot/IEA_BOOTSTRAP_PHASE4.md`
- `docs/robot/IEA_RECOVERY_PHASE3.md`
- `docs/robot/IEA_INVARIANTS_VERIFICATION.md`
- `docs/robot/IEA_AUDIT_GAPS_FIX_PLAN.md`
- `docs/robot/incidents/2026-02-18_BREAK_EVEN_FULL_ASSESSMENT.md` (EnqueueAndWait timeout)
- `docs/robot/incidents/2026-03-13_MES_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md` (broker_ahead, BE_STOP_VISIBILITY)
