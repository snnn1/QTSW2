# Phase 3 enforcement audit — `RobotCore_For_NinjaTrader` (read-only)

**Scope:** Normative rules in [03_transition_contract.md](03_transition_contract.md), [02_decision_authority.md](02_decision_authority.md), [02_phase2ab_addendum.md](02_phase2ab_addendum.md).  
**Method:** Call-chain trace in source; comments used only when they match code.  
**Note:** There is **no** single `ExecutionPermissionAuthority` *decision* type in code; EPA is split across `RiskGate`, `ExecutionPermissionAuthority` (preflight), `RobotEngine`, `MismatchEscalationCoordinator`, `NinjaTraderSimAdapter`, etc.

---

## Section 1 — Executive verdict

| Question | Answer |
|----------|--------|
| Does each audited transition currently have one coherent cause → decision → effect chain? | **MIXED** |
| Weakest audited transition (choose one) | **mismatch detected → mismatch escalated → trading blocked** |
| Strongest audited transition (choose one) | **stream terminal commit** |

**Proof (one paragraph):** Normative Phase 3 assigns **decision owner = EPA** for escalation and trading block ([03_transition_contract.md](03_transition_contract.md) §4 rows “Mismatch escalated”, “Trading blocked”). Deployed code has **no** unified EPA class: `MismatchEscalationCoordinator` mutates `Blocked` / `EscalationState` in `_stateByInstrument` (`ProcessMismatchPresent`, persistence thresholds in same file), while **enforcement** of “trading blocked” for stream order flow is delegated to `RobotEngine.IsInstrumentFrozenOrSupervisorilyBlocked` → `RiskGate.CheckGates` and `ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight` (`RobotEngine.cs` ~5701–5714, `NinjaTraderSimAdapter.cs` ~1913–1935). That splits **decision state** (coordinator) from **gate evaluation** (engine + gates) without a single named decision record—**overlapping authorities** relative to the locked contract. By contrast, **stream terminal commit** is a single causal chain: `StreamStateMachine.Commit` sets `StreamJournal.Committed` and calls `JournalStore.Save` before emitting `JOURNAL_WRITTEN` (`StreamStateMachine.cs` ~6536–6617, `JournalStore.cs` ~191–231).

---

## Section 2 — Transition-by-transition table

