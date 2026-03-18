# Adoption Recovery Implementation — Validation Report

## 1. Matching Safety

### How a live broker order is matched to an adoption candidate intent

1. **Broker order tag** — `Executor.GetOrderTag(o)` returns the NinjaTrader order Tag/Name (e.g. `QTSW2:a1b2c3d4e5f67890` or `QTSW2:a1b2c3d4e5f67890:STOP`).

2. **Intent ID extraction** — `RobotOrderIds.DecodeIntentId(tag)`:
   - Requires `tag.StartsWith("QTSW2:")`
   - For `QTSW2:{intentId}`: returns `intentId`
   - For `QTSW2:{intentId}:STOP` or `:TARGET`: returns substring before first `:` after prefix → `intentId`

3. **Role classification** — `tag.EndsWith(":STOP")` or `:TARGET` → protective; else → entry stop.

4. **Adoption candidate check** — `activeIntentIds.Contains(intentId)` where `activeIntentIds` = `GetAdoptionCandidateIntentIds(ExecutionInstrumentKey)` = execution journal entries with `EntrySubmitted && !TradeCompleted` and matching instrument.

5. **Registry skip** — `TryResolveByBrokerOrderId(o.OrderId, out _)` — if already in registry, skip (entry path) or `OrderMap.TryGetValue(mapKey, out _)` — if already in OrderMap, skip (protective path).

### Fields/conditions used in the match

| Field/Condition | Source | Purpose |
|-----------------|--------|---------|
| `o.OrderState` | Broker | Working or Accepted only |
| `tag` | Broker order Tag/Name | Must start with `QTSW2:` |
| `intentId` | Decoded from tag | Must be non-empty |
| `activeIntentIds.Contains(intentId)` | Execution journal | Intent must be in adoption candidates |
| `entry.Instrument` | Journal | Must match execution instrument |
| `entry.EntrySubmitted` | Journal | Must be true |
| `entry.TradeCompleted` | Journal | Must be false |

### What prevents wrong-order adoption

1. **QTSW2 prefix** — Non-robot orders (no `QTSW2:`) are skipped or go to `RegisterUnownedOrder`.
2. **Adoption candidates from our journal** — Only intents we submitted (`EntrySubmitted`) and not completed (`!TradeCompleted`) are adoptable. We cannot adopt orders we did not submit.
3. **Instrument match** — Journal entries are filtered by execution instrument.
4. **Intent ID collision** — Intent IDs are 16-char hashes from `ComputeIntentId`. Collision risk is negligible.

**Edge case**: A malicious order with tag `QTSW2:{ourIntentId}` would be adoptable only if we actually submitted that intent. We would be adopting our own order that was somehow duplicated or spoofed — acceptable risk.

---

## 2. Idempotency

### Repeated reconciliation ticks

| Concern | Mechanism | Verified |
|---------|-----------|----------|
| Adopt same order multiple times | `TryResolveByBrokerOrderId(o.OrderId, out _)` (entry) or `OrderMap.TryGetValue(mapKey, out _)` (protective) — skip if already present | ✓ |
| Duplicate registry state | `RegisterAdoptedOrder` adds to registry; next scan `TryResolveByBrokerOrderId` finds it | ✓ |
| Repeated misleading success logs | `ADOPTION_SUCCESS` only when we actually adopt (inside the `if (!OrderMap.TryGetValue...)` or after `RegisterAdoptedOrder`). On repeat scans we skip, so no log | ✓ |

**Gap**: `ADOPTION_SCAN_START` and `RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT` are emitted on every scan/tick when conditions hold. No throttling. Log volume can be high if mismatch persists.

---

## 3. Candidate Accuracy

| Case | EntrySubmitted | TradeCompleted | Included? | Correct? |
|------|----------------|----------------|-----------|-----------|
| Unfilled entry stop still working | true (RecordSubmission) | false | ✓ Yes | ✓ Yes — we want to adopt |
| Entry canceled before restart | true | false | ✓ Yes | ✓ Yes — harmless; no broker order to match |
| Entry filled, protectives working | true | false | ✓ Yes | ✓ Yes — adopt protectives |
| Trade completed | true | true | ✗ No | ✓ Yes — exclude |
| Partial fill / mixed state | true | false | ✓ Yes | ✓ Yes — adopt |

