# Pre-Implementation Audit Questions — Answers

**Date:** 2026-03-19  
**Purpose:** Answer audit questions before implementing snapshot cache, AssembleMismatchObservations refactor, or timetable sharing. Prevents over-refactor and defines clean boundaries.

---

## 1. Snapshot Ownership Boundary

### Which GetAccountSnapshot() call sites are purely audit/reconciliation?

| Call Site | Classification | Rationale |
|-----------|----------------|-----------|
| **MismatchEscalationCoordinator** (`_getSnapshot`) | Audit | Periodic mismatch detection; no execution action from snapshot alone. Escalation is policy-driven. |
| **MismatchEscalationCoordinator** (via `AssembleMismatchObservations`) | Audit | Same; produces observations for coordinator. |
| **ProtectiveCoverageCoordinator** | Audit | Periodic protective coverage check; blocks or triggers corrective workflow. 5 s staleness acceptable. |
| **ReconciliationRunner** | Reconciliation | Orphaned journal cleanup + qty reconciliation. 60 s throttle. Staleness acceptable. |
| **InstrumentIntentCoordinator.RecalculateBrokerExposure** | Audit/awareness | Called on exit fill; logs BROKER_EXPOSURE_MISMATCH. "For awareness only, not action." |
| **RobotEngine second reconciliation** (5 min after recovery) | Reconciliation | Safety net; uses snapshot for ReconcileEntryOrders. Same account; can share. |

**Safe to use cached snapshot:** All of the above.

### Which are execution-verification or recovery-critical?

| Call Site | Classification | Rationale |
|-----------|----------------|-----------|
| **StreamStateMachine.HandleRangeLockedState** | Execution-verification | Before ExecutePendingRecoveryAction (resubmit/cancel). Must see current position to avoid double exposure. |
| **StreamStateMachine.SubmitStopEntryBracketsAtLock** | Execution-verification | Position check before bracket submit: "position not flat → block resubmit." Prevents double exposure. |
| **StreamStateMachine.HandleForcedFlatten** | Execution-verification | Post-flatten exposure verify: "position still open → CRITICAL, manual flatten required." Risk guarantee. |
| **RunRecoveryLegacy** | Recovery-critical | Disconnect recovery; unmatched position policy; adopt or flatten. Must have fresh broker truth. |

**Must remain direct (or use fresh snapshot):** All four. Cached snapshot with 3–5 s age is **not** acceptable for:
- Pre-resubmit position check (fill could have occurred in last second)
- Post-flatten verify (must confirm flatten succeeded)
- Recovery (policy decisions are irreversible)

### Which must remain direct for now?

| Call Site | Keep Direct? | Reason |
|-----------|--------------|--------|
| RunRecoveryLegacy | **Yes** | Recovery is rare, one-time per disconnect. Not a CPU driver. Keep simple. |
| StreamStateMachine (3 sites) | **Yes** | Execution-verification; low frequency (conditional). Not a CPU driver. |
| InstrumentIntentCoordinator.RecalculateBrokerExposure | **Yes** (or remove) | On exit fill only; low frequency. Could accept stale for "awareness" but safer to keep direct. |
| ReconciliationRunner | **Optional** | 1/60 s; low impact. Could use cache for consistency with coordinators. |
| Second reconciliation | **Optional** | 1× per recovery; negligible. |

**Summary:** Coordinators (Mismatch + Protective) and their AssembleMismatchObservations path are the **only** high-frequency call sites. They are all audit. **Only coordinators should use cached snapshot.** All execution-verification and recovery paths stay direct.

---

## 2. Mismatch Assembly Contract

### Can AssembleMismatchObservations be refactored to accept AccountSnapshot as an explicit parameter without changing behavior?

