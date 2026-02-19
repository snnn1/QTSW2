# IEA Replay Contract

**Purpose:** Deterministic replay contract for Instrument Execution Authority (IEA). Audited against implementation. Reference: [IEA_RUNTIME_DECONSTRUCTION.md](IEA_RUNTIME_DECONSTRUCTION.md).

---

## 1. Minimum Event Types Required for Replay

### Audit Replay (default)

Replays ExecutionUpdate, OrderUpdate, Tick only. No EnqueueAndWait. No broker submission. No wall clock required.

| Event | Required Fields | Ordering | IEA Entry Point | Omission Breaks Determinism? |
|-------|-----------------|----------|-----------------|------------------------------|
| **IntentRegistered** | sequence (long), intentId, Intent (full) | Must precede ExecutionUpdate/Fill/BE for that intent | RegisterIntent | Yes |
| **IntentPolicyRegistered** | sequence (long), intentId, expectedQty, maxQty, canonical, execution, policySource | Must precede EntrySubmissionRequest for that intent | RegisterIntentPolicy | Yes (only for Full-System) |
| **ExecutionUpdate** | sequence (long), executionId (string?), orderId (string), fillPrice (decimal), fillQuantity (int), executionTime (DateTimeOffset), tag/intentId (string?), executionInstrumentKey (string). [NOT CURRENTLY TRUE — REQUIRES CODE CHANGE: Current code receives NT execution/order objects; ProcessExecutionUpdate(execution, order) expects NT types. Replay harness must supply minimal shape; adapter must accept replay payload or mock objects.] | By (source, sequence) | EnqueueExecutionUpdate | Yes |
| **OrderUpdate** | sequence (long), orderId (string), orderState (string), filled (int?), quantity (int?), tag/intentId (string?). [NOT CURRENTLY TRUE — REQUIRES CODE CHANGE: Current code receives NT order/orderUpdate objects.] | By (source, sequence) | EnqueueOrderUpdate | Yes |
| **Tick/MarketData** | sequence (long), tickPrice, tickTimeFromEvent (DateTimeOffset? nullable), executionInstrument | By (source, sequence) | EvaluateBreakEven | Yes |

### Full-System Replay (optional)

Adds EntrySubmissionRequest, FlattenRequest. Requires broker simulation. Requires deterministic OCO. Requires simulated wall clock.

| Event | Required Fields | Ordering | IEA Entry Point | Omission Breaks Determinism? |
|-------|-----------------|----------|-----------------|------------------------------|
| **EntrySubmissionRequest** | sequence (long), intentId, instrument, direction, stopPrice, quantity, ocoGroup | By (source, sequence) | EnqueueAndWait → SubmitStopEntryOrder | Yes (Full-System only) |
| **FlattenRequest** | sequence (long), intentId, instrument | By (source, sequence) | EnqueueFlattenAndWait | Yes (Full-System only) |

**Ordering:** Ordering is by (source, sequence). Timestamps are authoritative inputs for time-based rules (BE throttles, dedup timestamps, eviction cutoffs). [REQUIRES INJECTION OF CLOCK PROVIDER: Current code uses DateTimeOffset.UtcNow; no event clock injection exists.]

**tickTimeFromEvent:** Nullable. When null, replay MUST synthesize deterministic event time using (source, sequence), e.g. `baseTime + sequence * 1ms`. baseTime MUST be derived from: earliest observed event timestamp in replay file for that source/instrument, OR fixed constant from harness config.

**Time authority:** For ExecutionUpdate-driven logic (dedup key when ExecutionId missing), executionTime is authoritative. For BE logic, tickTimeFromEvent (or synthesized) is authoritative. Current code uses execution.Time for dedup composite key — [NOT CURRENTLY TRUE — REQUIRES CODE CHANGE: Dedup key must use executionTime from event, not raw object.]

---

## 2. UtcNow / Guid Branching Audit