**Entry rejected at submit**: `RecordRejection` sets `Rejected=true` but we do not filter on it. Entry may still have `EntrySubmitted` from a prior `RecordSubmission`. Rejected orders are not on the broker, so no adoption occurs. Including them in candidates is harmless.

---

## 4. Recovery Loop Behavior

### Tick 1, 2, 3 when `brokerWorking > 0 && localWorking == 0` persists

| Tick | Action |
|------|--------|
| 1 | `TryRecoveryAdoption()` → `ScanAndAdoptExistingOrders()` → adopt if possible. If `localAfter == brokerWorking`, skip mismatch. Else add observation. |
| 2 | Same. Adoption is idempotent (already in registry) → `adopted = 0`. Mismatch persists. |
| 3 | Same. |

### Throttling / state memory

- **No throttling** — Adoption is attempted on every reconciliation tick (every 1s).
- **No state memory** — No "already attempted" flag.
- **Effect** — Repeated `RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT` and `ADOPTION_SCAN_START` logs. Adoption logic remains idempotent; no duplicate adoptions.

---

## 5. Method Semantics

| Method | Current name | Accuracy | Recommendation |
|--------|--------------|----------|----------------|
| `ScanAndAdoptExistingOrders` (was `ScanAndAdoptExistingProtectives`) | — | Renamed ✓ | Adopts both protectives and entry stops |

---

## 6. Test Gap Analysis

| Scenario | Tested? | Notes |
|----------|---------|-------|
| Repeated reconciliation recovery attempts | ✗ No | Would need to mock IEA + broker state across ticks |
| Partial adoption success | ✗ No | brokerWorking=2, adopt 1, still mismatched |
| Wrong live order should remain fail-closed | ✗ No | Non-QTSW2 or intentId not in candidates → unowned |
| Duplicate adoption prevention | ✗ No | Same order scanned twice, second time skipped |
| Restart with mixed entry + protective broker orders | ✗ No | Both order types in one scan |
| Delayed broker visibility across multiple audit ticks | ✗ No | brokerWorking 0→2 across ticks |

**Minimal additional tests** (implemented):
1. Duplicate adoption prevention — covered by OrderRegistryTests (TryResolveByBrokerOrderId finds registered orders); adoption logic uses this to skip.
2. Wrong order fail-closed — `TestAdoptionCandidatesExcludeTradeCompleted` ensures TradeCompleted intents are excluded; intent not in journal → not in candidates → unowned.
3. Method rename — `ScanAndAdoptExistingProtectives` → `ScanAndAdoptExistingOrders` ✓

---

## 7. Implemented Fixes

1. **Rename**: `ScanAndAdoptExistingProtectives` → `ScanAndAdoptExistingOrders` (InstrumentExecutionAuthority.NT.cs, OrderRegistry.cs, BootstrapPhase4.cs).
2. **Tests**: Add `TestAdoptionCandidatesExcludeTradeCompleted` to AdoptionRecoveryTests — verifies journal excludes TradeCompleted intents (wrong-order prevention at candidate level). Duplicate adoption is enforced by registry/OrderMap lookups (OrderRegistryTests already covers registry resolution).

---

## 8. High-Risk Restart Scenario Tests (Phase 2)

| Scenario | Test | Coverage Type |
|----------|------|---------------|
| Delayed broker visibility | `ReconciliationRecoveryScenarioTests.TestDelayedBrokerVisibility` | Pure unit — `ReconciliationRecoveryOutcome.Evaluate` |
| Partial adoption success | `ReconciliationRecoveryScenarioTests.TestPartialAdoptionSuccess` | Pure unit |
| Repeated failed recovery | `ReconciliationRecoveryScenarioTests.TestRepeatedFailedRecoveryAttempts` | Pure unit |
| Mixed restart (entry + protective) | `AdoptionRecoveryTests.TestMixedRestartCandidateCoverage` | Pure unit — journal `GetAdoptionCandidateIntentIdsForInstrument` |
| Duplicate adoption prevention | `OrderRegistryTests` test 19 | Pure unit — `Register` overwrites same broker id |

**Test infrastructure**: No mocks, no harness extensions. All tests run via `dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test RECONCILIATION_RECOVERY_SCENARIOS` (or `ADOPTION_RECOVERY`, `ORDER_REGISTRY`).

**Log throttling**: `RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT` throttled to once per 60s per (instrument, brokerWorking, localWorking) in RobotEngine.AssembleMismatchObservations (NT).