| Transition | Trigger symbol / file | Cause owner | Decision owner | Effect owner | Persistence writes | Narrative/log side effects | Official commit point | Parallel authority present? | Compliant with Phase 3? | Verdict |
|------------|----------------------|-------------|----------------|--------------|-------------------|---------------------------|------------------------|------------------------------|-------------------------|---------|
| mismatch detected → mismatch escalated → trading blocked | Timer → `MismatchEscalationCoordinator.OnAuditTick` → `ProcessMismatchPresent` / gate advancement (`MismatchEscalationCoordinator.cs` ~215–280, ~467–575) | **Cause:** snapshot + `RobotEngine.AssembleMismatchObservations` via `_getMismatchObservations` delegate (engine wired at `RobotEngine.cs` ~1455–1458) | **Decision:** `MismatchEscalationCoordinator` sets `state.Blocked`, `EscalationState` (same file) — **not** a separate EPA module | **Effect:** `RiskGate` / adapter preflight consult `IsInstrumentFrozenOrSupervisorilyBlocked` which ORs coordinator block (`RobotEngine.cs` ~5701–5706) | Coordinator: **in-memory** `_stateByInstrument` only (no coordinator persistence in traced path) | `EXECUTION_ERROR` on audit exception; escalation metrics/events in coordinator | **Trading blocked:** first `RiskGate`/adapter denial using frozen callback **or** coordinator `Blocked==true` read in same tick path | **Yes** — coordinator state vs `ReconciliationRunner` detect-only vs engine freeze set | **PARTIAL** — decision owner ≠ documented EPA as a single choke point | **PARTIAL** |
| recovery entered | `RobotEngine.OnConnectionStatusUpdate` disconnect branch (`RobotEngine.cs` ~5534–5567) | Connection status callback | `RobotEngine` sets `_recoveryState = DISCONNECT_FAIL_CLOSED` under `_engineLock` | `IExecutionRecoveryGuard` via `IsExecutionAllowed()` false; `RiskGate` gate 0 | None in traced path | `DISCONNECT_FAIL_CLOSED_ENTERED`, health `ReportCritical` | **`_recoveryState` assignment** in memory | **Yes** — `IsExecutionAllowed` also reads `_killSwitch` (`RobotEngine.cs` ~543–551), overlapping “recovery” vs “kill” in one method | **PARTIAL** | **PARTIAL** |
| recovery exited | `RunRecovery` IE path or legacy snapshot path (`RobotEngine.cs` ~7333–7407, ~7625+); also tick-driven transitions | Broker/IEA bootstrap or snapshot reconciliation | `RobotEngine` sets `_recoveryState = RECOVERY_COMPLETE` | Same; unlocks `IsExecutionAllowed()` when true | None required for state itself | `DISCONNECT_RECOVERY_COMPLETE` etc. | **`_recoveryState = RECOVERY_COMPLETE`** | **Yes** — multiple code paths set `RECOVERY_COMPLETE` | **PARTIAL** | **PARTIAL** |
| stream terminal commit | `StreamStateMachine.Commit` (`StreamStateMachine.cs` ~6536) | SSM logic calling `Commit` | **Effectively SSM** (sets terminal fields) | `JournalStore.Save` + `StreamStateMachine` state `DONE` | `JournalStore.Save` → temp+move (`JournalStore.cs` ~191–231) | `JOURNAL_WRITTEN`, terminal event | **After `_journals.Save(_journal)`** (ordering: mutate journal DTO → Save → logs ~6605–6631) | **Yes** — `JournalStore.Save` can fail silently (swallow IOException in catch ~225–228) while SSM still sets `State = DONE` (~6619) | **PARTIAL** (commit durability vs §2 rules) | **PARTIAL** |
| flatten triggered | `NinjaTraderSimAdapter` enqueue: `TryExecutionSafetyGateFlattenEnqueue` → `TryExecutionSafetyFlattenGuard` (`NinjaTraderSimAdapter.cs` ~2198–2200, ~2133+) | Policy / IEA / `EnqueueNtActionInternal` | **Structural + overlay** in `TryExecutionSafetyFlattenGuard`; not `RiskGate.CheckGates` | NT command queue → executor | Journal/incident paths downstream of flatten (not single symbol here) | `FLATTEN_ENQUEUE`, `FLATTEN_BLOCKED_*` | **Enqueue accepted** past guard (command reaches queue) | **Yes** — `FlattenIntent` vs `EnqueueEmergencyFlattenProtective` vs IEA recovery flatten (`RobotEngine.cs` ~1440–1445) | **PARTIAL** | **PARTIAL** |
| flatten completed | `NinjaTraderSimAdapter.NT.cs` flatten execution → `FlattenResult.SuccessResult` (~562, ~664, ~7237); log notes broker confirm separate (`~1373–1380`) | Broker/API completion | Adapter interprets submit result | Broker position change; journal via execution ingress | Execution journal updates on fills (separate callbacks) | `FLATTEN_SUBMITTED`, `FLATTEN_INTENT_SUCCESS`, `FLATTEN_BROKER_FLAT_CONFIRMED` (per log text) | **Ambiguous** — success result can be submit-phase; broker flat may be async | **Yes** — IEA vs direct NT paths | **PARTIAL** | **PARTIAL** |
| journal repair | `NinjaTraderSimAdapter.TryRepairTaggedBrokerWithoutJournal` → `TryRepairTaggedBrokerWithoutJournalCore` (`NinjaTraderSimAdapter.cs` ~3583–3591; `.NT.cs` ~8846+) | Reconciliation / execution ingress calling repair | **Adapter core** eligibility + cooldown | **ExecutionJournal** upsert | `ExecutionJournal` durable rows (methods inside `.NT.cs` core) | Repair result codes, cooldown telemetry | **Journal write success** (internal to `ExecutionJournal`) | **Yes** — `ReconciliationRunner` “does not decide execution readiness” per its class doc (`ReconciliationRunner.cs` ~9–12) vs adapter mutation | **PARTIAL** | **PARTIAL** |
| orphan-fill containment | `EmitUnmappedFill` (`NinjaTraderSimAdapter.NT.cs` ~5032–5058) | Execution ingress classifies unmapped fill | **Adapter** applies `ExecutionSafetyGate.ApplyUnmappedExecutionKillSwitch` + `_blockInstrumentCallback` | Engine freeze via callback; unsafe lock overlay | Incident/logging; not tracing full incident file here | `CRITICAL_UNMAPPED_FILL_DETECTED` | **Unsafe lock + block callback invoked** (same method) | **Yes** — global kill file **not** this path; separate from Phase 2A “global kill” definition | **PARTIAL** vs normative “EPA” naming | **PARTIAL** |
| explicit unfreeze | `RobotEngine.TryUnfreezeInstrument` (`RobotEngine.cs` ~5825–5864) | Operator / caller | **Adapter** `TryValidateExplicitUnfreezeConditions` then engine | `_frozenInstruments.Remove`, `_riskLatchManager.Clear` | Risk latch file delete (`RiskLatchManager.Clear` from engine) | `INSTRUMENT_UNFROZEN_EXPLICIT` / denied | **`TryUnfreezeInstrument` returns true after latch clear** | **Yes** — adapter structural/overlay gate vs engine freeze | **PARTIAL** | **PARTIAL** |

