# Bootstrap / Adoption / Reconciliation — Full Architectural Investigation

**Read-only investigation. No code changes. No speculation — verified from source.**

**UPDATE (fix implemented)**: See implementation summary at end of document.

---

## 1. BOOTSTRAP FLOW

### 1.1 Entry Points

**Where bootstrap begins:**
- **File**: `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs`
- **Method**: `SetNTContext(object account, object instrument, string? engineExecutionInstrument)`
- **Line**: 558 — `_iea.BeginBootstrapForInstrument(executionInstrumentKey, BootstrapReason.STRATEGY_START, DateTimeOffset.UtcNow);`

**Full call chain (NinjaTrader strategy start → bootstrap):**

1. **NinjaTrader** `OnStateChange()` → `State.DataLoaded`
2. **RobotSimStrategy** `State.DataLoaded` block (lines 253–698):
   - Creates `RobotEngine`, calls `_engine.Start()`
   - Queues BarsRequest for execution instruments
   - Calls `WireNTContextToAdapter()` (line 625)
3. **WireNTContextToAdapter()** (lines 1299–1348):
   - Gets `NinjaTraderSimAdapter` from engine
   - Calls `simAdapter.SetNTContext(Account, Instrument, engineExecutionInstrument)` (line 1316)
4. **NinjaTraderSimAdapter.SetNTContext()** (lines 502–560):
   - Resolves IEA via `InstrumentExecutionAuthorityRegistry.GetOrCreate(...)`
   - Calls `HydrateIntentsFromOpenJournals()` (line 553)
   - Calls `_iea.BeginBootstrapForInstrument(executionInstrumentKey, BootstrapReason.STRATEGY_START, DateTimeOffset.UtcNow)` (line 558)

**Important**: `WireNTContextToAdapter` runs in **State.DataLoaded**, not State.Realtime. Bootstrap starts during DataLoaded, before Realtime.

---

### 1.2 Snapshot Construction

**Where snapshot is built:**
- **File**: `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`
- **Method**: `OnBootstrapSnapshotRequested(string instrument, BootstrapReason reason, DateTimeOffset utcNow)` (lines 434–494)

**Source of each snapshot field:**

| Field | Source | Method / Logic |
|-------|--------|----------------|
| **broker_working** | `account.Orders` | `account.Orders.Count(o => (o.OrderState == OrderState.Working \|\| OrderState.Accepted) && IsSameInstrument(...))` (lines 450–453). Direct read of NinjaTrader `Account.Orders`. |
| **broker_position_qty** | `GetAccountPositionForInstrument` | `((IIEAOrderExecutor)this).GetAccountPositionForInstrument(instrument)` → `account.Positions` (lines 445–446). |
| **journal_working** | Execution journal | `_executionJournal.GetOpenJournalQuantitySumForInstrument(instrument, execVariant)` (line 462). Sum of open journal entry quantities. |
| **iea_working** (registry) | IEA registry | `_iea.GetRegistrySnapshotForRecovery()` → `registry.UnownedLiveCount` (lines 463–474). Registry snapshot from `InstrumentExecutionAuthority.GetRegistrySnapshotForRecovery()`. |
| **SlotJournalShowsEntryStopsExpected** | Callback | `_hasSlotJournalWithEntryStopsForInstrumentCallback?.Invoke(instrument) ?? false` (line 465). Wired to `RobotEngine.HasSlotJournalWithEntryStopsForInstrument()`. |

**Broker dependency:**
- `broker_working` and `broker_position_qty` come from `account.Orders` and `account.Positions`.
- No explicit wait for broker state. Snapshot uses whatever NinjaTrader has at call time.
- No async broker polling or event-driven refresh before snapshot.

---

### 1.3 Decision Logic

**Classifier**: `modules/robot/contracts/BootstrapPhase4Types.cs` — `BootstrapClassifier.Classify(snapshot)`

**Decider**: `BootstrapPhase4Types.cs` — `BootstrapDecider.Decide(classification, snapshot)`

**Decision tree (simplified):**