**Yes.** The method currently:
1. Calls `_executionAdapter.GetAccountSnapshot(utcNow)` (line 4723)
2. Uses `snap.Positions` and `snap.WorkingOrders` to build `brokerQtyByInst`, `brokerWorkingByInst`
3. Uses `_executionJournal`, `InstrumentExecutionAuthorityRegistry`, `_executionPolicy`, `_accountName` (engine state)
4. Produces `IReadOnlyList<MismatchObservation>`

**Refactor:** Add overload or change signature to `AssembleMismatchObservations(AccountSnapshot snap, DateTimeOffset utcNow)`. The caller (MismatchEscalationCoordinator) already has `snapshot` from `_getSnapshot()`. Pass it in; remove the internal `GetAccountSnapshot` call.

**Behavior change:** None. Same inputs (snap + journal + IEA + policy), same outputs.

### What additional derived state does it currently build internally from direct broker reads?

| Derived State | Source | Used For |
|---------------|--------|----------|
| `brokerQtyByInst` | `snap.Positions` | Per-instrument broker position qty |
| `brokerWorkingByInst` | `snap.WorkingOrders` | Per-instrument broker working order count |
| `allInstruments` | broker keys + journal keys | Union of instruments to evaluate |
| `openByInst` | `_executionJournal.GetOpenJournalEntriesByInstrument()` | Journal state (not broker) |
| `journalQty` | `_executionJournal.GetOpenJournalQuantitySumForInstrument()` | Journal state |
| `localWorking` | `iea.GetOwnedPlusAdoptedWorkingCount()` | IEA state |

**From broker (snapshot) only:** `brokerQtyByInst`, `brokerWorkingByInst`, `allInstruments` (broker subset). All derivable from `AccountSnapshot`. No other broker reads.

**Conclusion:** AssembleMismatchObservations needs only `AccountSnapshot` + engine state (journal, IEA, policy). No hidden broker reads. Refactor is a pure parameter pass-through.

---

## 3. Snapshot Cache Placement

### Where is the cleanest ownership point for a per-account snapshot cache?

| Option | Pros | Cons |
|--------|------|------|
| **Adapter level** | Adapter already owns NT Account access | Each engine has its own adapter instance; 7 adapters = 7 caches. No sharing. |
| **Engine level** | Simple; engine owns coordinator | Each engine has its own cache; 7 caches. No sharing. |
| **Shared service in execution layer** | Per-account; one cache for all engines | New abstraction; need registration/lookup |

**Recommendation: Shared service in execution layer, keyed by account name.**

**Rationale:**
- All 7 engines use the **same** SIM account. Snapshot is account-scoped, not engine-scoped.
- `InstrumentExecutionAuthorityRegistry` is already `(Account, ExecutionInstrumentKey)` → shared, per-account. Same pattern.
- One refresh timer, one `GetAccountSnapshot` call per interval, shared by all coordinators across all engines.
- Adapter stays thin (raw NT access). Cache is a separate concern.

**Proposed design:**
- `AccountSnapshotCache` (or `AccountSnapshotService`) in `QTSW2.Robot.Core.Execution`
- Key: `accountName`
- `GetOrCreate(accountName, Func<AccountSnapshot> refresh)` — singleton per account
- `GetSnapshot(accountName)` → returns cached snapshot + age; background timer refreshes
- Coordinators receive `Func<AccountSnapshot>` that delegates to cache instead of adapter
- Engine passes `() => AccountSnapshotCache.GetSnapshot(accountName)` or similar

**Ownership:** Execution layer. Not inside a single engine. Not inside adapter. Analogous to `InstrumentExecutionAuthorityRegistry` — a shared registry/service.

---

## 4. Staleness Policy

### What happens if cached snapshot age exceeds threshold?

**Recommended explicit behavior:**

| Condition | Action |
|-----------|--------|
| Age ≤ threshold (e.g. 5 s) | Use cached snapshot. Proceed with audit. |
| Age > threshold | **Skip audit tick.** Do not use stale snapshot. Log warning once per threshold window. |
| Age > threshold repeatedly | Log at INFO: `SNAPSHOT_CACHE_STALE_SKIP` with `age_seconds`, `threshold_seconds`, `caller`. Rate-limit to 1/min. |

