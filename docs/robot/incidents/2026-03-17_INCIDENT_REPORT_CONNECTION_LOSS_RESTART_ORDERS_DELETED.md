# Incident Report: Connection Loss → Restart → Orders Deleted / Fail-Closed

**Date**: 2026-03-17  
**Type**: Technical incident reconstruction and fix documentation  
**Scope**: NinjaTrader connection loss, restart, working orders disappeared, system fail-closed/mismatch state

---

## 1. INCIDENT TIMELINE

### Step-by-Step Sequence

| Step | Event | Modules | Values | Decision |
|------|-------|---------|--------|----------|
| 1 | **Connection loss** | `RobotEngine.OnConnectionStatusUpdate` | `wasConnected=true`, `isConnected=false` | Transition to `DISCONNECT_FAIL_CLOSED` |
| 2 | **System state during disconnect** | `ConnectionRecoveryState` | `DISCONNECT_FAIL_CLOSED` | All non-emergency execution blocked; emergency flatten may still run |
| 3 | **NinjaTrader/strategy restart** | `RobotSimStrategy.State.DataLoaded` | New process | IEA registry cleared (in-memory); new `run_id` |
| 4 | **Bootstrap triggered** | `WireNTContextToAdapter` → `SetNTContext` → `BeginBootstrapForInstrument` | `BootstrapReason.STRATEGY_START` | Snapshot callback invoked |
| 5 | **Snapshot captured** | `NinjaTraderSimAdapter.OnBootstrapSnapshotRequested` | Reads `account.Orders` at call time | **Race**: If broker not yet repopulated, `broker_working=0` |
| 6 | **Classification** | `BootstrapClassifier.Classify` | `broker_working=0`, `broker_position_qty=0`, `journal_qty` from journal | `RESUME_WITH_NO_POSITION_NO_ORDERS` or `CLEAN_START` |
| 7 | **Decision** | `BootstrapDecider.Decide` | RESUME path | **RESUME** — no adoption |
| 8 | **Broker orders become visible** | NinjaTrader async | `account.Orders` populated | `broker_working=2` (or N) |
| 9 | **Reconciliation runs** | `MismatchEscalationCoordinator.OnAuditTick` (every 1s) | `AssembleMismatchObservations` | `broker_working=2`, `iea_working=0` |
| 10 | **ORDER_REGISTRY_MISSING** | `MismatchClassification.Classify` | `brokerWorking>0`, `localWorking==0` | Mismatch type: ORDER_REGISTRY_MISSING |
| 11 | **VerifyRegistryIntegrity** (heartbeat, 60s) | `InstrumentExecutionAuthority.EmitHeartbeatIfDue` | Broker order not in registry | `TryAdoptBrokerOrderIfNotInRegistry` fails (pre-fix: unfilled entry stops not in `activeIntentIds`) |
| 12 | **RequestRecovery** | `InstrumentExecutionAuthority.OrderRegistryPhase2` line 147 | `REGISTRY_BROKER_DIVERGENCE` | Reconstruction → `LIVE_ORDER_OWNERSHIP_MISMATCH` → FLATTEN |
| 13 | **Escalation** | `MismatchEscalationCoordinator.ProcessObservation` | DETECTED → PERSISTENT (10s) → FAIL_CLOSED (30s) | Instrument blocked |
| 14 | **Final outcome** | System state | Orders cancelled (flatten) or broker cancelled on disconnect; instrument fail-closed | Mismatch persists; instrument blocked |

### Code Paths

- **Connection loss**: `RobotEngine.cs` lines 4530–4563 — `OnConnectionStatusUpdate` → `DISCONNECT_FAIL_CLOSED_ENTERED`
- **Bootstrap snapshot**: `NinjaTraderSimAdapter.NT.cs` lines 434–494 — `OnBootstrapSnapshotRequested` reads `account.Orders` synchronously
- **Classifier/Decider**: `BootstrapPhase4Types.cs` — `BootstrapClassifier.Classify`, `BootstrapDecider.Decide`
- **Reconciliation**: `RobotEngine.cs` lines 4705–4814 — `AssembleMismatchObservations`; `MismatchEscalationCoordinator.cs` lines 166–226 — `ProcessObservation`
- **VerifyRegistryIntegrity**: `InstrumentExecutionAuthority.OrderRegistryPhase2.cs` lines 86–151 — iterates broker orders, calls `TryAdoptBrokerOrderIfNotInRegistry`, then `RequestRecovery`

