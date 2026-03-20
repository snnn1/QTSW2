# Full Execution Reliability Audit — Production Trading Level

**Date**: 2026-03-18  
**Scope**: Live execution layer reliability for supervised real trading  
**Constraint**: Audit only — no implementation, refactoring, or code changes

---

## 1. EXECUTION ARCHITECTURE MAP

### End-to-End Flow

```
NinjaTrader Strategy (RobotSimStrategy)
    │
    ├─ OnBar / Tick → RobotEngine (engineLock)
    │       │
    │       ├─ TimetableFilePoller → ApplyTimetable
    │       ├─ Bar routing → StreamStateMachine[] (per stream)
    │       └─ ConnectionStatusUpdate → ConnectionRecoveryState
    │
    ├─ OnOrderUpdate → ExecutionUpdateRouter → NinjaTraderSimAdapter.HandleOrderUpdate
    ├─ OnExecutionUpdate → ExecutionUpdateRouter → NinjaTraderSimAdapter.HandleExecutionUpdate
    │
    └─ SetNTContext → WireNTContextToAdapter → IEA bootstrap
```

### Component Roles and State Authority

| Component | Role | Where State Is Authoritative |
|-----------|------|------------------------------|
| **RobotEngine** | Orchestrator: timetable, streams, connection recovery, reconciliation tick, mismatch coordinator | Trading date, connection state, frozen instruments, recovery state |
| **StreamStateMachine** | Per-stream lifecycle: PRE_HYDRATION → ARMED → RANGE_BUILDING → RANGE_LOCKED → DONE | Range, intent creation, slot journal, entry eligibility |
| **NinjaTraderSimAdapter** | NT API boundary: order submit, fill handling, flatten, BE | Local OrderMap when IEA disabled; delegates to IEA when enabled |
| **InstrumentExecutionAuthority (IEA)** | Per-(account, executionInstrument) queue: serializes fills, BE, protectives, flatten | OrderMap, IntentMap, OrderRegistry, adoption state |
| **ExecutionJournal** | Idempotency and audit trail per (trading_date, stream, intent_id) | Disk: `data/execution_journals/*.json` — canonical for submission/fill state |
| **ReconciliationRunner** | Orphan journal closure when broker flat; qty mismatch detection | Reads broker snapshot + journal; emits RECONCILIATION_QTY_MISMATCH |
| **SharedAdoptedOrderRegistry** | Cross-instance fill resolution by broker_order_id | In-memory static; 60-min retention |
| **ExecutionUpdateRouter** | Routes execution/order callbacks by (account, executionInstrumentKey) | One endpoint per key; first instance wins |
| **MismatchEscalationCoordinator** | broker_working vs iea_working; ORDER_REGISTRY_MISSING → fail-closed | DETECTED → PERSISTENT (10s) → FAIL_CLOSED (30s) |

### State Authority by Stage

| Stage | Authoritative Source |
|-------|----------------------|
| Intent created | StreamStateMachine (in-memory) + ExecutionJournal (disk for idempotency) |
| Entry submitted | ExecutionJournal (RecordSubmission) + IEA OrderMap (when IEA) |
| Accepted/Working | Broker (NinjaTrader) + IEA OrderRegistry |
| Filled | ExecutionJournal (RecordEntryFill/RecordExitFill) + IEA IntentMap |
| Protectives submitted | IEA OrderRegistry + ExecutionJournal |
| Target/stop exit | ExecutionJournal (RecordExitFill, TradeCompleted) |
| Flatten/forced flatten | IEA (ExecuteRecoveryFlatten) or adapter |
| Trade complete | ExecutionJournal (RecordReconciliationComplete or RecordExitFill) |

---

## 2. ORDER LIFECYCLE RELIABILITY