```
BootstrapClassifier.Classify(snapshot):
  unownedLive > 0                    → MANUAL_INTERVENTION_PRESENT
  brokerQty=0, brokerWorking=0, journalQty=0 → CLEAN_START
  brokerQty=0, brokerWorking=0      → RESUME_WITH_NO_POSITION_NO_ORDERS
  brokerWorking>0, journalQty>0, brokerQty!=0 + reg conditions → ADOPTION_REQUIRED or RESUME_WITH_NO_POSITION_NO_ORDERS
  brokerQty != journalQty           → JOURNAL_RUNTIME_DIVERGENCE
  brokerQty!=0, brokerWorking=0     → POSITION_PRESENT_NO_OWNED_ORDERS
  brokerWorking>0, brokerQty=0      → LIVE_ORDERS_PRESENT_NO_POSITION
  else                              → UNKNOWN_REQUIRES_SUPERVISOR

BootstrapDecider.Decide(classification, snapshot):
  LIVE_ORDERS_PRESENT_NO_POSITION && SlotJournalShowsEntryStopsExpected → ADOPT  (lines 146–147)
  CLEAN_START                           → RESUME
  RESUME_WITH_NO_POSITION_NO_ORDERS     → RESUME
  ADOPTION_REQUIRED                     → ADOPT
  POSITION_PRESENT_NO_OWNED_ORDERS      → FLATTEN_THEN_RECONSTRUCT
  LIVE_ORDERS_PRESENT_NO_POSITION       → FLATTEN_THEN_RECONSTRUCT  (default, overridden by SlotJournal above)
  JOURNAL_RUNTIME_DIVERGENCE            → FLATTEN_THEN_RECONSTRUCT
  REGISTRY_BROKER_DIVERGENCE_ON_START   → FLATTEN_THEN_RECONSTRUCT
  MANUAL_INTERVENTION_PRESENT           → HALT
  UNKNOWN_REQUIRES_SUPERVISOR           → HALT
```

**SlotJournalShowsEntryStopsExpected** is used only in the decider for `LIVE_ORDERS_PRESENT_NO_POSITION` → ADOPT vs FLATTEN.

---

### 1.4 Timing Characteristics

- **Single-pass**: One snapshot per `BeginBootstrapForInstrument` call. `MarkBootstrapSnapshotStale` can trigger a re-run.
- **Blocking**: Snapshot callback runs synchronously; no await.
- **Not tick-driven**: Triggered once at `SetNTContext`.
- **Not async**: Snapshot is built and processed in the same call stack.
- **Broker state**: Uses current `account.Orders` / `account.Positions` with no wait or retry.

---

## 2. BROKER STATE AVAILABILITY

### 2.1 Where broker working orders come from

**Method**: `account.Orders` (NinjaTrader `Account.Orders`)

**Bootstrap snapshot**: `NinjaTraderSimAdapter.NT.cs` lines 448–453:
```csharp
if (account?.Orders != null)
{
    brokerWorkingCount = account.Orders.Count(o =>
        (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted) &&
        ExecutionInstrumentResolver.IsSameInstrument(...));
}
```

**Reconciliation snapshot**: `NinjaTraderSimAdapter.NT.cs` `GetAccountSnapshotReal()` (lines 4930–4946):
```csharp
foreach (var order in account.Orders)
{
    if (order.OrderState == OrderState.Working || order.OrderState == OrderState.Accepted)
        workingOrders.Add(...);
}
```

**Caching**: No explicit cache. Each call reads `account.Orders` directly.

**Event-driven**: NinjaTrader updates `account.Orders` via `OnOrderUpdate` / `OnExecutionUpdate`. No polling loop.

---

### 2.2 Timing of broker updates

- NinjaTrader populates `account.Orders` when:
  - Connection is established
  - Orders are submitted
  - Order state changes (Working, Filled, Cancelled, etc.)
- No explicit “account snapshot” or “orders loaded” event in the code.
- After a restart, NinjaTrader may repopulate `account.Orders` asynchronously as it reconnects and syncs.

---

### 2.3 Race condition

**Yes.** Bootstrap can run before broker orders are visible:

