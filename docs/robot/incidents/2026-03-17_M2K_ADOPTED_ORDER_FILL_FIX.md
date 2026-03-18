# M2K Adopted-Order Fill Journaling Fix

**Date**: 2026-03-17  
**Scope**: Multi-instance adopted-order fill journaling gap — ensure fills for adopted orders are journaled regardless of which IEA instance receives the callback.

---

## 1. Root Cause (Pre-Fix)

| Failure Condition | Description |
|-------------------|-------------|
| **Adopted in instance A** | Orphan order adopted via `TryAdoptBrokerOrderIfNotInRegistry` when `intentId` in `activeIntentIds` → `RegisterAdoptedOrder` + OrderMap |
| **Unowned in instance A** | When `intentId` NOT in `activeIntentIds` → `RegisterUnownedOrder` with `IsEntryOrder = false` |
| **Fill delivered** | Execution callback arrives; `TryResolveForExecutionUpdate` finds registry entry |
| **Journaling skipped** | `orderInfo.IsEntryOrder == false` for UNOWNED → `isEntryFill = false` → entry fill path skipped |

**Secondary gap**: When fill arrives before local registry has the order (timing race), `orderInfo` is null. Deferred retry may eventually resolve, but SharedAdoptedOrderRegistry provides a fallback for cross-instance resolution.

---

## 2. Files Changed