| Stage | Failure Mode | Detection | Current Mitigation | Residual Risk |
|-------|--------------|-----------|--------------------|---------------|
| **Intent created** | Duplicate intent, invalid range | Idempotency check (journal), RiskGate | Journal `IsIntentSubmitted`; intent hash | Low — hash collision theoretical |
| **Entry submitted** | Reject, timeout, disconnect | OrderUpdate Rejected, RiskGate blocks during recovery | RecordRejection; fail-closed on disconnect | Medium — OCO sibling CancelPending misreported as Rejected (fixed) |
| **Accepted/Working** | Order stuck Initialized | OrderState check | NT Submit on strategy thread; no explicit Initialized handling | Medium — orders can remain local-only if blocked |
| **Filled** | Fill to wrong instance, orphan fill | ExecutionUpdateRouter, SharedAdoptedOrderRegistry | Router by (account, instrument); SharedAdoptedOrderRegistry fallback | Low — cross-instance path fixed 2026-03-17 |
| **Protectives submitted** | Missing stop/target, qty mismatch | IEA validation, protective audit | FailClosed on mismatch; recovery state blocks during disconnect | Medium — recovery blocks → flatten (no queue) |
| **Target/stop exit** | Partial fill, OCO sibling cancel | RecordExitFill, OCO_SIBLING_CANCELLED handling | Idempotent exit recording; skip RecordRejection for CancelPending | Low |
| **Flatten/forced flatten** | Flatten fails, retry exhausted | FlattenWithRetry, verify | Retry with backoff; EXECUTION_FLATTEN_VERIFY_FAILED after max | Medium — manual intervention if verify fails |
| **Trade complete** | Orphan journal, broker flat first | ReconciliationRunner | RecordReconciliationComplete when broker flat + no working | Low |

### Journaling Correctness

- **Entry submission**: Journal `RecordSubmission` before/with broker submit; corruption → stand-down (fail-closed).
- **Entry fill**: `RecordEntryFill` from fill callback; SharedAdoptedOrderRegistry enables cross-instance journaling.
- **Exit fill**: `RecordExitFill`; `TradeCompleted` set on full exit.
- **Reconciliation**: `RecordReconciliationComplete` when broker flat and no working orders.

---

## 3. STARTUP / RESTART RELIABILITY

### Bootstrap Flow

1. **SetNTContext** → `BeginBootstrapForInstrument` (BootstrapReason.STRATEGY_START)
2. **OnBootstrapSnapshotRequested** → reads `account.Orders` synchronously (single snapshot)
3. **BootstrapClassifier.Classify** → RESUME_WITH_NO_POSITION_NO_ORDERS, CLEAN_START, LIVE_ORDERS_PRESENT_NO_POSITION, etc.
4. **BootstrapDecider.Decide** → RESUME, ADOPT, FLATTEN
5. **ProcessBootstrapResult** → transitions IEA recovery state

### Known Fixes (2026-03-17 Incident)

| Issue | Fix |
|-------|-----|
| Bootstrap snapshot before broker repopulated | TryRecoveryAdoption in AssembleMismatchObservations before ORDER_REGISTRY_MISSING |
| Unfilled entry stops not adoptable | GetAdoptionCandidateIntentIds (EntrySubmitted && !TradeCompleted) |
| No adoption on reconciliation | TryRecoveryAdoption when brokerWorking>0, iea_working==0 |

### Remaining Limitations

| Limitation | Description |
|------------|-------------|
| **Bootstrap still snapshot-based** | Single snapshot at bootstrap; no wait for broker convergence |
| **IEA in-memory** | Restart clears registry; adoption restores from journal when broker orders appear |
| **Journal hydration** | Journal must be correct; adoption depends on EntrySubmitted/TradeCompleted |

### Is Restart Now Operationally Reliable?

**CONDITIONAL YES**. Valid QTSW2 orders (EntrySubmitted && !TradeCompleted) are adoptable when broker orders appear—even delayed. Unknown/unverifiable orders still fail-closed.

### Risky Cases

| Case | Risk | Mitigation |
|------|------|------------|
| Broker cancels orders on disconnect | Orders lost; cannot recover | Platform behavior; accept |
| Bootstrap sees broker_working=0, orders appear >1s later | Adoption on next reconciliation tick (1s) | TryRecoveryAdoption before fail-closed |
| Journal corrupted or wrong instrument | Adoption fails | Fail-closed; stand-down |

### Design Flaws vs Acceptable Fail-Closed

- **Snapshot-based bootstrap**: Design limitation; reconciliation recovery path mitigates.
- **Flatten on unverifiable orders**: Acceptable fail-closed.
- **Journal corruption stand-down**: Correct fail-closed.

---

## 4. DISCONNECT / RECONNECT RELIABILITY

### DISCONNECT_FAIL_CLOSED Behavior

