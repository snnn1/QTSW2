# NinjaTrader CPU Audit — Phases 1–7 (Code Analysis)

**Date:** 2026-03-19  
**Scope:** RobotCore_For_NinjaTrader (production NT addon)  
**Method:** Static code analysis. Runtime instrumentation required for measured numbers.

---

## Phase 1 — Account Snapshot Ground Truth

### CALL_SITE_AUDIT

| File | Class | Method | Call Frequency | Trigger | Thread |
|------|-------|--------|----------------|---------|--------|
| `MismatchEscalationCoordinator.cs` | MismatchEscalationCoordinator | OnAuditTick | 1/sec per engine | Timer (1000 ms) | ThreadPool |
| `MismatchEscalationCoordinator.cs` | (via _getMismatchObservations) | AssembleMismatchObservations | 1/sec per engine | Same timer tick | ThreadPool |
| `ProtectiveCoverageCoordinator.cs` | ProtectiveCoverageCoordinator | OnAuditTick | 1/sec per engine | Timer (1000 ms) | ThreadPool |
| `ReconciliationRunner.cs` | ReconciliationRunner | RunInternal | 1/60 sec per engine | RunPeriodicThrottle from Tick | NT (inside _engineLock) |
| `RobotEngine.cs` | RobotEngine | TickInternal (second reconciliation) | 1× per engine, ~5 min after recovery | Conditional in Tick | NT (inside _engineLock) |
| `RobotEngine.cs` | RobotEngine | RunRecovery (RunRecoveryLegacy) | 1× per disconnect recovery | Recovery path | NT (inside _engineLock) |
| `RobotEngine.cs` | RobotEngine | Phase 5 position reconciliation | 1× when frozen instrument check | IsInstrumentBlockedForReentry → Phase 5 | NT |
| `StreamStateMachine.cs` | StreamStateMachine | HandleRangeLockedState | Per stream, when _entryOrderRecoveryState.IsPending | stream.Tick | NT (inside _engineLock) |
| `StreamStateMachine.cs` | StreamStateMachine | SubmitStopEntryBracketsAtLock | Per stream, when pending resubmit + position check | Before bracket submit | NT (inside _engineLock) |
| `StreamStateMachine.cs` | StreamStateMachine | HandleForcedFlatten | Per stream, after flatten (exposure verify) | Forced flatten path | NT (inside _engineLock) |

### Critical Finding: MismatchEscalationCoordinator Double Snapshot

**MismatchEscalationCoordinator.OnAuditTick** calls:
1. `_getSnapshot()` → GetAccountSnapshot
2. `_getMismatchObservations()` → AssembleMismatchObservations → GetAccountSnapshot

**Result:** 3 snapshot calls per engine per second from coordinators alone (1 Protective + 2 Mismatch).

### Theoretical Snapshot Rate (7 engines)

| Source | Calls/sec (steady) | Burst potential |
|-------|--------------------|-----------------|
| MismatchEscalationCoordinator (direct) | 7 | 7 in same ms if timers aligned |
| MismatchEscalationCoordinator (AssembleMismatchObservations) | 7 | 7 in same ms |
| ProtectiveCoverageCoordinator | 7 | 7 in same ms |
| ReconciliationRunner | 0.117 (7/60) | 7 in same Tick if all engines tick together |
| Second reconciliation | ~0.023 (7/300) | 7 in same Tick |
| RunRecoveryLegacy | rare (disconnect only) | — |
| Phase 5 | rare (frozen check) | — |
| StreamStateMachine (3 sites) | conditional | Up to 7× streams × 3 when conditions met |
| **Total steady (coordinators only)** | **21** | **21 in same ms if aligned** |

### Snapshot Cost (from code)