---

## Section 3 — Detailed causal traces (abbreviated; symbols are proof anchors)

### A. mismatch → escalate → trading blocked

- **Trigger:** `MismatchEscalationCoordinator` timer → `OnAuditTick` (`MismatchEscalationCoordinator.cs` ~215).
- **Chain:** `OnAuditTick` → `_getMismatchObservations(snapshot, utcNow)` (engine delegate) → `ProcessMismatchPresent` / escalation thresholds → `state.Blocked = true` (~499–500, ~536–537, etc.) → later `RiskGate.CheckGates` / `TryAdapterOrderSubmitPreflight` read block via `RobotEngine.IsInstrumentFrozenOrSupervisorilyBlocked` (`RobotEngine.cs` ~5706).
- **Cause owner proof:** `AssembleMismatchObservations` in `RobotEngine.cs` ~5884+ (wired as `getMismatchObservations`).
- **Decision owner proof:** `MismatchEscalationCoordinator` sets `Blocked` without calling a distinct `EPA` type.
- **Effect owner proof:** `RiskGate` + adapter (`RiskGate.cs` ~64–69; `NinjaTraderSimAdapter` EPA preflight via engine callbacks).
- **Persistence proof:** Coordinator state **in-memory** in `_stateByInstrument` (field in `MismatchEscalationCoordinator`).
- **Official commit proof:** **None** for “mismatch block” as durable contract—block is live process state + gate reads.
- **Logs:** Participate in **telemetry only** for this path unless a deny is returned from a gate (then execution fails).
- **Second subsystem:** `ReconciliationRunner` mismatch callback `LogReconciliationQuantityMismatchDiagnostics` **explicitly does not decide** (`RobotEngine.cs` ~5670–5673).

### B. recovery entered

- **Trigger:** `OnConnectionStatusUpdate` (`RobotEngine.cs` ~5520+).
- **Chain:** disconnect → `_recoveryState = DISCONNECT_FAIL_CLOSED` (~5537–5539) → `IsExecutionAllowed()` false via recovery state (~547–550).
- **Parallel:** `IsExecutionAllowed` also checks `_killSwitch.IsEnabled()` (~547–548)—**overlaps** recovery semantics with kill semantics in one function.

### C. recovery exited

- **Trigger:** `RunRecovery` / reconnection paths (`RobotEngine.cs` ~7333+).
- **Chain:** sets `_recoveryState = RECOVERY_COMPLETE` (~7395–7396) (IEA bootstrap path shown).
- **Parallel:** Multiple branches can set `RECOVERY_COMPLETE` → **duplicate transition authority** risk across paths (same symbol pattern at ~7395, ~7639 in grep results).

### D. stream terminal commit

- **Trigger:** `Commit(utcNow, commitReason, eventType)` (`StreamStateMachine.cs` ~6536).
- **Chain:** set `_journal.Committed = true`, fields → `_journals.Save(_journal)` (~6605) → logs → `State = StreamState.DONE` (~6619).
- **Persistence proof:** `JournalStore.Save` atomic temp+move (`JournalStore.cs` ~202–208).
- **Failure mode:** Save failure swallowed (~225–228) → **commit belief can diverge from disk** (see §6).

### E. flatten triggered / completed

- **Triggered:** `TryExecutionSafetyGateFlattenEnqueue` → `TryExecutionSafetyFlattenGuard` (`NinjaTraderSimAdapter.cs` ~2198–2200).
- **Completed:** Success results from NT flatten execution (`NinjaTraderSimAdapter.NT.cs` ~1373–1380 notes broker confirmation is separate event).

### F. journal repair

- **Trigger:** callers of `TryRepairTaggedBrokerWithoutJournal` (adapter public API).
- **Chain:** `TryRepairTaggedBrokerWithoutJournalCore` (`.NT.cs` ~8846+) mutates journal when eligibility passes; cooldown in `NinjaTraderSimAdapter.cs` ~83–118.

### G. orphan-fill containment