| State | Blocked | Permitted |
|-------|---------|-----------|
| DISCONNECT_FAIL_CLOSED | Entry, protective, BE modifications | Emergency flatten (bypasses RiskGate) |
| RECONNECTED_RECOVERY_PENDING | Same | Same |
| RECOVERY_RUNNING | Same | Recovery logic (snapshot, reconcile, adopt) |
| RECOVERY_COMPLETE | None | Full execution |

### What Survives Reconnect

- **Broker**: Positions and working orders (unless broker cancels on disconnect)
- **Journal**: Disk-persisted; survives restart
- **IEA registry**: Cleared on restart; restored via adoption

### Recovery Determinism

- Recovery runs on reconnect; requires two reconciliation passes + broker sync before RECOVERY_COMPLETE.
- `_lastOrderUpdateUtc` / `_lastExecutionUpdateUtc` reset on reconnect to avoid stale counts.

### Disconnect/Reconnect Truth Table

| Case | Expected System Response | Current Reliability | Operator Action |
|------|---------------------------|---------------------|-----------------|
| Disconnect, no position | Block all execution | Reliable | None |
| Disconnect, position, reconnect | Recovery, adopt, resume | Reliable post-fix | Monitor for RECOVERY_POSITION_UNMATCHED |
| Disconnect, working orders, reconnect | Adoption via TryRecoveryAdoption | Reliable post-fix | None if adoption succeeds |
| Broker cancels on disconnect | Orders gone; journal may be ahead | Platform-limited | Flatten if journal_ahead |
| Reconnect before broker repopulated | Bootstrap RESUME; adoption when orders appear | Reliable | None |

### Manual Intervention

- **RECOVERY_POSITION_UNMATCHED**: Unmatched positions → recovery aborted; operator must flatten or reconcile.
- **Force reconcile**: `force_reconcile.ps1` when broker correct, journal wrong.

---

## 5. OWNERSHIP / RECONCILIATION RELIABILITY

### Ownership Determination

| Source | Scope | Use |
|--------|-------|-----|
| **Broker** | account.Orders, account.Positions | Snapshot for reconciliation |
| **Journal** | GetOpenJournalEntriesByInstrument, GetAdoptionCandidateIntentIds | Position qty, adoption candidates |
| **IEA** | OrderMap, OrderRegistry, GetOwnedPlusAdoptedWorkingCount | Working order count, fill resolution |

### Failure Modes

| Event | Meaning | Resolution |
|-------|---------|------------|
| **ORDER_REGISTRY_MISSING** | broker_working>0, iea_working=0 | TryRecoveryAdoption first; if fails → fail-closed |
| **RECONCILIATION_QTY_MISMATCH** | broker_qty ≠ journal_qty | Instrument freeze; RequestRecovery; force_reconcile or flatten |
| **POSITION_DRIFT_DETECTED** | Alias for RECONCILIATION_QTY_MISMATCH | Same |
| **EXPOSURE_INTEGRITY_VIOLATION** | Same taxonomy | Same |
| **REGISTRY_BROKER_DIVERGENCE** | Broker order not in registry | TryAdoptBrokerOrderIfNotInRegistry; else RequestRecovery → FLATTEN |

### broker_ahead / journal_ahead

- **broker_ahead**: Broker has position, journal does not → possible external fill or journal lag.
- **journal_ahead**: Journal has fill, broker flat → possible external close or broker lag.

### Ownership Failure Matrix

| Failure | Resolved | Fail-Safe | Still Dangerous |
|---------|----------|-----------|-----------------|
| ORDER_REGISTRY_MISSING (unfilled entry stops) | ✅ | — | — |
| RECONCILIATION_QTY_MISMATCH (stale cache) | ✅ | — | — |
| RECONCILIATION_QTY_MISMATCH (file lock) | ✅ | — | — |
| Adopted fill to wrong instance | ✅ | — | — |
| Unknown order → flatten | — | ✅ | — |
| Journal corruption | — | ✅ | — |
| Wrong project root | — | — | Operator must set QTSW2_PROJECT_ROOT |

---

## 6. MULTI-INSTANCE RELIABILITY

### Design

- **IEA**: Shared per (account, executionInstrumentKey) via InstrumentExecutionAuthorityRegistry.
- **ExecutionUpdateRouter**: One endpoint per (account, executionInstrumentKey); first instance wins.
- **SharedAdoptedOrderRegistry**: Cross-instance fill resolution when OrderMap/Registry miss.