---

## 2. WHAT "ORDERS WERE DELETED" ACTUALLY MEANS

### Clarification

Orders were **not** necessarily cancelled at the broker by QTSW2. The outcome can be one or more of:

| Scenario | Description | Code Path |
|----------|-------------|-----------|
| **Not adopted** | Broker orders exist; IEA registry has 0. System does not track them. | Bootstrap saw `broker_working=0` → RESUME; no adoption ran |
| **Not tracked in IEA** | Orders on broker but not in `OrderRegistry` / `OrderMap`. | Adoption failed because `activeIntentIds` (pre-fix) excluded unfilled entry stops |
| **Treated as unknown** | Broker order with QTSW2 tag but `intentId` not in adoption candidates → `RegisterUnownedOrder` | `ScanAndAdoptExistingOrders` / `TryAdoptBrokerOrderIfNotInRegistry` |
| **Flattened by fail-closed logic** | `RequestRecovery` → reconstruction → `LIVE_ORDER_OWNERSHIP_MISMATCH` → `ExecuteRecoveryFlatten` → `NtFlattenInstrumentCommand` | `InstrumentExecutionAuthority.RecoveryPhase3.cs` lines 273–299; `NinjaTraderSimAdapter.NT.cs` line 538 |
| **Broker cancelled on disconnect** | NinjaTrader/broker may cancel working orders when connection is lost (broker-specific) | External to QTSW2 |

### Exact Code Path Leading to Flatten (When System Cancels)

1. `VerifyRegistryIntegrity` finds broker order not in registry (`InstrumentExecutionAuthority.OrderRegistryPhase2.cs` lines 122–126)
2. `TryAdoptBrokerOrderIfNotInRegistry(o)` returns false (pre-fix: `intentId` not in `GetActiveIntentsForBEMonitoring` for unfilled entry stops)
3. `RequestRecovery(ExecutionInstrumentKey, "REGISTRY_BROKER_DIVERGENCE", ...)` (line 147)
4. `InstrumentExecutionAuthority.RecoveryPhase3` — reconstruction sees `UnownedLiveOrderCount > 0`
5. `ChooseRecoveryDecision` → `LIVE_ORDER_OWNERSHIP_MISMATCH` → `RecoveryDecision.FLATTEN`
6. `ExecuteRecoveryFlatten` → `_onRecoveryFlattenRequestedCallback` → `NtFlattenInstrumentCommand`
7. `ExecuteFlattenInstrument` — cancel robot-owned working orders, then flatten position

---

## 3. ROOT CAUSE (PRE-FIX)

### 3.1 Bootstrap Timing

**Mechanism**: Bootstrap snapshot is taken once, synchronously, at `SetNTContext` time. It reads `account.Orders` directly with no wait for broker state.

- **File**: `NinjaTraderSimAdapter.NT.cs` lines 448–453
- **Logic**: `broker_working = account.Orders.Count(o => (o.OrderState == Working || Accepted) && IsSameInstrument(...))`
- **Race**: After reconnect, NinjaTrader repopulates `account.Orders` asynchronously. If bootstrap runs before repopulation, `broker_working=0`.
- **Result**: Classification → `RESUME_WITH_NO_POSITION_NO_ORDERS` or `CLEAN_START` → Decision RESUME → no adoption.

### 3.2 Adoption Gap

**Mechanism**: Adoption paths used `GetActiveIntentsForBEMonitoring()`, which only returns intents with `EntryFilled && EntryFilledQuantityTotal > 0`.

- **File**: `NinjaTraderSimAdapter.cs` (GetActiveIntentsForBEMonitoring)
- **Criteria**: `journalEntry.EntryFilled && journalEntry.EntryFilledQuantityTotal > 0`
- **Effect**: Unfilled entry stops (waiting to fill) have no fill in the journal → excluded from `activeIntentIds`
- **Adoption paths affected**: `ScanAndAdoptExistingOrders`, `TryAdoptBrokerOrderIfNotInRegistry` (both use `activeIntentIds`)
- **Result**: Unfilled entry stops are never adopted; they are treated as UNOWNED → `RequestRecovery` → FLATTEN

### 3.3 Reconciliation Behavior