- **Trigger:** `EmitUnmappedFill` (`.NT.cs` ~5032+).
- **Chain:** `ApplyUnmappedExecutionKillSwitch` → `_blockInstrumentCallback` → engine-side freeze; **not** `KillSwitch` JSON file.

### H. explicit unfreeze

- **Trigger:** `RobotEngine.TryUnfreezeInstrument` (~5825).
- **Chain:** `NinjaTraderSimAdapter.TryValidateExplicitUnfreezeConditions` (~2093+) → `_frozenInstruments.Remove` + `_riskLatchManager?.Clear` (~5856–5858).

---

## Section 4 — Phase 3 compliance test (binary + rating)

Columns: transition | cause explicit? | decision explicit? | effect explicit? | commit explicit? | second subsystem same effect? | logs affect outcome? | persistence lag belief risk? | rating |

| Transition | cause | decision | effect | commit | dup effect | logs→control | lag belief | rating |
|------------|-------|----------|--------|--------|------------|--------------|------------|--------|
| mismatch→escalate→block | YES | PARTIAL | YES | NO | YES | NO | YES | **partially compliant** |
| recovery entered | YES | YES | YES | YES | PARTIAL | NO | NO | **partially compliant** |
| recovery exited | YES | PARTIAL | YES | PARTIAL | YES | NO | NO | **partially compliant** |
| stream terminal commit | YES | YES | YES | PARTIAL | NO | NO | YES | **partially compliant** |
| flatten triggered | YES | PARTIAL | YES | PARTIAL | YES | NO | PARTIAL | **partially compliant** |
| flatten completed | YES | PARTIAL | PARTIAL | NO | YES | NO | YES | **partially compliant** |
| journal repair | YES | PARTIAL | YES | PARTIAL | PARTIAL | NO | YES | **partially compliant** |
| orphan-fill containment | YES | YES | YES | PARTIAL | PARTIAL | NO | PARTIAL | **partially compliant** |
| explicit unfreeze | YES | YES | YES | YES | NO | NO | NO | **partially compliant** |

**Legend:** “PARTIAL” answers reflect split ownership (no single EPA module) or ambiguous commit (flatten complete, mismatch block durability).

---

## Section 5 — Highest-risk authority overlaps

| Competing components | Symbols | What each does | Safe / unsafe / ambiguous | S/T/F/O |
|---------------------|---------|------------------|---------------------------|---------|
| `ReconciliationRunner` vs `MismatchEscalationCoordinator` | `RunInternal`, `LogReconciliationQuantityMismatchDiagnostics` vs `OnAuditTick`, `ProcessMismatchPresent` | Runner **detects** qty mismatch (logs + tracker); coordinator **decides** escalation/block | **Ambiguous** vs normative “EPA decides” wording; **safe** if treated as deliberate split detect/decide | **T** |
| `RiskGate` / EPA preflight vs engine freeze / coordinator | `RiskGate.CheckGates`, `ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight`, `IsInstrumentFrozenOrSupervisorilyBlocked` | Multiple booleans OR’d for “blocked” | **Ambiguous** — no single decision record; **unsafe** under forensic reconstruction if not logged together | **T** |
| `StreamStateMachine` vs `JournalStore` | `Commit`, `JournalStore.Save` | In-memory journal DTO vs disk | **Unsafe** if `Save` fails silently (`JournalStore.cs` ~225–228) while `State=DONE` set | **T** |
| Flatten intent vs emergency flatten | `FlattenIntent`, `EnqueueEmergencyFlattenProtective`, `TryExecutionSafetyFlattenGuard` | Different entrypoints; shared guard for enqueue | **Ambiguous** for auditing “who triggered flatten” | **F** |
| Journal repair vs normal execution | `TryRepairTaggedBrokerWithoutJournalCore` vs fill path | Same `ExecutionJournal` writer | **Ambiguous** provenance without row-level audit | **T** |
| Unfreeze vs risk latch | `TryUnfreezeInstrument`, `RiskLatchManager.Clear`, `TryValidateExplicitUnfreezeConditions` | Adapter validates; engine clears memory + latch file | **Safe** if both succeed; **ambiguous** if only one layer clears | **S** |

---

## Section 6 — Official commit audit