### Callback Routing

- `OnExecutionUpdate` / `OnOrderUpdate` → ExecutionUpdateRouter.TryGetEndpoint → single adapter.
- Routing by order.Instrument (execution key), not chart.

### Local OrderMap vs Shared Registry

- When IEA enabled: IEA owns OrderMap, IntentMap; adapter delegates.
- When IEA disabled: Adapter's local OrderMap; no cross-instance sharing (unsafe for multi-chart same instrument).

### Cross-Instance Adopted-Order Fill Path

1. Order adopted in instance A (or adoption in IEA shared by all).
2. Fill callback arrives at instance B (or any).
3. Router sends to correct endpoint (by account, instrument).
4. If orderInfo null: SharedAdoptedOrderRegistry.TryResolve → journal hydration → RecordEntryFill.

### Remaining Edge Cases

| Edge Case | Status |
|-----------|--------|
| Fill before registry has order | SharedAdoptedOrderRegistry + deferred retry |
| Two instances same instrument, IEA disabled | EXEC_ROUTER_ENDPOINT_CONFLICT; second fails |
| SharedAdoptedOrderRegistry 60-min expiry | Fail-closed; acceptable for typical session |

### Is Multi-Instance Execution Now Reliable?

**YES** when UseInstrumentExecutionAuthority=true. IEA + router + SharedAdoptedOrderRegistry provide correct routing and cross-instance fill journaling.

**NO** when IEA disabled with multiple charts on same instrument — no shared registry.

---

## 7. ORDER SUBMISSION / INITIALIZED-STATE RELIABILITY

### Why Orders Remain Local-Only (Initialized)

NinjaTrader OrderState.Initialized = order created but not yet submitted to broker. Causes:

1. **Disconnect**: RiskGate blocks; `_guard.IsExecutionAllowed()` false.
2. **Fail-closed**: Instrument frozen, recovery state, kill switch.
3. **Unsafe state**: INSTRUMENT_FROZEN, STREAM_NOT_ARMED, etc.
4. **Strategy state**: Stream not RANGE_LOCKED, intent not ready.
5. **Context not ready**: SetNTContext not called, bootstrap not complete.
6. **Broker/account not ready**: Sim account not verified, connection lost.

### Blocking Matrix

| Condition | Log/Event | Expected | Operator Action |
|-----------|-----------|----------|-----------------|
| RECOVERY_STATE | EXECUTION_BLOCKED, RISK_CHECK_EVALUATED | Yes | Wait for RECOVERY_COMPLETE |
| KILL_SWITCH | GLOBAL_KILL_SWITCH_ACTIVATED | Yes | Disable kill switch |
| INSTRUMENT_FROZEN | EXECUTION_BLOCKED | Yes | force_reconcile or flatten |
| STREAM_NOT_ARMED | EXECUTION_BLOCKED | Yes | Wait for ARMED |
| TIMETABLE_NOT_VALIDATED | EXECUTION_BLOCKED | Yes | Fix timetable |
| IEA queue blocked | IEA_ENQUEUE_REJECTED_INSTRUMENT_BLOCKED | Yes | Restart |
| EXEC_ROUTER_ENDPOINT_CONFLICT | CRITICAL | Yes | One chart per (account, instrument) |

### Assessment

Orders stuck in Initialized are **expected fail-closed** when any gate blocks. Not an execution bug.

---

## 8. PROTECTIVE / BE EXECUTION RELIABILITY

### Protective Order Placement

- After entry fill: `HandleEntryFill` → submit stop + target via IEA.
- Recovery state blocks: `_isExecutionAllowedCallback()` → FailClosed (flatten).

### Protective Audit

- IEA validates stop/target prices, quantities against policy.
- PROTECTIVE_QUANTITY_MISMATCH, PROTECTIVE_MISMATCH → FailClosed.

### Missing Stop / Missing Target Detection

- Adoption requires both stop and target.
- VerifyRegistryIntegrity checks broker vs registry.

### BE Trigger Logic

- `EvaluateBreakEvenDirect` in IEA worker.
- FIRST_TO_TRIGGER policy; dedup via `_processedExecutionIds`.

### Stale/No-Tick Handling