1. `SetNTContext` is called in `State.DataLoaded`.
2. `BeginBootstrapForInstrument` runs immediately.
3. `OnBootstrapSnapshotRequested` reads `account.Orders` at that moment.
4. If NinjaTrader has not yet repopulated `account.Orders` after reconnect, `broker_working` = 0.
5. Classification becomes CLEAN_START or RESUME_WITH_NO_POSITION_NO_ORDERS → RESUME.
6. No ADOPT path is taken.
7. Later, `account.Orders` is populated → broker has 2 working orders.
8. IEA registry still has 0 → ORDER_REGISTRY_MISSING.

---

## 3. ADOPTION LOGIC

### 3.1 Adoption entry points

| Entry Point | File | Method | When |
|-------------|------|--------|------|
| **Bootstrap ADOPT** | `InstrumentExecutionAuthority.NT.cs` | `RunBootstrapAdoption()` → `ScanAndAdoptExistingProtectives()` | When `ProcessBootstrapResult` returns ADOPT (line 493 in NinjaTraderSimAdapter.NT.cs) |
| **First execution update post-bootstrap** | `InstrumentExecutionAuthority.NT.cs` | `EnqueueExecutionUpdate` → `ScanAndAdoptExistingProtectives()` | When `!_hasScannedForAdoption && !IsInBootstrap && (NORMAL \|\| RESOLVED)` (lines 510–514) |
| **REGISTRY_BROKER_DIVERGENCE** | `InstrumentExecutionAuthority.OrderRegistryPhase2.cs` | `VerifyRegistryIntegrity()` → `TryAdoptBrokerOrderIfNotInRegistry()` | From `EmitHeartbeatIfDue()` → `VerifyRegistryIntegrity()` (InstrumentExecutionAuthority.cs lines 394–395). Heartbeat runs every HEARTBEAT_INTERVAL_SECONDS. |

---

### 3.2 What each path handles

| Path | Entry stops | Stop loss | Targets | Reconstructs intent? |
|------|-------------|-----------|---------|---------------------|
| **ScanAndAdoptExistingProtectives** | Yes* | Yes | Yes | No — only registers if `intentId` in `activeIntentIds` |
| **TryAdoptBrokerOrderIfNotInRegistry** | Yes* | Yes | Yes | No — same `activeIntentIds` logic |

\* **Critical**: Both use `GetActiveIntentsForBEMonitoring()` for `activeIntentIds`. That method only returns intents with `journalEntry.EntryFilled && journalEntry.EntryFilledQuantityTotal > 0` (NinjaTraderSimAdapter.cs lines 2411–2412). Unfilled entry stops are excluded. So entry stops are **not** adopted by either path when they have no fill yet.

---

### 3.3 When adoption runs

| Path | When |
|------|------|
| **Bootstrap ADOPT** | During bootstrap, only when decision is ADOPT |
| **First execution update** | On first `EnqueueExecutionUpdate` after bootstrap completes (NORMAL or RESOLVED) |
| **VerifyRegistryIntegrity** | Every IEA heartbeat (HEARTBEAT_INTERVAL_SECONDS) |

---

### 3.4 Missing coverage

- **Entry stops (unfilled)**: Not adopted.
  - `GetActiveIntentsForBEMonitoring` filters by `EntryFilled`.
  - Entry stops waiting to fill have no execution journal entry.
  - `activeIntentIds` is empty for them.
  - `ScanAndAdoptExistingProtectives` and `TryAdoptBrokerOrderIfNotInRegistry` treat them as UNOWNED.
- **Protectives**: Adopted when `intentId` is in `activeIntentIds` (i.e. entry already filled).
- **Post-bootstrap adoption**: No dedicated path that re-runs adoption when broker orders appear after bootstrap. `VerifyRegistryIntegrity` runs on heartbeat, but only adopts when `intentId` is in `activeIntentIds` (filled intents).

---

## 4. RECONCILIATION FLOW

### 4.1 Reconciliation trigger

