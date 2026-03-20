# NinjaTrader Platform-Limit Audit — QTSW2 Robot & Breakout Strategy

**Date**: 2026-03-18  
**Scope**: Platform suitability and failure-mode audit of NinjaTrader as execution host for QTSW2  
**Constraint**: Audit only — no implementation, refactoring, or code changes

---

## 1. REQUIREMENTS OF THIS ROBOT

### 1.1 Explicit Requirements and Rationale

| Requirement | Why QTSW2 Needs It |
|-------------|--------------------|
| **Deterministic order lifecycle** | Breakout strategy submits entry stops, protectives, BE modifications, and exits in a strict sequence. Order state transitions (Initialized → Submitted → Working → Filled → Cancelled) must be predictable and observable. Non-determinism causes unknown-order flatten, protective mismatch, or orphan exposure. |
| **Strict ownership mapping** | Every broker order and position must map to exactly one intent/stream. OCO groups, QTSW2 tags, and intent_id linkage are used for adoption and fill resolution. Ambiguity → fail-closed (flatten). |
| **Journal-backed restart recovery** | ExecutionJournal on disk is the canonical record of submission/fill state per (trading_date, stream, intent_id). On restart, IEA registry is cleared; adoption restores from journal when broker orders appear. Without journal, restart cannot recover. |
| **Fail-closed safety** | When state is ambiguous (broker ahead, journal ahead, unknown order, registry mismatch), the system must block new execution and flatten rather than guess. Fail-open would risk unmanaged exposure. |
| **Instrument-level flatten authority** | Emergency flatten must be possible per instrument (e.g., MYM only) without affecting others. Account.Flatten(ICollection&lt;Instrument&gt;) provides this. |
| **Protective order certainty** | Entry fill without verified stop/target → FailClosed. BE modification must not race with protective submission. Protective audit validates quantities and prices. |
| **Multi-stream / multi-instrument coordination** | Timetable drives multiple streams (YM1, NQ2, RTY2, etc.) across instruments (MYM, MNQ, M2K). One chart per execution instrument; IEA shared per (account, executionInstrumentKey). |
| **Reconnect safety** | Connection loss → DISCONNECT_FAIL_CLOSED; block execution; emergency flatten permitted. Reconnect → recovery (snapshot, reconcile, adopt). Broker may cancel orders on disconnect; robot must handle. |
| **Cross-instance consistency** | ExecutionUpdateRouter routes callbacks by (account, executionInstrumentKey). SharedAdoptedOrderRegistry enables cross-instance fill journaling. First instance wins for endpoint registration. |
| **Human-auditable event trail** | RobotEventTypes, robot_*.jsonl, execution_events/*.jsonl, RECONCILIATION_*, BOOTSTRAP_*, RECOVERY_* for incident reconstruction. |
| **Bounded recovery from ambiguous state** | TryRecoveryAdoption, force_reconcile.ps1, UnmatchedPositionPolicyEvaluator. No indefinite hold; adopt or flatten. |
| **Supervised live suitability** | Operator monitors RECONCILIATION_*, RECOVERY_*, DISCONNECT_*; manual flatten or force_reconcile when needed. |
| **Long-term path to autonomy** | Reduce operator-dependent procedures; automated recovery from RECOVERY_POSITION_UNMATCHED; bootstrap convergence; force reconcile automation. |

---

## 2. NINJATRADER'S EXECUTION MODEL vs REQUIREMENTS

### 2.1 Strategy Instance Model

| Aspect | NinjaTrader Reality | Classification |
|--------|---------------------|----------------|
| **One strategy per chart** | Each chart runs one strategy instance. Multiple charts → multiple instances. | **Strong fit** |
| **Account-level order/execution events** | `Account.OrderUpdate`, `Account.ExecutionUpdate` fire for all instances subscribed to that account. All instances receive every callback. | **Workable with mitigation** — ExecutionUpdateRouter routes by (account, instrument); first instance wins. |
| **IsUnmanaged = true** | Required for manual order management. Robot owns CreateOrder, Submit, Cancel, Flatten. | **Strong fit** |
| **ConnectionLossHandling.KeepRunning** | Reduces restarts from brief connection loss. Orders not cancelled by NT on disconnect (broker-dependent). | **Workable** — broker may still cancel; robot blocks execution. |

### 2.2 Chart / Workspace Coupling

| Aspect | NinjaTrader Reality | Classification |
|--------|---------------------|----------------|
| **Chart instrument = execution instrument** | Strategy.Instrument is the chart's instrument (e.g., MYM 03-26). Execution key derived from Instrument.FullName root (MYM). | **Strong fit** |
| **One chart per instrument** | RobotSimStrategy enforces one instance per (account, executionInstrumentKey). Duplicate → DUPLICATE_INSTANCE_DETECTED, stand-down. | **Strong fit** |
| **BarsRequest** | Bars requested in State.DataLoaded for all execution instruments from enabled streams. Multi-series (BarsArray) for cross-instrument streams. | **Workable** — BarsInProgress used; bar timing drift risk. |

### 2.3 Account / Order / Position Visibility

| Aspect | NinjaTrader Reality | Classification |
|--------|---------------------|----------------|
| **account.Orders** | Synchronous read; repopulated asynchronously after reconnect. Bootstrap snapshot can see broker_working=0 before repopulation. | **Fragile** — single snapshot, no convergence loop. Mitigated by TryRecoveryAdoption on reconciliation tick. |
| **account.Positions** | Same async repopulation. Snapshot at bootstrap may be stale. | **Fragile** |
| **Order.Instrument** | Available on Order; used for routing and filtering. | **Strong fit** |
| **Order.OrderState** | Initialized, Submitted, Working, Filled, Cancelled, Rejected, etc. Initialized = local-only, not yet at broker. | **Strong fit** — robot blocks when RiskGate disallows; orders stay Initialized until allowed. |

### 2.4 OnOrderUpdate, OnExecutionUpdate, OnPositionUpdate

| Aspect | NinjaTrader Reality | Classification |
|--------|---------------------|----------------|
| **OnOrderUpdate** | Fires for order state changes. All instances receive. Robot filters by Instrument before forwarding. | **Workable** — filter + router. |
| **OnExecutionUpdate** | Fires for fills. ExecutionUpdateRouter routes by (account, executionInstrumentKey) to single endpoint. | **Workable** — SharedAdoptedOrderRegistry for cross-instance fill resolution. |
| **OnPositionUpdate** | Not used by robot. Position inferred from account.Positions snapshot. | **Workable** |
| **Callback ordering** | NinjaTrader does not guarantee order of OrderUpdate vs ExecutionUpdate. Fill can arrive before order is in registry (race). | **Fragile** — 500ms grace, SharedAdoptedOrderRegistry, deferred retry mitigate. |

### 2.5 OnBarUpdate and BarsInProgress

| Aspect | NinjaTrader Reality | Classification |
|--------|---------------------|----------------|
| **OnBarUpdate** | Called on bar close (Calculate.OnBarClose). Single BarsInProgress per call when multi-series. | **Workable** |
| **BarsInProgress** | Indicates which series triggered the bar. Robot uses CurrentBars[BarsInProgress] &lt; 1 guard. | **Workable** — misuse risk if bar routing wrong. |
| **Strategy thread** | All NT order APIs (Submit, Cancel, Flatten) must run on strategy thread. IEA worker cannot call them directly (deadlock). | **Fragile** — EntrySubmissionLock + DrainNtActions on strategy thread; protective/BE still on worker (potential future issue). |

### 2.6 Lifecycle / OnStateChange

| Aspect | NinjaTrader Reality | Classification |
|--------|---------------------|----------------|
| **State.DataLoaded** | Engine.Start(), BarsRequest, SetNTContext (Realtime). Bootstrap runs at SetNTContext. | **Workable** — bootstrap snapshot timing race. |
| **State.Realtime** | Live trading. Order/execution events active. | **Strong fit** |
| **State.Historical** | Backfill; no execution. | **Strong fit** |
| **Restart** | New process; IEA registry cleared; new run_id. Bootstrap runs again. | **Fragile** — adoption restores; single snapshot. |

### 2.7 Threading / Event Ordering / Callback Timing

| Aspect | NinjaTrader Reality | Classification |
|--------|---------------------|----------------|
| **Strategy thread** | OnBarUpdate, OnOrderUpdate, OnExecutionUpdate on same thread (or NT-managed). | **Workable** |
| **IEA worker** | Separate thread for protective, BE, flatten. Must not call NT Submit directly (deadlock). Entry submission marshalled to strategy thread. | **Workable with mitigation** — NQ1 IEA timeout fixed by EntrySubmissionLock. |
| **EnqueueAndWait** | 12s timeout. Worker blocks; strategy thread drains NT actions. | **Fragile** — under load NT Submit can block 6+ s; protective path still uses worker. |
| **Callback timing** | Execution update can arrive before order in OrderMap (118ms race observed). | **Fragile** — grace period, SharedAdoptedOrderRegistry. |

### 2.8 Reconnect Behavior

| Aspect | NinjaTrader Reality | Classification |
|--------|---------------------|----------------|
| **Connection status** | OnConnectionStatusUpdate; robot transitions to DISCONNECT_FAIL_CLOSED. | **Strong fit** |
| **Broker repopulation** | account.Orders repopulated asynchronously. No API to wait for convergence. | **Fragile** — bootstrap race; TryRecoveryAdoption mitigates. |
| **Broker cancels on disconnect** | Broker-specific. Robot cannot prevent. | **Platform limitation** |
| **4+ disconnects in 5 min** | NinjaTrader disables strategies; restart required. | **Platform limitation** |

### 2.9 Local Strategy State vs Account State

| Aspect | NinjaTrader Reality | Classification |
|--------|---------------------|----------------|
| **IEA in-memory** | OrderMap, OrderRegistry, IntentMap cleared on restart. No persistence. | **Fragile** — adoption restores from journal when broker orders appear. |
| **Journal on disk** | ExecutionJournal persists. Survives restart. | **Strong fit** |
| **Slot journals** | Stream state; RANGE_LOCKED, StopBracketsSubmittedAtLock. | **Strong fit** |

### 2.10 Account-Level Flatten

| Aspect | NinjaTrader Reality | Classification |
|--------|---------------------|----------------|
| **Account.Flatten(ICollection&lt;Instrument&gt;)** | Instrument-scoped flatten. Robot uses for emergency flatten. | **Strong fit** |
| **Flatten verify** | Robot retries; verifies position flat. EXECUTION_FLATTEN_VERIFY_FAILED after max retries. | **Workable** — manual intervention if verify fails. |

### 2.11 Strategy Restart / Re-Enable Semantics

| Aspect | NinjaTrader Reality | Classification |
|--------|---------------------|----------------|
| **Disable → Enable** | Strategy stops and restarts. New process or same process depending on NT version. | **Fragile** — registry cleared; bootstrap runs. |
| **Re-enable** | Same as restart. Bootstrap snapshot; adoption if broker has orders. | **Workable** — post-fix adoption path. |

---

## 3. FULL FAILURE MODE INVENTORY

### A. Startup / Bootstrap

| Failure Mode | Description | Root Cause Class | Current Mitigation | Residual Risk | Classification |
|--------------|-------------|------------------|--------------------|--------------|---------------|
| **Broker orders not yet visible** | Bootstrap snapshot reads account.Orders; NinjaTrader repopulates async after reconnect. Snapshot sees broker_working=0. | NT async repopulation | TryRecoveryAdoption in AssembleMismatchObservations before ORDER_REGISTRY_MISSING | Low — adoption on 1s reconciliation tick | NinjaTrader limitation |
| **Account state not fully hydrated** | Same; positions may also be delayed. | NT async repopulation | Reconciliation runs periodically; adoption when orders appear | Low | NinjaTrader limitation |
| **Strategy starts before broker truth stable** | SetNTContext → BeginBootstrapForInstrument → snapshot. No wait. | Single-pass bootstrap | TryRecoveryAdoption; RESUME if broker_working=0 | Medium — first adoption delayed up to 1s | NinjaTrader limitation |
| **Journal/registry/bootstrap disagreement** | Journal says EntrySubmitted; registry empty; broker has orders. | Restart clears registry | GetAdoptionCandidateIntentIds (EntrySubmitted && !TradeCompleted); adoption | Low | Robot logic (fixed) |

### B. Disconnect / Reconnect

| Failure Mode | Description | Root Cause Class | Current Mitigation | Residual Risk | Classification |
|--------------|-------------|------------------|--------------------|--------------|---------------|
| **Connection loss during live order lifecycle** | Orders working; disconnect; DISCONNECT_FAIL_CLOSED. | Connection | Block execution; emergency flatten permitted | Low | Acceptable fail-safe |
| **Broker cancels on disconnect** | Some brokers cancel working orders when connection lost. | Broker/platform | Cannot recover; journal may be ahead | Medium — orders lost | Broker/platform interaction |
| **Partial broker rehydration after reconnect** | account.Orders repopulated in waves. | NT async | TryRecoveryAdoption on each reconciliation tick | Low | NinjaTrader limitation |
| **Reconnect while working orders or positions exist** | Recovery runs; adoption; RECOVERY_COMPLETE or RECOVERY_POSITION_UNMATCHED | Multi-factor | UnmatchedPositionPolicyEvaluator; flatten if not proven | High — RECOVERY_POSITION_UNMATCHED requires manual flatten | Robot + platform |

### C. Order Lifecycle

| Failure Mode | Description | Root Cause Class | Current Mitigation | Residual Risk | Classification |
|--------------|-------------|------------------|--------------------|--------------|---------------|
| **Order stuck Initialized** | Order created but not submitted. RiskGate blocks (recovery, kill switch, frozen instrument). | Expected | EXECUTION_BLOCKED logged; no bug | None | Acceptable fail-safe |
| **Order update and execution update ordering** | Fill arrives before order in registry. | NT callback ordering | 500ms grace; SharedAdoptedOrderRegistry; deferred retry | Low | NinjaTrader limitation |
| **Fills arriving before local ownership ready** | Cross-instance: Instance B receives fill for order adopted by Instance A. | Multi-instance | SharedAdoptedOrderRegistry + TryGetAdoptionCandidateEntry; journal hydration | Low | Robot logic (fixed) |
| **Cancel/replace edge cases** | OCO sibling CancelPending reported as Rejected. | NT behavior | Skip RecordRejection for CancelPending | Low | NinjaTrader limitation (workaround) |
| **OCO sibling behavior** | One leg fills; other cancels. CancelPending can be misclassified. | NT behavior | Fixed 2026-03-17 | Low | NinjaTrader limitation |
| **Protective submission races** | BE move vs protective submit. | Threading | IEA serializes; EntrySubmissionLock | Low | Robot logic |

### D. Ownership / Reconciliation

| Failure Mode | Description | Root Cause Class | Current Mitigation | Residual Risk | Classification |
|--------------|-------------|------------------|--------------------|--------------|---------------|
| **Broker ahead of journal** | Broker has position; journal does not. External fill or journal lag. | Multi-cause | RECONCILIATION_QTY_MISMATCH; StandDownStreamsForInstrument; force_reconcile or flatten | Medium | Robot + platform |
| **Journal ahead of broker** | Journal has fill; broker flat. External close or broker lag. | Multi-cause | Same | Medium | Robot + platform |
| **Working orders not in registry** | broker_working>0, iea_working=0. | Restart or wrong data source | TryRecoveryAdoption; GetOwnedPlusAdoptedWorkingCount (not journal) | Low | Robot defect (fixed) |
| **Unmatched positions** | Broker position cannot be proven to robot-owned. | RECOVERY_POSITION_UNMATCHED | UnmatchedPositionPolicyEvaluator → flatten or adopt | High — manual flatten if policy says flatten | Robot + platform |
| **Manual/external contamination** | Orders placed outside robot. | Operator/broker | Treat as unknown; flatten | Low | Acceptable fail-safe |
| **Stale or wrong journal root/path** | QTSW2_PROJECT_ROOT wrong; journal in different dir. | Operator | RECONCILIATION_QTY_MISMATCH; operator must set env | Medium | Operator procedure |

### E. Multi-Instance / Multi-Chart

| Failure Mode | Description | Root Cause Class | Current Mitigation | Residual Risk | Classification |
|--------------|-------------|------------------|--------------------|--------------|---------------|
| **Same instrument across multiple charts** | Two MNQ charts → EXEC_ROUTER_ENDPOINT_CONFLICT. | Misconfiguration | First wins; second fails to register | Low | Acceptable fail-safe |
| **Routing callbacks to wrong local context** | Fill for MGC routed to M2K. | Router key | ExecutionUpdateRouter by (account, executionInstrumentKey) from Order.Instrument | Low | Robot logic |
| **Split ownership across instances** | Instance A adopts; Instance B receives fill; B has empty OrderMap. | Multi-instance | SharedAdoptedOrderRegistry; cross-instance journaling | Low | Robot logic (fixed) |
| **Local OrderMap vs shared state divergence** | IEA shared; OrderMap in IEA. When IEA disabled, adapter has local OrderMap — no cross-instance. | Design | UseInstrumentExecutionAuthority=true required for multi-chart same instrument | High if IEA disabled | Robot design |

### F. Data / Timing

| Failure Mode | Description | Root Cause Class | Current Mitigation | Residual Risk | Classification |
|--------------|-------------|------------------|--------------------|--------------|---------------|
| **Market data not arriving** | No ticks; BE cannot evaluate. | Data feed | BE runs on tick; no tick → no BE move (acceptable) | Low | Platform |
| **Stale ticks** | BE_TICK_STALE_WARNING. | Data/timing | Logged; BE may skip | Low | Platform |
| **Bar timing drift** | BarsInProgress wrong series. | Multi-series | Guard: CurrentBars[BarsInProgress] &lt; 1 | Low | Robot logic |
| **BarsInProgress misuse risk** | Bar routed to wrong stream. | Logic error | StreamStateMachine routing | Low | Robot logic |

### G. Protective / BE Management

| Failure Mode | Description | Root Cause Class | Current Mitigation | Residual Risk | Classification |
|--------------|-------------|------------------|--------------------|--------------|---------------|
| **Stop missing** | Entry filled; no protective stop. | Adoption or submission failure | PROTECTIVE_MISSING_STOP → FailClosed; flatten | Low | Acceptable fail-safe |
| **Target missing** | Same | Same | Same | Low | Acceptable fail-safe |
| **BE blocked by context mismatch** | BE_GATE_BLOCKED INSTRUMENT_MISMATCH (account position M2K, chart MGC). | Multi-chart | IEA per instrument; BE evaluates per execution instrument | Low | Robot logic |
| **No-tick / stale-tick behavior** | BE evaluation needs tick. | Data | No tick → no BE; acceptable | Low | Platform |
| **Recovery state preventing protective maintenance** | DISCONNECT_FAIL_CLOSED blocks protectives. | Design | Flatten on recovery; no queue | Low | Acceptable fail-safe |

### H. Flatten / Recovery

| Failure Mode | Description | Root Cause Class | Current Mitigation | Residual Risk | Classification |
|--------------|-------------|------------------|--------------------|--------------|---------------|
| **Flatten requested but not executed** | Account.Flatten called; position remains. | Broker/NT | FlattenWithRetry; verify | Medium | Broker/platform interaction |
| **Flatten verify fails** | EXECUTION_FLATTEN_VERIFY_FAILED after retries | Broker/NT | Manual intervention | High | Broker/platform interaction |
| **Partial flatten** | Some positions closed; others remain. | Broker | Verify checks; retry | Medium | Broker/platform interaction |
| **Dead-end fail-closed state** | RECOVERY_POSITION_UNMATCHED; instrument frozen. | Policy | Operator must flatten or force_reconcile | High | Robot + platform |
| **Automation requiring manual override** | force_reconcile.ps1 when broker correct, journal wrong | Design | Operator procedure | Medium | Robot design |

### I. Logging / Observability / Ops

| Failure Mode | Description | Root Cause Class | Current Mitigation | Residual Risk | Classification |
|--------------|-------------|------------------|--------------------|--------------|---------------|
| **Critical decisions not surfaced live** | BOOTSTRAP_*, RECOVERY_DECISION_* not in LIVE_CRITICAL → watchdog misses | Config | Add to LIVE_CRITICAL (recommended) | Medium | Robot/ops |
| **Operator cannot tell safe vs unsafe** | No single OPERATOR_STATUS_SNAPSHOT | Design | Infer from multiple events | Medium | Robot design |
| **Too much noise during incidents** | RECONCILIATION_ORDER_SOURCE_BREAKDOWN every 60s; ENGINE_TICK_CALLSITE every tick | Throttling | EngineLogDedupe; rate limits | Low | Robot design |
| **Split evidence across multiple files/pipelines** | robot_*.jsonl, execution_events/*.jsonl, logs/health/ | Design | Operator must grep multiple | Medium | Robot design |

---

## 4. INCIDENT HISTORY CLASSIFICATION

| Incident | Mainly Robot Bug or NT-Hosting? | Fixable in Robot Logic? | Likely to Recur (Platform Structure)? | Direct API Would Reduce/Eliminate? |
|----------|--------------------------------|-------------------------|--------------------------------------|-----------------------------------|
| **Bootstrap snapshot before broker state visible** | Both — robot assumed snapshot complete; NT repopulates async | Yes — TryRecoveryAdoption added | Yes — NT has no "wait for broker ready" API | Partially — direct API could poll/wait |
| **Unfilled entry stops not adopted** | Robot bug — GetActiveIntentsForBEMonitoring excluded them | Yes — GetAdoptionCandidateIntentIds | No | No |
| **Cross-instance adopted fill not journaled** | Robot bug — fill to instance without OrderMap | Yes — SharedAdoptedOrderRegistry | No | Partially — single process would eliminate |
| **M2K unmanaged exposure** | Both — fill to wrong instance; adoption in one, fill to another | Yes — SharedAdoptedOrderRegistry fix | Low — fix deployed | Partially |
| **Initialized order stuck while fail-closed** | Expected — RiskGate blocks; not a bug | N/A | Yes — whenever blocked | No |
| **RECONCILIATION_QTY_MISMATCH** (stale cache) | Robot bug — per-instance cache | Yes — disk read | No | No |
| **RECONCILIATION_QTY_MISMATCH** (file lock) | Both — Windows file locking | Yes — ReadJournalFileWithRetry, FileShare.ReadWrite | Possible | No |
| **RECOVERY_POSITION_UNMATCHED** | Both — policy correctly flattens; operator must act when flatten fails or manual override needed | Partially — automate force_reconcile | Yes — ambiguous positions recur | Partially |
| **Disconnect → restart → apparent order loss** | Both — bootstrap race + adoption gap | Yes — TryRecoveryAdoption, GetAdoptionCandidateIntentIds | Yes — NT async repopulation | Partially |
| **ORDER_REGISTRY_MISSING** (journal vs IEA) | Robot bug — local_working from journal not IEA | Yes — GetOwnedPlusAdoptedWorkingCount | No | No |
| **NQ1 IEA EnqueueAndWait timeout** | Both — NT Submit blocks worker; deadlock | Yes — EntrySubmissionLock, strategy thread | Yes — NT requires strategy thread for Submit | Yes — direct API could use async |
| **Unknown order flatten (MNQ 2026-03-03)** | Robot — instance swap; new instance empty OrderMap | Yes — SharedAdoptedOrderRegistry, adoption | Low | Partially |

---

## 5. PLATFORM COMPATIBILITY MATRIX

| Requirement | NinjaTrader Fit | Current Mitigation | Residual Risk | Long-Term Verdict |
|-------------|-----------------|--------------------|---------------|-------------------|
| **Deterministic restart recovery** | Fragile | TryRecoveryAdoption; GetAdoptionCandidateIntentIds; journal-backed adoption | Bootstrap single snapshot; no convergence loop | Workable supervised; incompatible with full autonomy |
| **Single authoritative ownership** | Workable | IEA + ExecutionUpdateRouter + SharedAdoptedOrderRegistry | Cross-instance fill race (mitigated); SharedAdoptedOrderRegistry 60-min expiry | Workable |
| **Cross-instance consistency** | Workable | One endpoint per (account, instrument); SharedAdoptedOrderRegistry | IEA must be enabled; duplicate chart → fail | Workable |
| **Autonomous unmatched resolution** | Incompatible | UnmatchedPositionPolicyEvaluator → flatten; RECOVERY_POSITION_UNMATCHED → manual | Operator must flatten or force_reconcile | Incompatible |
| **Protective certainty** | Strong | IEA validation; FailClosed on mismatch; recovery blocks protectives | None identified | Strong |
| **Instrument-level fail-closed** | Strong | Account.Flatten(Instrument); RiskGate; frozen instruments | None | Strong |
| **Operator-friendly supervision** | Workable | RECONCILIATION_*, RECOVERY_*, DISCONNECT_*; force_reconcile.ps1 | BOOTSTRAP/RECOVERY not in watchdog; split pipelines | Workable |
| **Full autonomy** | Incompatible | N/A | RECOVERY_POSITION_UNMATCHED, force_reconcile, bootstrap race | Incompatible |

---

## 6. SUPERVISED VS AUTONOMOUS SUITABILITY

| Use Case | Verdict | Exact Reason | Limiting Factor |
|----------|---------|--------------|-----------------|
| **Replay** | YES | Deterministic; no live execution; IEA supports replay | — |
| **Sim** | YES | SIM mode; IEA; adoption and reconciliation fixes; ConnectionLossHandling.KeepRunning | — |
| **Supervised small live** | CONDITIONAL | UseInstrumentExecutionAuthority=true; operator monitors RECONCILIATION_*, RECOVERY_*, DISCONNECT_*; procedures for RECOVERY_POSITION_UNMATCHED, force_reconcile | NinjaTrader platform structure (bootstrap race, async repopulation) + robot (manual procedures) |
| **Supervised prop-firm use** | CONDITIONAL | Same as small live; prop firms may require stricter audit; journal + execution_events provide trail | Same |
| **Unattended/autonomous live** | NO | RECOVERY_POSITION_UNMATCHED requires manual flatten; force_reconcile requires operator; bootstrap has no convergence; 4+ disconnects → NT disables strategies | NinjaTrader platform structure + robot design |

---

## 7. WHAT NINJATRADER CAN SAFELY SUPPORT

1. **Supervised fail-closed execution** — RiskGate, DISCONNECT_FAIL_CLOSED, instrument freeze, flatten on ambiguity.
2. **Deterministic flatten-on-uncertainty** — UnmatchedPositionPolicyEvaluator, ORDER_REGISTRY_MISSING → TryRecoveryAdoption → fail-closed.
3. **Journal-backed audit trail** — ExecutionJournal on disk; idempotency; adoption candidates.
4. **Instrument-level safety control** — Account.Flatten(Instrument); frozen instruments per instrument.
5. **Multi-instance execution with IEA** — ExecutionUpdateRouter, SharedAdoptedOrderRegistry, one endpoint per (account, instrument).
6. **Connection status observation** — OnConnectionStatusUpdate; DISCONNECT_FAIL_CLOSED; recovery state machine.
7. **Protective order validation** — IEA audit; FailClosed on mismatch; no unprotected exposure.
8. **Replay and SIM** — Full support; no live broker dependency.

---

## 8. WHAT NINJATRADER CANNOT SAFELY SUPPORT AT THE TARGET LEVEL

1. **Clean single-process authoritative execution ownership** — Multiple strategy instances; account-level callbacks; IEA shared but registry in-memory. Restart clears registry.
2. **Platform-independent restart determinism** — Bootstrap is single snapshot; no "wait for broker ready"; adoption depends on reconciliation tick (1s).
3. **Rich autonomous ambiguity resolution** — RECOVERY_POSITION_UNMATCHED requires operator; force_reconcile is manual; no automated "broker correct, journal wrong" recovery.
4. **Perfect cross-instance simplicity** — SharedAdoptedOrderRegistry 60-min expiry; fill routing depends on first instance; duplicate chart → conflict.
5. **Full autonomy without manual procedures** — Flatten verify failure, RECOVERY_POSITION_UNMATCHED, force_reconcile, instrument unfreeze all require operator.
6. **Guaranteed broker order preservation on disconnect** — Broker may cancel; NT may disable strategies after 4+ disconnects in 5 min.
7. **Synchronous broker state convergence** — No API to wait for account.Orders repopulation.

---

## 9. IF WE STAY ON NINJATRADER, WHAT MUST THE OPERATING MODEL BE?

1. **Strict fail-closed bias** — Never guess; flatten on ambiguity. Unknown order → flatten. RECOVERY_POSITION_UNMATCHED → flatten.
2. **Flatten-on-uncertainty** — When ownership cannot be proven, flatten. UnmatchedPositionPolicyEvaluator implements this.
3. **One-chart-per-instrument discipline** — Exactly one strategy instance per (account, executionInstrumentKey). Duplicate → stand-down.
4. **IEA enabled always** — UseInstrumentExecutionAuthority=true. Non-negotiable for multi-instrument.
5. **Operator monitoring requirements** — Watch RECONCILIATION_QTY_MISMATCH, RECOVERY_POSITION_UNMATCHED, DISCONNECT_FAIL_CLOSED, ORDER_REGISTRY_MISSING_FAIL_CLOSED, EXECUTION_FLATTEN_VERIFY_FAILED, IEA_ENQUEUE_AND_WAIT_TIMEOUT.
6. **Manual procedures that remain necessary** — force_reconcile.ps1 when broker correct and journal wrong; manual flatten when RECOVERY_POSITION_UNMATCHED; restart after IEA timeout/instrument block.
7. **What types of autonomy should not be attempted** — Unattended live; overnight without operator; prop-firm live without supervision; any scenario where RECOVERY_POSITION_UNMATCHED or force_reconcile could occur without operator response.

---

## 10. MIGRATION SENSITIVITY

| Weakness | Fixable Fully in Robot on NT? | Partially Fixable? | Fundamentally Better with Direct API? |
|----------|-------------------------------|--------------------|--------------------------------------|
| **Bootstrap snapshot race** | No — no NT API to wait | Yes — reconciliation recovery mitigates | Yes — direct API could poll until broker ready |
| **RECOVERY_POSITION_UNMATCHED** | No — policy correctly flattens; automation of force_reconcile possible | Yes — could auto force_reconcile with audit | Partially — single process reduces instance swap |
| **Cross-instance fill routing** | Yes — SharedAdoptedOrderRegistry | — | Yes — single process eliminates |
| **IEA worker deadlock** | Yes — marshal to strategy thread | — | Yes — direct API async |
| **Broker cancels on disconnect** | No | No | No — broker behavior |
| **4+ disconnects → NT disables** | No | No | No — NT behavior |
| **Split log pipelines** | Yes — consolidate | — | No |
| **BOOTSTRAP/RECOVERY not in watchdog** | Yes — config change | — | No |
| **Force reconcile manual** | Yes — automate with audit | — | Partially |
| **Flatten verify failure** | No — broker/NT | No | Partially — direct API may have better verify |

---

## 11. FINAL DIRECT ANSWERS

### Can NinjaTrader safely support QTSW2 for supervised live trading?

**YES**, with conditions: UseInstrumentExecutionAuthority=true, one chart per (account, execution instrument), operator monitors critical events (RECONCILIATION_*, RECOVERY_*, DISCONNECT_*), and documented procedures for RECOVERY_POSITION_UNMATCHED and force_reconcile.

### Can NinjaTrader safely support QTSW2 for autonomous live trading?

**NO**. RECOVERY_POSITION_UNMATCHED, force_reconcile, flatten verify failure, and bootstrap timing require operator intervention. NinjaTrader has no API for broker state convergence or deterministic restart recovery. Full autonomy is incompatible with the current platform structure.

### What is the single biggest NinjaTrader-imposed limitation for this robot?

**Asynchronous repopulation of account.Orders after reconnect** — bootstrap snapshot can see broker_working=0 before NinjaTrader repopulates. There is no API to wait for broker state convergence. Mitigation (TryRecoveryAdoption on reconciliation tick) works but is reactive, not deterministic.

### What is the single biggest robot-side weakness that still matters even on another platform?

**RECOVERY_POSITION_UNMATCHED and force_reconcile dependency** — when broker has positions that cannot be proven to robot-owned through journal/continuity evidence, the policy correctly flattens. When broker is correct and journal is wrong, force_reconcile requires operator. Automating force_reconcile with full audit would reduce operator dependency but would need careful safety design on any platform.

### What is the simplest safe operating philosophy for this robot on NinjaTrader?

**Fail-closed, flatten-on-uncertainty, one-chart-per-instrument, IEA always on, operator present.** When in doubt, flatten. Never guess ownership. One instance per instrument. Operator monitors and executes manual procedures when needed.

### Is NinjaTrader a temporary execution shell or a viable long-term execution foundation for QTSW2?

**Viable for supervised live only.** NinjaTrader can be a long-term foundation for replay, sim, and supervised live trading with the operating model in Section 9. It is **not** a viable foundation for unattended/autonomous live. If the goal is full autonomy, a direct API execution layer would materially reduce bootstrap, cross-instance, and threading limitations. NinjaTrader would then be a data/chart host, not the execution authority.

---

*Audit complete. No implementation. No refactoring. Platform suitability and failure-mode assessment only.*