- BE evaluation runs on tick; no tick → no BE move (acceptable).

### Instrument Mapping

- Chart instrument vs execution instrument: ExecutionInstrumentResolver, timetable mapping.
- Journal stores execution instrument; reconciliation uses both inst and execVariant.

### Can Robot-Owned Exposure Exist Without Verified Protection?

**No.** Entry fill without successful protective submission → FailClosed (flatten). Recovery state blocks protectives → FailClosed (flatten).

### Classification

| Area | Status |
|------|--------|
| Protective submission after fill | **Safe** — blocked during recovery → flatten |
| Protective audit | **Safe** — mismatch → FailClosed |
| Missing stop/target | **Safe** — adoption requires both; mismatch → FailClosed |
| BE trigger | **Safe** — dedup, policy |
| Instrument mapping | **Safe** — execution instrument used consistently |

---

## 9. FAIL-CLOSED QUALITY

### When Fail-Closed Is Triggered

| Trigger | Location |
|---------|----------|
| Kill switch | RiskGate |
| Recovery state | RiskGate, IEA |
| Instrument frozen | RiskGate |
| Journal corruption | ExecutionJournal |
| Protective mismatch | IEA |
| Intent incomplete | IEA |
| ORDER_REGISTRY_MISSING (after adoption attempt) | MismatchEscalationCoordinator |
| RECONCILIATION_QTY_MISMATCH | StandDownStreamsForInstrument |
| IEA queue timeout/overflow | _instrumentBlocked |
| Unowned fill | Flatten |

### Correctness

- Triggers are correct for safety.
- Fail-closed prevents new risk when state is ambiguous.

### Dead-End Risk

- **RECOVERY_POSITION_UNMATCHED**: Recovery aborted; remains fail-closed until operator acts.
- **Instrument frozen**: Requires force_reconcile or flatten + restart.
- **IEA queue poison**: Instrument blocked until restart.

### Bounded Recovery

- Adoption path provides recovery for ORDER_REGISTRY_MISSING.
- Force reconcile script allows operator override when broker is correct.

### Over-Triggering / Under-Triggering

- **Appropriately conservative**: Prefer false fail-closed over incorrect execution.
- No evidence of under-triggering in critical paths.

---

## 10. PLATFORM DEPENDENCY / NINJATRADER LIMITS

### Classification of Recent Incidents

| Issue | Root Class | Fixed? | Platform-Limited? | Migration-Sensitive? |
|-------|------------|--------|------------------|----------------------|
| Connection loss → orders deleted | Robot logic (adoption gap) | ✅ | Partial (broker may cancel) | No |
| Bootstrap snapshot before broker visible | Robot logic (timing) | ✅ (reconciliation recovery) | Partial (NT async repopulation) | No |
| Unfilled entry stops not adopted | Robot logic | ✅ | No | No |
| OCO sibling CancelPending as Rejected | Platform (NT behavior) | ✅ | Yes | No |
| RECONCILIATION_QTY_MISMATCH (stale cache) | Robot logic | ✅ | No | No |
| RECONCILIATION_QTY_MISMATCH (file lock) | Robot logic | ✅ | Partial (Windows file locking) | No |
| Adopted fill to wrong instance | Robot logic | ✅ | No | No |
| Journal instrument key mismatch | Robot logic | ✅ | No | No |

### Platform Limitations

| Limitation | Impact |
|------------|--------|
| NT account.Orders repopulated asynchronously after reconnect | Bootstrap may see 0; mitigation: TryRecoveryAdoption |
| NT OrderState.Initialized | Order not yet at broker; expected |
| NT threading (strategy thread for Submit) | IEA EntrySubmissionLock serializes |
| Broker may cancel on disconnect | Cannot recover; accept |

---

## 11. CURRENT GO / NO-GO STATUS

| Use Case | Status | Reason |
|----------|--------|--------|
| **Replay** | YES | Deterministic; IEA supports replay |
| **Sim** | YES | SIM mode with IEA; adoption and reconciliation fixes applied |
| **Supervised live small size** | CONDITIONAL | Requires UseInstrumentExecutionAuthority=true; operator monitors RECONCILIATION_*, RECOVERY_*, DISCONNECT_* |
| **Supervised prop-firm use** | CONDITIONAL | Same; prop firms may have stricter audit requirements |
| **Unsupervised live use** | NO | RECOVERY_POSITION_UNMATCHED, force_reconcile, instrument freeze require operator |