| File | Method | Exact Usage | Branch/State Affected | Breaks Audit Replay? | Smallest Change |
|------|--------|-------------|------------------------|----------------------|-----------------|
| InstrumentExecutionAuthority.NT.cs | TryMarkAndCheckDuplicate | `_processedExecutionIds.TryAdd(key, DateTimeOffset.UtcNow)` | Dedup value; eviction | Yes | [REQUIRES INJECTION OF CLOCK PROVIDER] Replace with NowEvent() |
| InstrumentExecutionAuthority.NT.cs | EvictDedupEntries | `cutoff = DateTimeOffset.UtcNow.AddMinutes(-DEDUP_MAX_AGE_MINUTES)` | Eviction cutoff | Yes | [REQUIRES INJECTION OF CLOCK PROVIDER] Replace with NowEvent(); OrderBy key |
| InstrumentExecutionAuthority.NT.cs | EvaluateBreakEven | `eventTime = tickTimeFromEvent ?? DateTimeOffset.UtcNow` | BE dedupe fallback | Yes | [REQUIRES INJECTION OF CLOCK PROVIDER] Use NowEvent() |
| InstrumentExecutionAuthority.NT.cs | EvaluateBreakEvenCore | `utcNow = DateTimeOffset.UtcNow`; BE_SCAN_THROTTLE, BE_DEDUPE_FALLBACK, BE_MODIFY_ATTEMPT_INTERVAL | Throttle branches | Yes | [REQUIRES INJECTION OF CLOCK PROVIDER] Replace with NowEvent() |
| InstrumentExecutionAuthority.cs | WorkerLoop | `_lastMutationUtc = DateTimeOffset.UtcNow` | Heartbeat | No | Optional |
| InstrumentExecutionAuthority.cs | WorkerLoop | `Log?.Write(..., DateTimeOffset.UtcNow, ...)` | IEA_QUEUE_WORKER_ERROR | No | Optional |
| InstrumentExecutionAuthority.cs | EnqueueAndWait | `utcNow = DateTimeOffset.UtcNow` (overflow, timeout) | Block callback | Full-System only — Audit Replay does not exercise EnqueueAndWait | [REQUIRES INJECTION OF CLOCK PROVIDER] NowWall() for Full-System |
| InstrumentExecutionAuthority.cs | EnqueueFlattenAndWait | `utcNow = DateTimeOffset.UtcNow` | Failure timestamp | No | Optional |
| InstrumentExecutionAuthority.cs | EmitHeartbeatIfDue | `now = DateTimeOffset.UtcNow` | Heartbeat throttle | No | Optional |
| RobotOrderIds.cs | EncodeEntryOco | `Guid.NewGuid():N` | Broker OCO group identity | Full-System only — Audit Replay has no broker calls. OCO does not affect IEA internal branching. | [REQUIRES CODE CHANGE] Add deterministicSeed for Full-System Replay |

**Logging-only (no code change):** NinjaTraderSimAdapter, ExecutionJournal, KillSwitch, InstrumentIntentCoordinator.

---

## 3. Unordered Iteration Audit

| File | Method | Collection | Affects Branching/Output? | Smallest Change |
|------|--------|------------|---------------------------|-----------------|
| InstrumentExecutionAuthority.NT.cs | TryAggregateWithExistingOrders | IntentMap (ConcurrentDictionary) | Yes — order determines toAggregate, allIntentIds, primaryIntentId | `.OrderBy(kvp => kvp.Key)` |
| InstrumentExecutionAuthority.NT.cs | FindOppositeEntryIntentId | IntentMap | Yes — returns first match; multiple matches non-deterministic | `.OrderBy(kvp => kvp.Key)` |
| InstrumentExecutionAuthority.NT.cs | EvictDedupEntries | _processedExecutionIds (ConcurrentDictionary) | Yes — eviction order affects which keys remain | `.OrderBy(kvp => kvp.Key)` |
| InstrumentExecutionAuthority.cs | GetPositionState | OrderMap (ConcurrentDictionary) | No — sums are commutative | [STABILITY IMPROVEMENT — NOT REQUIRED FOR DETERMINISM] |
| InstrumentExecutionAuthority.cs | GetBracketState | OrderMap | [UNVERIFIED — REQUIRES CODE CONFIRMATION: Downstream use of WorkingStopOrderIds/WorkingStopPrices order not audited.] | `.OrderBy(kvp => kvp.Key)` if downstream consumes order |

**Already deterministic:** AllocateFillToIntents uses `intentIds.OrderBy(id => id)`.

---

## 4. Off-Worker Mutation Audit

| File | Method | Calling Context | Worker Logic That Could Race | Breaks Replay? | Smallest Change |
|------|--------|-----------------|------------------------------|----------------|-----------------|
| NinjaTraderSimAdapter.cs | RegisterIntent | StreamStateMachine / engine | TryAggregateWithExistingOrders, HandleEntryFill, ProcessExecutionUpdate | Replay harness contract requirement. Engine ordering guarantees registration before submission in live. | Harness: IntentRegistered must precede ExecutionUpdate for that intent |
| NinjaTraderSimAdapter.cs | RegisterIntentPolicy | StreamStateMachine / engine | TryAggregateWithExistingOrders, AllocateFillToIntents, SubmitStopEntryOrder | Same | Harness: IntentPolicyRegistered must precede EntrySubmissionRequest |
| InstrumentExecutionAuthority.NT.cs | TryMarkAndCheckDuplicate | NT event thread (before enqueue) | ProcessExecutionUpdate | No — dedup key deterministic if events replayed in order | Contract: dedup on single lane; replay calls same entry point in (source, sequence) order |
| InstrumentExecutionAuthority.cs | EnqueueAndWait | Caller thread | Worker continues; caller rejects | Full-System only; Audit Replay does not exercise | No change for Audit Replay |