| Metric | Source | Value |
|--------|--------|-------|
| Positions iteration | `account.Positions` foreach | O(N positions) |
| Orders iteration | `account.Orders` foreach (Working/Accepted only) | O(N orders) |
| Allocations per snapshot | List&lt;PositionSnapshot&gt;, List&lt;WorkingOrderSnapshot&gt; | ~2 lists + N+M objects |
| NT API | NinjaTrader Account access | Unknown; may involve locks |

**Instrumentation needed:** `timestamp, caller, duration_ms, thread_id, positions_count, orders_count`

### Threading Model

| Caller | Thread | Can run concurrently? |
|--------|--------|------------------------|
| Coordinator timers (Mismatch, Protective) | ThreadPool | **YES** — 14–21 callbacks can run in parallel |
| ReconciliationRunner | NinjaTrader (Tick) | Serialized by _engineLock per engine; 7 engines = 7 Ticks can interleave |
| StreamStateMachine | NinjaTrader (Tick) | Inside _engineLock; serialized per engine |
| RunRecovery, Phase 5 | NinjaTrader | Inside _engineLock |

**Conclusion:** Coordinator timers run on ThreadPool. All 7 engines' timers can fire within milliseconds. **Up to 21 GetAccountSnapshot calls can run concurrently** if timers align.

---

## Phase 2 — Coordinator Behavior Audit

### Do both coordinators call snapshot independently every tick?

| Coordinator | Snapshot calls per tick | Shared state? | Caching? |
|-------------|-------------------------|--------------|----------|
| MismatchEscalationCoordinator | **2** (direct + AssembleMismatchObservations) | No | No |
| ProtectiveCoverageCoordinator | 1 | No | No |

**Confirmed:** No shared state, no caching, no reuse. Mismatch coordinator redundantly fetches snapshot twice per tick.

### Work Overlap

| Coordinator | Depends on positions | Depends on orders | Depends on journal | Depends on IEA |
|-------------|----------------------|-------------------|--------------------|----------------|
| MismatchEscalationCoordinator | Yes (brokerQtyByInst) | Yes (brokerWorkingByInst) | Yes (GetOpenJournalEntriesByInstrument) | Yes (GetOwnedPlusAdoptedWorkingCount) |
| ProtectiveCoverageCoordinator | Yes (positions for instrument) | Yes (workingOrders for instrument) | No | No (uses activeIntentIds from engine) |

**Overlap:** Both need positions + orders. Mismatch also needs journal + IEA. **Same broker state (positions, orders) is fetched 3× per second per engine** (1 Protective + 2 Mismatch).

### Delta vs Full Recompute

| Coordinator | Mode | Tracks incremental? |
|-------------|------|---------------------|
| MismatchEscalationCoordinator | Full recompute | No — re-evaluates all instruments every tick |
| ProtectiveCoverageCoordinator | Full recompute | No — audits all active instruments every tick |

**Could operate on diffs?** Not without architectural change. Both assume fresh snapshot each tick.

### Per-Instrument Loops

| Coordinator | Instruments processed | Filter |
|-------------|-----------------------|--------|
| MismatchEscalationCoordinator | allInstruments (broker + journal union) | Active + from positions |
| ProtectiveCoverageCoordinator | _getActiveInstruments() + from positions if empty | Active only |

**Typical N:** 1–7 instruments per engine. Cost is O(N) per coordinator; snapshot cost dominates.

---

## Phase 3 — Logging Pressure Audit

### Event Volume (from config + code)

| event_type | Rate limit | Est. count/sec (7 engines) | Notes |
|------------|------------|---------------------------|-------|
| ENGINE_TICK_CALLSITE | 1/5s per engine | 1.4 | Rate-limited |
| BAR_ACCEPTED | 12/min | 1.4 | Per engine |
| BAR_DELIVERY_TO_STREAM | 12/min | 1.4 | Per engine |
| IEA_HEARTBEAT | 12/min | 1.4 | Per IEA |
| RECONCILIATION_MISMATCH_METRICS | Every Tick (1/min) | 0.12 | Per engine |
| PROTECTIVE_AUDIT_METRICS | Every Tick (1/min) | 0.12 | Per engine |
| RECONCILIATION_ORDER_SOURCE_BREAKDOWN | Dedupe 30s | variable | When broker≠IEA |
| (diagnostic) | enable_diagnostic_logs | +variable | BAR_ACCEPTED, SLOT_GATE, etc. |