**Do not:**
- Use stale snapshot with a flag (adds complexity; consumers would need to interpret)
- Trigger recovery signal (staleness is a cache/refresh issue, not a broker disconnect)

**Rationale:** Audit ticks are safety checks. Skipping one tick when cache is stale is safer than acting on stale data. Next refresh will restore. If refresh is broken, repeated skips will be visible in logs.

**Threshold:** 5 s recommended. Coordinators run every 1 s (or 5 s after interval change). Cache refresh every 2–5 s. Threshold slightly above refresh interval.

---

## 5. Timetable Sharing Boundary

### Can timetable polling be lifted out of engine instances without changing engine APIs too much?

**Yes.** Current flow:
1. Engine has `_timetablePoller`, `_timetablePath`
2. In Tick (before lock): `shouldPoll = _timetablePoller.ShouldPoll(nowWall)`
3. If shouldPoll: `parsed = PollAndParseTimetable(nowWall)` — reads file, hashes, parses
4. Inside lock: `ReloadTimetableIfChanged(utcNow, force: false, parsed.Poll, parsed.Timetable, parsed.ParseException)`

Engine API today: `ReloadTimetableIfChanged(utcNow, force, poll, timetable, parseException)`. It receives **pre-parsed** data. The engine does not need to own the poll.

### What is the minimal design for one-reader/many-consumer sharing?

**Option A: Static/shared TimetableCache (like RobotLoggingService)**

- `TimetableCache.GetOrCreate(projectRoot, pollInterval)` — singleton per project root
- Background: one timer/poll loop, reads file every N seconds, stores `(TimetableContract?, hash, lastPollUtc)`
- `GetCurrent()` → `(TimetableContract? timetable, string? hash, bool changed)` — changed vs last consumer's hash
- Engine calls `TimetableCache.GetCurrent()` instead of `PollAndParseTimetable()`
- Engine still owns `ReloadTimetableIfChanged` logic (streams, trading date, etc.)

**Minimal engine change:**
- Replace `_timetablePoller` with `_timetableCache` (or inject `ITimetableProvider`)
- In Tick: `var (timetable, hash, changed) = _timetableCache.GetCurrent(_lastTimetableHash);`
- Construct `FilePollResult`-like struct from `(changed, hash, null)` for `ReloadTimetableIfChanged`
- `ReloadTimetableIfChanged` signature unchanged: still receives `(poll, timetable, parseException)`

**Option B: Simpler — shared poller, engines subscribe**

- `TimetableFilePoller` becomes a shared instance (per project root)
- `Poll(path, utcNow)` is called by a single owner (e.g. first engine to tick, or a dedicated poll task)
- Problem: who owns the poll? If "first engine," then engine still does I/O. Need a shared poll runner.

**Recommendation: Option A.** `TimetableCache` with background poll. One read per interval for entire process. Engine receives `(timetable, hash, changed)` — no file I/O in engine path.

**API impact:**
- Engine constructor: replace `TimetableFilePoller` with `TimetableCache` or `ITimetableProvider`
- `PollAndParseTimetable` becomes `GetTimetableFromCache()` — no poll, just cache read
- `ReloadTimetableIfChanged` unchanged

---

## Summary: Implementation Order

| Change | Risk | Dependency |
|--------|------|-------------|
| **A1:** AssembleMismatchObservations(snap, utcNow) — pass snapshot from coordinator | Low | None |
| **A2:** Coordinator intervals 1 s → 5 s | Low | None |
| **B1:** AccountSnapshotCache service + coordinator wiring | Medium | A1 (optional; A1 alone removes 7/s) |
| **B2:** Staleness policy (skip tick + log) | Low | B1 |
| **C1:** TimetableCache shared service | Medium | None |

**Safest path:** A1 + A2 first. Measure. Then B1+B2 if needed. C1 is independent.