**Dedup contract note:** Dedup check and insert occur on NT thread before enqueue. Replay must call same dedup entry point before enqueue in (source, sequence) order.

---

## 5. Summary

### A. Counts Table

| Category | Count | Determinism Required? | Mitigation |
|----------|-------|------------------------|------------|
| UtcNow / Guid branching points | 10 | Yes (6 for Audit Replay; 4 logging/Full-System) | [REQUIRES INJECTION OF CLOCK PROVIDER] |
| Unordered iteration points | 5 | Yes for 3 (TryAggregate, FindOpposite, EvictDedup); 1 stability; 1 unverified | `.OrderBy(kvp => kvp.Key)` |
| Off-worker mutation races | 4 | Yes — harness preconditions | Harness validates |

### B. Minimal Change Checklist (Implementation Order)

1. **[REQUIRES CODE CHANGE]** InstrumentExecutionAuthority.cs, InstrumentExecutionAuthority.NT.cs — Add NowEvent() and NowWall(). BE/dedup use NowEvent(); EnqueueAndWait uses NowWall(). Default to DateTimeOffset.UtcNow when replay disabled.
2. **[REQUIRES CODE CHANGE — Full-System only]** RobotOrderIds.cs — Add EncodeEntryOco(..., string? deterministicSeed = null).
3. **[REQUIRES CODE CHANGE]** InstrumentExecutionAuthority.NT.cs — TryAggregateWithExistingOrders: order IntentMap by key.
4. **[REQUIRES CODE CHANGE]** InstrumentExecutionAuthority.NT.cs — FindOppositeEntryIntentId: order IntentMap by key.
5. **[REQUIRES CODE CHANGE]** InstrumentExecutionAuthority.NT.cs — EvictDedupEntries: order dedup keys before eviction.
6. **Replay harness** — Every event carries sequence; replay in (source, sequence) order; fail fast on gaps.
7. **Replay harness** — Validate IntentRegistered precedes ExecutionUpdate for that intent; hard-fail if violated.

### C. Replay Readiness Verdict

**Contract Preconditions (Strict)**

- IntentRegistered must precede ExecutionUpdate/Fill/BE for that intent. Harness validates.
- IntentPolicyRegistered must precede EntrySubmissionRequest (Full-System only).
- Events replayed in (source, sequence) order.
- Tick events: tickTimeFromEvent or harness synthesizes using (source, sequence).

**Clock Semantics**

[REQUIRES INJECTION OF CLOCK PROVIDER — NOT CURRENTLY IMPLEMENTED]

- **Event Clock (NowEvent())** — BE throttles, dedup insert, dedup eviction. Live: UtcNow. Replay: event timestamp.
- **Wall Clock (NowWall())** — EnqueueAndWait timeouts. Full-System only. Audit Replay does not exercise.

**Replay Modes**

- **Audit Replay (default):** ExecutionUpdate, OrderUpdate, Tick. No EnqueueAndWait. No broker submission. No wall clock. OCO determinism irrelevant.
- **Full-System Replay (optional):** Adds EntrySubmissionRequest, FlattenRequest. Requires deterministic OCO. Requires simulated wall clock.

**Replay Harness Must Reject**

- ExecutionUpdate or OrderUpdate for intentId with no prior IntentRegistered.
- EntrySubmissionRequest for intentId with no prior IntentPolicyRegistered.
- Out-of-order events.
- Sequence gaps.

**Verdict**

- **Deterministic replay possible with local changes?** Yes.
- **Primary risk:** BE throttle and dedup use UtcNow; no clock injection exists.
- **Primary blocker:** Adapter expects NT execution/order objects; replay payload shape requires code change.

---

## 6. Replay Acceptance Criteria

Replay is valid only if:

- Identical input event stream produces identical IEA state snapshot (OrderMap, IntentMap, dedup state, BE state) across 3 runs.
- Final IEA state snapshot hash is identical across runs.
- [REQUIRES CODE CHANGE] No DateTimeOffset.UtcNow or Guid.NewGuid() in branch-relevant paths (verified by audit).
- [REQUIRES CODE CHANGE] BE throttles and dedup use Event Clock.

**Measurement:** Determinism defined as identical IEA state snapshot. IEA-emitted events (e.g. fill handling, BE triggers) are secondary if harness records them.

---

## 7. Do NOT Modify These Semantics

The replay contract must not alter live trading semantics. All determinism changes must be injectable and default to existing behavior when replay mode is disabled.

---

*End of IEA Replay Contract*