| Transition | Line of truth (“it happened”) | in-memory only? | persisted only? | both? | matches 03 §2? |
|------------|-------------------------------|-----------------|-----------------|-------|----------------|
| Mismatch trading block | `MismatchInstrumentState.Blocked` in coordinator | YES | NO | NO | **NO** — no durable commit for block state |
| Recovery state | `_recoveryState` field | YES | NO | NO | **PARTIAL** — contract mentions state var; no persistence |
| Stream commit | `StreamJournal.Committed` + successful `JournalStore.Save` | YES (DTO) | YES (file) | **both required by normative** | **PARTIAL** — code does not verify Save success |
| Flatten triggered | Guard pass + command enqueued | YES | PARTIAL | PARTIAL | **PARTIAL** |
| Flatten completed | `FlattenResult.Success` + downstream broker/journal evidence | PARTIAL | PARTIAL | PARTIAL | **PARTIAL** |
| Journal repair | Durable `ExecutionJournal` write | PARTIAL | YES | both | **PARTIAL** — depends on journal fsync semantics (not audited here) |
| Orphan-fill | Unsafe lock dict + engine callback side effects | YES | PARTIAL | PARTIAL | **PARTIAL** |
| Explicit unfreeze | `TryUnfreezeInstrument` true + latch file deleted | YES | YES | **both** | **YES** (closest match) |

---

## Section 7 — Gap register (Phase 3)

| Gap ID | Transition | Rule violated (03) | Observed behavior | Severity | Why it matters | Narrowest fixing surface |
|--------|------------|--------------------|-------------------|----------|----------------|---------------------------|
| P3-G1 | mismatch→block | Decision owner = EPA (§4 table) | `MismatchEscalationCoordinator` owns `Blocked` flag | **T** | Forensic “who decided?” points to coordinator, not a named EPA façade | Single decision façade or explicit delegate row in §1 register |
| P3-G2 | stream commit | §2 “both hold” + durable Save | `JournalStore.Save` may fail silently; SSM still sets DONE | **T** | Reconstruction can disagree with disk | Propagate Save failure or block `State=DONE` on failure |
| P3-G3 | recovery exit | Single coherent transition | Multiple sites set `RECOVERY_COMPLETE` | **O** | Operator confusion on which path cleared recovery | Consolidate state transition helper |
| P3-G4 | flatten completed | Official when | Submit success vs broker flat split across logs | **F** | Audit ambiguity | One “broker_flat_confirmed” commit record |
| P3-G5 | `IsExecutionAllowed` | Phase 2A RR vs kill | Kill switch mixed into recovery guard callback | **S** | RR policy vs recovery semantics conflated | Split callbacks (recovery vs kill) per 02_phase2ab |

---

## Section 8 — Final verdict

| Transition | Cause owner | Decision owner | Effect owner | Official commit point | Parallel authority? | Phase 3 status | Severity if broken |
|------------|-------------|----------------|--------------|----------------------|---------------------|----------------|-------------------|
| mismatch→escalate→block | Engine observations + coordinator process | **Coordinator** (not EPA module) | RiskGate + adapter gates | In-memory block | YES | **partially enforced** | **T** |
| recovery entered | Connection callback | **RobotEngine** | `IsExecutionAllowed` / RiskGate | `_recoveryState` | YES | **partially enforced** | **S** |
| recovery exited | Recovery runner / IEA | **RobotEngine** | same | `_recoveryState` | YES | **partially enforced** | **S** |
| stream terminal commit | SSM | **SSM** | JournalStore | Post-`Save` (intended) | YES (Save fail) | **partially enforced** | **T** |
| flatten triggered | Adapter/IEA/engine | Structural+overlay guard | NT queue | Post-guard enqueue | YES | **partially enforced** | **S** |
| flatten completed | Broker/API | Adapter | Broker+journal | **UNKNOWN** (split) | YES | **partially enforced** | **T** |
| journal repair | Ingress/recon | Adapter repair core | ExecutionJournal | Journal write | PARTIAL | **partially enforced** | **T** |
| orphan-fill containment | Ingress | Adapter + callback | Engine freeze + overlay | Lock+callback | YES | **partially enforced** | **S** |
| explicit unfreeze | Operator API | Engine+adapter | Engine+latch | Return true + Clear | NO | **closest to enforced** | **S** |

**Phase 3 status (single sentence):** Phase 3 is **partially enforced**: stream commit and explicit unfreeze approximate the locked model, while **mismatch escalation and trading block lack a single durable, named decision commit** aligned with §4’s EPA decision owner.

**Highest-confidence reason from code:** **`MismatchEscalationCoordinator` holds authoritative `Blocked` state while `RiskGate` / adapter preflight enforce via a **separate** OR’d callback (`IsInstrumentFrozenOrSupervisorilyBlocked`) with **no persisted decision record**—splitting “decision” and “enforcement” without an explicit EPA module or delegate row.