**Instrumentation needed:** `event_type | count/sec | avg size bytes` from log pipeline.

### Queue Behavior (from code)

| Setting | Value | Source |
|---------|-------|--------|
| max_queue_size | 50000 | configs/robot/logging.json |
| flush_interval_ms | 500 | configs/robot/logging.json |
| max_batch_per_flush | 2000 | configs/robot/logging.json |
| Block on enqueue? | No | RobotLoggingService.Log is non-blocking |
| Drop when full? | Yes — DEBUG, then INFO | Backpressure at >= MAX_QUEUE_SIZE |
| WARN/ERROR/CRITICAL | Never dropped | — |

**Observed:** Queue reached 41,425 (Mar 18) — 83% of max. Near drop threshold.

### Backpressure

| Condition | Behavior |
|-----------|----------|
| queue.Count >= 50000 | Drop DEBUG, then INFO; emit rate-limited LOG_BACKPRESSURE |
| Worker slow | Queue grows; no backpressure to producers |

---

## Phase 4 — Timer Coordination Audit

### Timer Alignment

| Timer | Initial delay | Period | Constructor |
|-------|---------------|--------|-------------|
| MismatchEscalationCoordinator | 1000 ms | 1000 ms | `new Timer(OnAuditTick, null, 1000, 1000)` |
| ProtectiveCoverageCoordinator | 1000 ms | 1000 ms | `new Timer(OnAuditTick, null, 1000, 1000)` |
| InstrumentExecutionAuthority (stall) | 2000 ms | 2000 ms | `new Timer(CheckCommandStall, null, 2000, 2000)` |
| Engine heartbeat | 5000 ms | 5000 ms | `new Timer(_, 5s, 5s)` |

**Alignment:** All coordinator timers start at 1000 ms. If 7 engines are created within ~1 second, **all 14 coordinator timers (7×2) can fire in the same 1-second window**. System.Threading.Timer uses ThreadPool; callbacks are queued. **Burst:** Up to 21 snapshot calls (14 coordinator + 7 AssembleMismatchObservations) within ~50 ms if timers align.

### Timer Drift

System.Threading.Timer is not high-precision. Drift over time is typical. No explicit staggering.

### Thread Usage

| Timer | Thread | Lock |
|-------|--------|------|
| Coordinator timers | ThreadPool | None — do NOT hold _engineLock |
| IEA stall check | ThreadPool | None |
| Engine heartbeat | ThreadPool | None |
| Tick (OnBarUpdate) | NinjaTrader | _engineLock |

### Lock Contention

Coordinator timers do **not** run inside _engineLock. They call:
- `_getSnapshot()` → adapter → NT Account (may have NT-internal locks)
- `_getMismatchObservations()` → RobotEngine.AssembleMismatchObservations (no _engineLock)

**Risk:** 21 concurrent Account.Positions/Orders iterations → NT lock contention.

---

## Phase 5 — Timetable Poll Audit

### Where is poll executed?

| Step | Location | Inside _engineLock? |
|------|----------|---------------------|
| ShouldPoll check | TickInternal, before lock | No |
| PollAndParseTimetable | TickInternal, before lock | **No** |
| ReloadTimetableIfChanged | TickInternal, inside lock | Yes (but uses pre-parsed data) |

**File read + hash + parse:** Outside lock. Good.

### Poll Cost (from code)

| Operation | When | Cost |
|-----------|------|------|
| File.ReadAllBytes | Every poll (even if unchanged) | O(file size) |
| TimetableContentHasher.ComputeFromBytes | Every poll | Parse JSON + SHA256 |
| TimetableContract.LoadFromFile | Every poll (in PollAndParseTimetable) | Parse JSON again (LoadFromFile) |