**Mechanism**: When `ORDER_REGISTRY_MISSING` was detected (broker_working > 0, iea_working == 0), the pre-fix code did **not** attempt adoption before escalating.

- **File**: `RobotEngine.AssembleMismatchObservations` (pre-fix)
- **Logic**: Mismatch added directly to list; no `TryRecoveryAdoption` call
- **Escalation**: `MismatchEscalationCoordinator` — DETECTED → PERSISTENT (10s) → FAIL_CLOSED (30s)
- **No recovery attempt**: No adoption retry when broker orders appeared after bootstrap

---

## 4. FAILURE CLASSIFICATION

| Category | Yes/No | Explanation |
|----------|--------|-------------|
| **Timing issue** | Yes | Bootstrap snapshot taken before broker orders visible; single-pass, no retry |
| **Architectural limitation** | Yes | IEA registry is in-memory; restart clears it. Snapshot-based bootstrap has no convergence loop |
| **Missing recovery path** | Yes | No adoption attempt when reconciliation first sees broker_working > 0 and iea_working == 0 |
| **Incorrect abstraction** | Yes | Adoption used BE-monitoring intents (EntryFilled) instead of adoption-specific candidates (EntrySubmitted && !TradeCompleted) |

---

## 5. FIX IMPLEMENTED

### 5.1 Adoption Candidates

**New method**: `ExecutionJournal.GetAdoptionCandidateIntentIdsForInstrument(executionInstrument, canonicalInstrument)`

- **File**: `ExecutionJournal.cs` lines 1482–1532
- **Criteria**: `EntrySubmitted && !TradeCompleted` (includes unfilled entry stops)
- **Interface**: `IIEAOrderExecutor.GetAdoptionCandidateIntentIds()` — `NinjaTraderSimAdapter`, `ReplayExecutor` implement

### 5.2 Entry Stop Adoption

- **Change**: `ScanAndAdoptExistingOrders` and `TryAdoptBrokerOrderIfNotInRegistry` now use `GetAdoptionCandidateIntentIds` instead of `GetActiveIntentsForBEMonitoring`
- **Effect**: Unfilled entry stops are adoptable when `intentId` is in adoption candidates
- **Removal of dependency**: Adoption no longer depends on BE monitoring intents (EntryFilled)

### 5.3 Reconciliation Recovery

**New logic**: `AssembleMismatchObservations` (RobotEngine.cs lines 4798–4838)

- **When**: `brokerWorking > 0 && effectiveLocalWorking == 0` and IEA available
- **Action**: Call `ieaForRecovery.TryRecoveryAdoption()` before adding mismatch observation
- **Success**: If `localAfter == brokerWorking`, skip adding observation (no escalation)
- **Partial**: If `adopted > 0` but still mismatched, log `RECONCILIATION_RECOVERY_ADOPTION_PARTIAL`
- **Effect**: Prevents `ORDER_REGISTRY_MISSING` escalation when adoption can restore registry

### 5.4 Logging Improvements

| Event | Purpose |
|-------|---------|
| `BOOTSTRAP_RESUME_NO_BROKER_ORDERS` | When RESUME chosen with broker_working=0 (delayed visibility) |
| `BOOTSTRAP_ADOPTION_ATTEMPT` | When RunBootstrapAdoption runs |
| `ADOPTION_SCAN_START` | adoption_candidate_count, broker_working_count |
| `ADOPTION_SUCCESS` | broker_order_id, intent_id, order_class (ENTRY_STOP \| STOP \| TARGET), source |
| `RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT` | Before recovery attempt |
| `RECONCILIATION_RECOVERY_ADOPTION_SUCCESS` | When adoption resolves mismatch |
| `RECONCILIATION_RECOVERY_ADOPTION_PARTIAL` | When still mismatched after adoption |
| `ORDER_REGISTRY_MISSING_FAIL_CLOSED` | When fail-closed still occurs (no adoptable evidence or adoption failed) |

---

## 6. BEFORE vs AFTER BEHAVIOR

### Before (Pre-Fix)

1. Restart → bootstrap → snapshot sees `broker_working=0` (race) → RESUME
2. Broker orders appear later → `broker_working=2`, `iea_working=0`
3. No adoption (unfilled entry stops not in activeIntentIds)
4. Reconciliation → ORDER_REGISTRY_MISSING → DETECTED → PERSISTENT → FAIL_CLOSED
5. VerifyRegistryIntegrity → TryAdoptBrokerOrderIfNotInRegistry fails → RequestRecovery → FLATTEN (orders cancelled)
6. Instrument blocked; orders lost