- **MismatchEscalationCoordinator**: `Timer` with `MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_MS` (1000 ms). `OnAuditTick` calls `getMismatchObservations` → `AssembleMismatchObservations`.
- **AssembleMismatchObservations**: `RobotEngine.cs` lines 4705–4814. Uses `_executionAdapter.GetAccountSnapshot(utcNow)` for broker state.

---

### 4.2 ORDER_REGISTRY_MISSING logic

**Condition** (`MismatchClassification.cs` line 25–26):
```csharp
if (brokerWorkingOrderCount > 0 && localWorkingOrderCount == 0)
    return MismatchType.ORDER_REGISTRY_MISSING;
```

**localWorkingOrderCount** comes from `iea.GetOwnedPlusAdoptedWorkingCount()` (RobotEngine.cs line 4766).

**brokerWorkingOrderCount** comes from `GetAccountSnapshot()` → `WorkingOrders` count (from `account.Orders`).

---

### 4.3 Recovery behavior

**MismatchEscalationCoordinator.ProcessObservation** (lines 166–226):
- DETECTED → PERSISTENT_MISMATCH (after 10s)
- PERSISTENT_MISMATCH → FAIL_CLOSED (after 30s or max retries)
- No adoption attempt; no retry of adoption.
- Blocking: `IsInstrumentBlockedByMismatch` returns true when instrument is blocked.

---

## 5. IEA REGISTRY LIFECYCLE

### 5.1 Initialization

- **File**: `NinjaTraderSimAdapter.cs` lines 516–517
- **When**: `SetNTContext` with IEA enabled
- **Logic**: `InstrumentExecutionAuthorityRegistry.GetOrCreate(accountName, executionInstrumentKey, () => new InstrumentExecutionAuthority(...))`

---

### 5.2 Persistence

- **Not persisted.** IEA is in-memory only (`ConcurrentDictionary` in `InstrumentExecutionAuthorityRegistry`).
- Process restart clears the registry.

---

### 5.3 Rebuild mechanism

- On restart, bootstrap runs.
- `GetRegistrySnapshotForRecovery()` returns a fresh registry.
- Registry is repopulated by:
  - `ScanAndAdoptExistingProtectives` (when ADOPT or first execution update)
  - `TryAdoptBrokerOrderIfNotInRegistry` (during `VerifyRegistryIntegrity`)
  - Normal order flow (submits, fills)
- **Gap**: If bootstrap sees `broker_working=0` and chooses RESUME, adoption is never triggered. Later `VerifyRegistryIntegrity` can adopt only when `intentId` is in `activeIntentIds` (filled intents). Unfilled entry stops do not qualify.

---

## 6. SLOT JOURNAL USAGE

### 6.1 Where slot journals are read

- **File**: `RobotEngine.cs`
- **Method**: `HasSlotJournalWithEntryStopsForInstrument(string instrument)` (lines 5456–5483)
- **When**: Called from `OnBootstrapSnapshotRequested` via `_hasSlotJournalWithEntryStopsForInstrumentCallback`
- **Logic**: For each stream with `CanonicalInstrument` matching the instrument, loads slot journal via `_journals.TryLoad(tradingDateStr, stream.Stream)`. Returns true if `LastState == "RANGE_LOCKED"` and `StopBracketsSubmittedAtLock == true`.

---

### 6.2 Role in adoption

- Slot journal **does not** trigger adoption.
- It only affects `BootstrapDecider`: `LIVE_ORDERS_PRESENT_NO_POSITION` + `SlotJournalShowsEntryStopsExpected` → ADOPT instead of FLATTEN.
- When ADOPT is chosen, `RunBootstrapAdoption` → `ScanAndAdoptExistingProtectives` runs. Adoption still depends on `activeIntentIds` from `GetActiveIntentsForBEMonitoring`, which excludes unfilled entry stops.

---

## 7. LOGGING

### 7.1 BOOTSTRAP_SNAPSHOT_CAPTURED

**Exists**: Yes.  
**File**: `NinjaTraderSimAdapter.NT.cs` lines 483–491  
**When**: After building `BootstrapSnapshot`, before `ProcessBootstrapResult`  
**Logger**: `_log.Write(RobotEvents.EngineBase(...))`  
**Payload**: `instrument`, `broker_position_qty`, `broker_working_count`, `journal_qty`, `unowned_live_count`, `slot_journal_shows_entry_stops_expected`, `iea_instance_id`