| File | Change |
|------|--------|
| `RobotCore_For_NinjaTrader/Execution/SharedAdoptedOrderRegistry.cs` | **New** — static registry keyed by broker_order_id for cross-instance resolution |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.OrderRegistryPhase2.cs` | `RegisterUnownedOrder` adds `isEntryOrder` param; registers to SharedAdoptedOrderRegistry when intentId present |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.OrderRegistry.cs` | `RegisterAdoptedOrder` registers to SharedAdoptedOrderRegistry |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` | Pass `isEntryOrder: true` when RegisterUnownedOrder for entry orders |
| `RobotCore_For_NinjaTrader/Execution/ExecutionJournal.cs` | Add `TryGetAdoptionCandidateEntry(intentId, executionInstrument)` |
| `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` | SharedAdoptedOrderRegistry resolution path before deferred retry; hydrate intent from journal if needed |
| `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs` | Hydrate intents from adoption candidates in `HydrateIntentsFromOpenJournals` |
| `RobotCore_For_NinjaTrader/RobotEventTypes.cs` | Add ADOPTED_ORDER_FILL_RESOLVED_CROSS_INSTANCE, ADOPTED_ORDER_FILL_JOURNALED, ADOPTED_ORDER_FILL_UNRESOLVED |
| `modules/robot/core/Execution/SharedAdoptedOrderRegistry.cs` | **New** — same implementation for harness/replay |
| `modules/robot/core/Tests/SharedAdoptedOrderRegistryTests.cs` | **New** — unit tests |
| `modules/robot/harness/Program.cs` | Add --test SHARED_ADOPTED_ORDER_REGISTRY |

---

## 3. Ownership Model After Fix

| Storage | Scope | Key | Purpose |
|---------|-------|-----|---------|
| **OrderMap** | Per-IEA (shared per account+instrument) | intentId | Primary fill resolution; OrderInfo for journaling |
| **OrderRegistry** | Per-IEA | broker_order_id, alias (intentId, intentId:STOP, intentId:TARGET) | TryResolveForExecutionUpdate; lifecycle |
| **SharedAdoptedOrderRegistry** | Global static | broker_order_id | Cross-instance fallback when OrderMap/Registry miss |

**Flow**:
1. Adoption: `RegisterAdoptedOrder` or `RegisterUnownedOrder` → add to OrderRegistry + OrderMap (when adopted) + SharedAdoptedOrderRegistry
2. Fill: Try `TryResolveForExecutionUpdate` → if found, use regEntry.OrderInfo (now has `IsEntryOrder = true` for unowned entry orders)
3. Fallback: If orderInfo null, try SharedAdoptedOrderRegistry → load journal via TryGetAdoptionCandidateEntry → journal and hydrate intent

---

## 4. Logging Added

| Event | When | Data |
|-------|------|------|
| **ADOPTED_ORDER_FILL_RESOLVED_CROSS_INSTANCE** | Fill resolved via SharedAdoptedOrderRegistry | broker_order_id, intent_id, receiving_instance_id |
| **ADOPTED_ORDER_FILL_JOURNALED** | Fill journaled via cross-instance path | broker_order_id, intent_id, fill_price, fill_quantity, trading_date, stream |
| **ADOPTED_ORDER_FILL_UNRESOLVED** | SharedAdoptedOrderRegistry had record but could not journal | broker_order_id, intent_id, reason (context_incomplete \| no_journal_entry) |

---

## 5. Tests

Run: `dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test SHARED_ADOPTED_ORDER_REGISTRY`

| Test | Verifies |
|------|----------|
| Register + resolve | Adopted order resolved via shared lookup |
| Unknown order | TryResolve returns false (fail-closed) |
| Idempotent registration | Repeated registration overwrites |
| Repeated resolve | Same result (caller dedupes; no double-journal) |
| Case-insensitive | broker-003 resolves "Broker-003" |
| Empty broker id | Register no-op, resolve false |

---

## 6. Remaining Limitations

1. **Intent hydration**: If journal has no adoption-candidate entry for intentId (e.g. wrong instrument, corrupted file), cross-instance path cannot journal. ADOPTED_ORDER_FILL_UNRESOLVED is emitted.

2. **Contract multiplier**: Fixed 2026-03-12. Cross-instance path now derives multiplier from `order.Instrument.MasterInstrument.PointValue` (fallback 100). See validation section below.

3. **Execution dedup**: Cross-instance path does not add to IEA's _processedExecutionIds. Dedup runs at start of HandleExecutionUpdateReal before our path; same execution should not be delivered twice. If NT delivers duplicate callbacks, dedup catches the first; second would be skipped.

4. **SharedAdoptedOrderRegistry retention**: 60 minutes. Orders older than that are evicted on resolve. Long-lived positions may need longer retention or persistence.

5. **modules/robot vs RobotCore_For_NinjaTrader**: SharedAdoptedOrderRegistry exists in both. RobotCore_For_NinjaTrader has the full fix (RegisterUnownedOrder isEntryOrder, fill handler path). modules/robot has SharedAdoptedOrderRegistry for harness tests; adoption logic may differ in NT-specific code.

---

## 7. Correctness Validation (2026-03-12)

### Contract multiplier

| Item | Detail |
|------|--------|
| **Location** | `NinjaTraderSimAdapter.NT.cs` ~2689, cross-instance path when building `IntentContext` for `RecordEntryFill` |
| **Fix** | Replaced hardcoded `100` with `order.Instrument.MasterInstrument.PointValue` (fallback 100) |
| **Correct for** | ES (50), NQ (20), RTY/M2K (50), YM (5), CL (1000), GC (100), etc. — all instruments via NT PointValue |
| **Previously wrong for** | MES (5), MNQ (2), M2K (5), MCL (100), MGC (10), MYM (5) — micros and non-100 instruments |

### Shared registry retention

| Item | Detail |
|------|--------|
| **Value** | 60 minutes (`SharedAdoptedOrderRegistry.RetentionMinutes`) |
| **Rationale** | Balance between memory and typical session length; adopted orders are short-lived |
| **After expiry** | `TryResolve` returns false; fill falls through to untracked path → EXECUTION_UNOWNED → deferred retry → fail-closed flatten |
| **Assessment** | Acceptable fail-closed. Fill >60 min after adoption is unusual; fail-closed is correct |

### Cross-instance idempotency

| Scenario | Outcome |
|----------|---------|
| **Fill callback arrives twice** | `TryMarkAndCheckDuplicate` (line 2438) runs before fill handling; second callback skipped; `EXECUTION_DUPLICATE_DETECTED` logged |
| **Order update vs fill race** | Fill first → SharedAdoptedOrderRegistry journals once. Order first → normal registry path journals once. No double journal |
| **Two instances resolve same fill** | ExecutionUpdateRouter routes by (account, instrument) → one endpoint; only one instance receives the execution |

### Logging completeness

| Event | Logged? |
|------|---------|
| Shared registry resolved | `ADOPTED_ORDER_FILL_RESOLVED_CROSS_INSTANCE` |
| Journal candidate lookup failed | `ADOPTED_ORDER_FILL_UNRESOLVED` (reason: `no_journal_entry` or `context_incomplete`) |
| Multiplier/instrument context | Not explicitly logged; `ADOPTED_ORDER_FILL_JOURNALED` includes fill_price, fill_quantity |
| Final fail-closed | `EXECUTION_UNOWNED` → deferred retry → flatten path |

### Test coverage

| Covered | Not covered (NT-dependent) |
|--------|----------------------------|
| SharedAdoptedOrderRegistry register/resolve, unknown order, idempotent registration, repeated resolve, case-insensitivity, empty broker id | Full flow (adopted in A, fill in B), order update vs fill race, double fill callback, end-to-end journaling with real NT execution |