### After (Post-Fix)

1. Restart → bootstrap → RESUME (same if broker_working=0)
2. Broker orders appear → reconciliation tick
3. **TryRecoveryAdoption** runs before adding mismatch
4. `ScanAndAdoptExistingOrders` uses `GetAdoptionCandidateIntentIds` → unfilled entry stops adoptable
5. Adoption succeeds → `localAfter == brokerWorking` → skip mismatch
6. Registry restored; no fail-closed; orders preserved

---

## 7. SAFETY GUARANTEES NOW

### What Is Guaranteed

- **Valid QTSW2 orders adoptable after restart**: Orders with `EntrySubmitted && !TradeCompleted` in the execution journal are adoption candidates; when broker orders appear (even delayed), reconciliation will attempt adoption before fail-closed.
- **Unfilled entry stops recoverable**: Adoption no longer requires EntryFilled; unfilled entry stops are adoptable.
- **No incorrect fail-closed when recoverable**: When `broker_working > 0` and `iea_working == 0`, `TryRecoveryAdoption` runs first; if adoption succeeds, mismatch is not added.

### What Is NOT Guaranteed

- **Unknown/unverifiable orders**: Orders without QTSW2 tag or with intentId not in adoption candidates → remain unowned → fail-closed (flatten) as before.
- **Broker-level cancellation**: If the broker cancels orders on disconnect, QTSW2 cannot recover them.

---

## 8. REMAINING LIMITATIONS

| Limitation | Description |
|------------|-------------|
| **Bootstrap still snapshot-based** | Single snapshot at bootstrap time; no wait for broker convergence |
| **Reconciliation heartbeat** | Recovery adoption runs on reconciliation tick (1s); first adoption may be delayed up to 1s after broker orders appear |
| **Journal correctness** | Adoption depends on execution journal having correct `EntrySubmitted` / `TradeCompleted` state |
| **IEA in-memory** | Restart clears registry; no persistence across process restarts |

---

## 9. RELATED SYSTEMS IMPACTED

| System | Impact |
|--------|--------|
| **IEA registry** | Restored by adoption; `GetOwnedPlusAdoptedWorkingCount` reflects adopted orders |
| **Slot journals** | `SlotJournalShowsEntryStopsExpected` still used for bootstrap ADOPT vs FLATTEN when `LIVE_ORDERS_PRESENT_NO_POSITION`; adoption candidates are separate (execution journal) |
| **Reconciliation** | `AssembleMismatchObservations` now calls `TryRecoveryAdoption` before adding ORDER_REGISTRY_MISSING |
| **Watchdog** | No direct change; fail-closed still blocks instruments when adoption fails |
| **Multi-instance** | IEA shared per (account, executionInstrumentKey); adoption runs in IEA; all engines see same registry state |

---

## 10. FINAL SUMMARY

### A. Plain English

After a NinjaTrader connection loss and restart, the system could see working orders "disappear" because (1) the bootstrap snapshot was taken before the broker had repopulated its order list, so the system chose RESUME and never tried to adopt; (2) when broker orders did appear, the adoption logic only considered orders whose entries were already filled, so unfilled entry stops were never adopted; and (3) reconciliation treated this as a critical mismatch and either escalated to fail-closed or triggered a recovery flatten that cancelled the orders. The fix adds a separate adoption-candidate list (including unfilled entry stops), runs adoption during reconciliation before escalating, and ensures valid QTSW2 orders are adopted when they appear—even if delayed after restart.

### B. Technical Summary

The incident was caused by a bootstrap timing race (snapshot before broker orders visible), an adoption gap (unfilled entry stops excluded by `GetActiveIntentsForBEMonitoring`), and the absence of a reconciliation recovery path. The fix introduces `GetAdoptionCandidateIntentIdsForInstrument` (EntrySubmitted && !TradeCompleted), switches adoption paths to use it, and adds `TryRecoveryAdoption` in `AssembleMismatchObservations` before adding ORDER_REGISTRY_MISSING. Valid QTSW2 orders are now adoptable after restart; unknown/unverifiable orders still fail-closed. Bootstrap remains snapshot-based; reconciliation recovery runs on the 1s audit tick.