---

### 7.2 REGISTRY_BROKER_DIVERGENCE_ADOPTED

**File**: `InstrumentExecutionAuthority.OrderRegistryPhase2.cs` lines 136–143  
**When**: `VerifyRegistryIntegrity` finds a broker order not in registry and `TryAdoptBrokerOrderIfNotInRegistry(o)` returns true  
**Condition**: `broker_has_registry_missing` path in `VerifyRegistryIntegrity` (lines 121–149)

---

## 8. MULTI-INSTANCE BEHAVIOR

### 8.1 Per-engine behavior

- Each engine: own bootstrap, own reconciliation, own adapter.
- `InstrumentExecutionAuthorityRegistry` is static per process.  
- Each (account, executionInstrument) has one IEA.

### 8.2 Shared state

- IEA is shared per (account, executionInstrumentKey) via `InstrumentExecutionAuthorityRegistry`.  
- Multiple engines for the same instrument can share the same IEA instance.
- `SetNTContext` is called per strategy instance; each wires its adapter to the same IEA when the instrument matches.

---

## 9. TIMELINE RECONSTRUCTION (Restart → MNQ has 2 working broker orders)

### Step-by-step

1. **Engine start**  
   NinjaTrader starts; strategy enters `State.DataLoaded`.

2. **Bootstrap runs**  
   `WireNTContextToAdapter` → `SetNTContext` → `BeginBootstrapForInstrument` → `OnBootstrapSnapshotRequested`.

3. **Snapshot values (race)**  
   - If broker orders are not yet visible: `broker_working=0`, `broker_position_qty=0`, `journal_qty=0` (or from journal).  
   - Classification: CLEAN_START or RESUME_WITH_NO_POSITION_NO_ORDERS.  
   - Decision: RESUME.  
   - No adoption.

4. **Broker orders become visible**  
   NinjaTrader populates `account.Orders`; `broker_working=2`.

5. **Reconciliation runs**  
   `MismatchEscalationCoordinator.OnAuditTick` (every 1s) → `AssembleMismatchObservations` → `GetAccountSnapshot` → `broker_working=2`, `iea.GetOwnedPlusAdoptedWorkingCount()=0`.

6. **ORDER_REGISTRY_MISSING**  
   `MismatchClassification.Classify(brokerWorking=2, localWorking=0)` → ORDER_REGISTRY_MISSING.

7. **Escalation**  
   DETECTED → PERSISTENT_MISMATCH (10s) → FAIL_CLOSED (30s). Instrument blocked.

8. **VerifyRegistryIntegrity**  
   Runs on IEA heartbeat. `TryAdoptBrokerOrderIfNotInRegistry` can adopt only when `intentId` is in `activeIntentIds`. For unfilled entry stops, `activeIntentIds` is empty → no adoption.

---

## 10. FINAL OUTPUT

### A. Confirmed root cause

1. **Bootstrap timing**: Bootstrap may run before NinjaTrader has repopulated `account.Orders` after reconnect. Snapshot sees `broker_working=0` → RESUME, no adoption.
2. **Entry-stop adoption gap**: `ScanAndAdoptExistingProtectives` and `TryAdoptBrokerOrderIfNotInRegistry` use `GetActiveIntentsForBEMonitoring`, which only returns intents with filled entries. Unfilled entry stops are never in `activeIntentIds` and are never adopted.
3. **No post-bootstrap adoption retry**: No scheduled or event-driven retry of adoption when broker orders appear after bootstrap. `VerifyRegistryIntegrity` runs on heartbeat but cannot adopt unfilled entry stops.

### B. Exact missing mechanisms

1. **No wait for broker state before bootstrap** — snapshot is taken immediately.
2. **Entry stops not adopted** — `activeIntentIds` excludes unfilled intents.
3. **No post-bootstrap adoption retry** — no path that re-runs adoption when broker orders appear later.
4. **IntentMap not populated from slot journal** — `HydrateIntentsFromOpenJournals` uses execution journal only; RANGE_LOCKED entry-stop intents are not hydrated.