**Redundant:** Poll() does ReadAllBytes + ComputeFromBytes (which parses). PollAndParseTimetable also calls LoadFromFile (parse again). **File read + 2 parses per poll.**

### Redundant Reads

**Yes.** File is read every 5 seconds per engine even when unchanged. Hash comparison happens after read. No "skip read if mtime unchanged" optimization.

### Cross-Engine Duplication

**Yes.** Each of 7 engines has its own TimetableFilePoller. **7 independent reads of the same file every 5 seconds** (when each engine's ShouldPoll is true). Bar closes stagger Tick, so polls may be spread over ~5 s, but clustering possible.

---

## Phase 6 — IEA Timer Audit

### CheckCommandStall Cost

| Operation | Code |
|-----------|------|
| Read _currentWorkStartedUtc | Volatile read |
| DateTimeOffset.UtcNow | ~0.001 ms |
| Elapsed check | 2 comparisons |
| Early return if < 8000 ms | Most ticks return here |
| Log.Write + callback | Only when stall detected |

**Duration:** &lt;0.1 ms typical. **Allocations:** 0 in hot path (early return). **Locks:** None.

**Conclusion:** IEA stall check is low impact. Verify with instrumentation if desired.

---

## Phase 7 — System-Level Scaling (Requires Runtime)

| Engines | Theoretical snapshot/sec | CPU (measure) | Log/sec (measure) |
|---------|--------------------------|--------------|-------------------|
| 1 | 3 (coordinators) + 0.02 (reconciliation) | — | — |
| 3 | 9 + 0.05 | — | — |
| 7 | 21 + 0.14 | — | — |

**Instrumentation needed:** Run with 1, 3, 7 engines; measure CPU, snapshot calls/sec, log events/sec. Determine if scaling is linear or superlinear.

---

## Meta Questions — Explicit Answers

### 25. Can the system function correctly if snapshots are delayed by 3 seconds?

**Yes.** Mismatch escalation and protective coverage are safety audits. A 3-second delay does not change correctness:
- Mismatch: Detects broker/journal divergence. 3 s delay → detection delayed by 3 s.
- Protective: Verifies stop coverage. 3 s delay → audit delayed by 3 s.
- Reconciliation: 60 s throttle. 3 s delay is negligible.

**Conclusion:** System is over-polling. 3 s snapshot age is acceptable.

### 26. Do any components REQUIRE real-time account state (&lt;1 s)?

**No.** No component has a hard &lt;1 s requirement:
- Coordinators: Policy-driven escalation; 1 s was chosen, not derived.
- Reconciliation: 60 s throttle.
- Stream recovery: Event-driven; snapshot is for verification.
- Forced flatten verify: One-time post-flatten check.

**Conclusion:** 1 s architecture is unjustified. 5 s or 10 s is sufficient.

### 27. Minimum viable refresh rate

| Component | Current | Minimum viable | Rationale |
|-----------|---------|-----------------|-----------|
| Mismatch detection | 1 s | 5–10 s | Escalation has persistence thresholds (2 consecutive, etc.) |
| Protective coverage | 1 s | 5–10 s | Stop missing is critical but 5 s detection is acceptable |

### 28. What breaks if snapshot frequency drops by 80%?

**Nothing.** Reducing from 21/s to ~4/s (e.g. 5 s coordinator interval):
- Mismatch: Detection delayed by up to 5 s. Escalation still works.
- Protective: Audit delayed by up to 5 s. No functional break.
- Reconciliation: Unchanged (60 s).
- Stream/Recovery: Unchanged (event-driven).

---

## Output Tables Summary

### CALL_SITE_AUDIT (Condensed)

| Caller | Freq (7 eng) | Thread | Concurrent? |
|--------|-------------|--------|-------------|
| MismatchEscalationCoordinator (×2) | 14/s | ThreadPool | Yes |
| ProtectiveCoverageCoordinator | 7/s | ThreadPool | Yes |
| ReconciliationRunner | 0.12/s | NT | Per-engine serial |
| Second reconciliation | 0.02/s | NT | Per-engine serial |
| StreamStateMachine (3 sites) | Conditional | NT | Per-engine serial |
| RunRecoveryLegacy | Rare | NT | Serial |
| Phase 5 | Rare | NT | Serial |

### COORDINATOR_COST

| Coordinator | Snapshots/tick | Positions used | Orders used | Journal used | Redundant? |
|-------------|----------------|----------------|-------------|-------------|------------|
| Mismatch (direct) | 1 | Yes | Yes | No | Yes — same as Assemble |
| Mismatch (Assemble) | 1 | Yes | Yes | Yes | — |
| Protective | 1 | Yes | Yes | No | — |

### LOG_VOLUME (Config-Derived)

| Metric | Value |
|--------|-------|
| max_queue_size | 50000 |
| flush_interval_ms | 500 |
| max_batch_per_flush | 2000 |
| Observed queue (Mar 18) | 41425 |
| Drop policy | DEBUG then INFO at full |
| Block on enqueue | No |

### TIMER_ALIGNMENT

| Timer | Period | Start | Alignment risk |
|-------|--------|-------|----------------|
| Mismatch | 1000 ms | 1000 ms | High — all 7 can align |
| Protective | 1000 ms | 1000 ms | High — all 7 can align |
| IEA stall | 2000 ms | 2000 ms | Medium |
| Engine heartbeat | 5000 ms | 5000 ms | Low |

---

## Explicit Conclusions

### Top 3 CPU Drivers (Estimate)

| Rank | Driver | Est. % | Evidence |
|------|--------|--------|----------|
| 1 | GetAccountSnapshot (21/s from coordinators) | 50–70% | 21 calls/s, NT Account iteration, possible lock contention |
| 2 | Logging (serialization + flush) | 15–25% | 41k queue, 500 ms flush, 2000 batch |
| 3 | Timetable poll (7× read + hash + 2× parse) | 5–15% | 7 engines × every 5 s |

### Top 3 Unnecessary Duplications

| Rank | Duplication | Fix |
|------|-------------|-----|
| 1 | MismatchEscalationCoordinator fetches snapshot twice per tick | Pass snapshot from OnAuditTick to AssembleMismatchObservations; or have Assemble return observations and coordinator use its own snapshot once |
| 2 | Protective + Mismatch both fetch same broker state every second | Shared cached snapshot (single refresh timer, both consume) |
| 3 | 7 engines each read same timetable file every 5 s | Shared timetable poll service (one reader, broadcast to engines) |

### Safest Reduction Opportunities

| Change | Risk | Impact |
|--------|------|--------|
| MismatchEscalationCoordinator: use _getSnapshot() result in AssembleMismatchObservations (pass snapshot in) | Low | −7 snapshot/s |
| Coordinator intervals 1 s → 5 s | Low | −16.8 snapshot/s |
| enable_diagnostic_logs: false in production | Low | −log volume |
| Timetable poll 5 s → 10 s | Low | −50% file reads |
| Shared account snapshot cache | Medium | Requires design; −14 to −21 snapshot/s |

---

## Instrumentation Requirements (To Validate)

1. **GetAccountSnapshot wrapper:** Log `caller, duration_ms, thread_id, positions_count, orders_count` before/after each call.
2. **5-minute run:** Aggregate `calls/sec`, `p50/p95/p99 duration_ms`, `peak burst (calls in 50 ms window)`.
3. **Log pipeline:** Per-event-type count, avg size, queue depth over time.
4. **Scaling test:** 1, 3, 7 engines; CPU %, snapshot/sec, log/sec.