---

## 12. TOP REMAINING RISKS

| Risk | Severity | Likelihood | Operational Impact | Action |
|------|----------|------------|--------------------|--------|
| Bootstrap snapshot race (broker not yet repopulated) | Medium | Low | Adoption delayed up to 1s; rarely fail-closed if adoption succeeds | Monitor |
| RECOVERY_POSITION_UNMATCHED | High | Low | Recovery aborted; manual flatten | Monitor; document procedure |
| Flatten verify fails after retries | High | Low | Position may remain; manual intervention | Monitor |
| Wrong QTSW2_PROJECT_ROOT | High | Low | Journal path wrong; RECONCILIATION_QTY_MISMATCH | Operator checklist |
| IEA queue poison (3 consecutive worker errors) | High | Very low | Instrument blocked until restart | Monitor |
| SharedAdoptedOrderRegistry 60-min expiry | Low | Very low | Long-held position fill unjournaled | Accept |

---

## 13. FINAL HEALTH TABLE

| Subsystem | Status | Reliability Level | Main Risk | Action |
|-----------|--------|-------------------|-----------|--------|
| Startup/Bootstrap | ACCEPTABLE | Supervised-only | Snapshot race; adoption mitigates | Monitor RECOVERY_ADOPTION_* |
| Disconnect/Recovery | ACCEPTABLE | Supervised-only | RECOVERY_POSITION_UNMATCHED | Document operator procedure |
| Ownership/Reconciliation | ACCEPTABLE | Supervised-only | Force reconcile when needed | Use force_reconcile.ps1 |
| Order Lifecycle | ACCEPTABLE | Supervised-only | Flatten verify failure | Monitor |
| Protective/BE | STRONG | Production-grade | None identified | — |
| Multi-instance | STRONG | Production-grade | IEA must be enabled | Config check |
| Fail-closed | STRONG | Production-grade | Dead-end on UNMATCHED | Operator procedure |
| Platform Dependency | ACCEPTABLE | Supervised-only | NT async, broker cancel | Accept |

**Statuses**: STRONG, ACCEPTABLE, FRAGILE, UNSAFE  
**Reliability levels**: production-grade, supervised-only, unstable

---

## 14. FINAL DIRECT ANSWERS

### Is the execution layer currently reliable enough for supervised trading?

**YES**, with conditions: UseInstrumentExecutionAuthority=true, operator monitors critical events (RECONCILIATION_*, RECOVERY_*, DISCONNECT_*), and procedures exist for RECOVERY_POSITION_UNMATCHED and force_reconcile.

### What is the single biggest remaining execution weakness?

**RECOVERY_POSITION_UNMATCHED** — when broker has positions that cannot be matched to streams/journal, recovery aborts and the system remains fail-closed until manual flatten or reconciliation.

### What is the single biggest NinjaTrader-imposed limitation?

**Asynchronous repopulation of account.Orders after reconnect** — bootstrap snapshot can see broker_working=0 before NinjaTrader repopulates. Mitigated by TryRecoveryAdoption on reconciliation tick.

### What must be manually monitored during live use?

- RECONCILIATION_QTY_MISMATCH, RECONCILIATION_CONTEXT
- RECOVERY_POSITION_UNMATCHED, RECOVERY_ACCOUNT_SNAPSHOT_FAILED
- DISCONNECT_FAIL_CLOSED_ENTERED, DISCONNECT_RECOVERY_*
- ORDER_REGISTRY_MISSING_FAIL_CLOSED
- EXECUTION_FLATTEN_VERIFY_FAILED
- IEA_ENQUEUE_AND_WAIT_TIMEOUT, EXECUTION_QUEUE_POISON_DETECTED

### What must be fixed before unsupervised/autonomous use?

1. **Automated recovery from RECOVERY_POSITION_UNMATCHED** — or clear policy to flatten unmatched.
2. **Bootstrap convergence loop** — wait for broker state or bounded retries instead of single snapshot.
3. **Force reconcile automation** — when broker correct and journal wrong, auto-unfreeze with audit.
4. **Eliminate operator-dependent procedures** — no manual flatten, force_reconcile, or restart decisions.