### C. Bootstrap type

- **Snapshot-based**: Single snapshot at bootstrap time.
- **Not convergence-based**: No retry or wait until broker and registry converge.

---

## File Reference Summary

| Component | File |
|-----------|------|
| Bootstrap entry | `NinjaTraderSimAdapter.cs` SetNTContext, `InstrumentExecutionAuthority.BootstrapPhase4.cs` BeginBootstrapForInstrument |
| Snapshot callback | `NinjaTraderSimAdapter.NT.cs` OnBootstrapSnapshotRequested |
| Classifier/Decider | `modules/robot/contracts/BootstrapPhase4Types.cs` |
| ScanAndAdoptExistingProtectives | `InstrumentExecutionAuthority.NT.cs` |
| TryAdoptBrokerOrderIfNotInRegistry | `InstrumentExecutionAuthority.NT.cs` |
| VerifyRegistryIntegrity | `InstrumentExecutionAuthority.OrderRegistryPhase2.cs` |
| GetActiveIntentsForBEMonitoring | `NinjaTraderSimAdapter.cs` |
| HasSlotJournalWithEntryStopsForInstrument | `RobotEngine.cs` |
| MismatchClassification | `MismatchClassification.cs` |
| AssembleMismatchObservations | `RobotEngine.cs` |
| GetAccountSnapshotReal | `NinjaTraderSimAdapter.NT.cs` |
| IEA Registry | `InstrumentExecutionAuthorityRegistry.cs` |

---

## Implementation Summary (Fix Applied)

### Changes Made

1. **Adoption candidate discovery (separate from BE monitoring)**
   - `ExecutionJournal.GetAdoptionCandidateIntentIdsForInstrument()` — returns intent IDs where `EntrySubmitted && !TradeCompleted` (includes unfilled entry stops)
   - `IIEAOrderExecutor.GetAdoptionCandidateIntentIds()` — new interface method
   - `NinjaTraderSimAdapter.GetAdoptionCandidateIntentIds()` — delegates to execution journal
   - `ReplayExecutor.GetAdoptionCandidateIntentIds()` — returns empty (replay path)

2. **Adoption logic now uses adoption candidates**
   - `ScanAndAdoptExistingProtectives` — uses `GetAdoptionCandidateIntentIds` instead of `GetActiveIntentsForBEMonitoring`
   - `TryAdoptBrokerOrderIfNotInRegistry` — same change
   - Both paths now adopt unfilled entry stops when intent ID is in adoption candidates

3. **Reconciliation recovery before fail-closed**
   - `AssembleMismatchObservations` — when `brokerWorking > 0 && localWorking == 0`, calls `iea.TryRecoveryAdoption()` before adding to mismatch list
   - If adoption succeeds (localAfter == brokerWorking), skips adding observation
   - `InstrumentExecutionAuthority.TryRecoveryAdoption()` — runs `ScanAndAdoptExistingProtectives`, returns adopted count

4. **Logging**
   - `BOOTSTRAP_ADOPTION_ATTEMPT` — when RunBootstrapAdoption runs
   - `ADOPTION_SCAN_START` — adoption_candidate_count, broker_working_count
   - `ADOPTION_SUCCESS` — broker_order_id, intent_id, order_class (ENTRY_STOP | STOP | TARGET), source
   - `RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT` — before recovery
   - `RECONCILIATION_RECOVERY_ADOPTION_SUCCESS` — when adoption resolves mismatch
   - `RECONCILIATION_RECOVERY_ADOPTION_PARTIAL` — when still mismatched after adoption
   - `ORDER_REGISTRY_MISSING_FAIL_CLOSED` — reason when fail-closed still occurs

5. **Tests**
   - `AdoptionRecoveryTests` — GetAdoptionCandidateIntentIdsForInstrument includes unfilled, excludes completed, no adoptable evidence fail-closed, bootstrap ADOPT with slot journal
   - Run: `dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test ADOPTION_RECOVERY`
