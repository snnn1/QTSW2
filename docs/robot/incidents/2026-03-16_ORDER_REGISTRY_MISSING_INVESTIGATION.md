# ORDER_REGISTRY_MISSING Investigation (2026-03-16)

## Summary

**Exact cause:** Wrong data source for `local_working` in reconciliation. The code compares broker working orders to **open journal entries** (filled positions), but ORDER_REGISTRY_MISSING is meant to detect broker working orders not tracked in the **IEA order registry**. Working entry orders (pre-fill) are never in the journal—they are only in the IEA registry. This produces false ORDER_REGISTRY_MISSING when entry stops are live but unfilled.

---

## Step 1 — Orders Identified

| broker_order_id | instrument | intent_id (from OCO) | order_role | timestamp |
|-----------------|------------|----------------------|------------|-----------|
| `6bf241a7845e4569991d3af9774f3d2b` | MYM | `7a2f3a2586e343a2b8e329b2ba4ae2c3` (long) | ENTRY_STOP | 12:30:06.387 UTC |
| `b982523b2ffe4b75adfd117a2868e563` | MYM | `7a2f3a2586e343a2b8e329b2ba4ae2c3` (short) | ENTRY_STOP | 12:30:07.094 UTC |

OCO group: `QTSW2:OCO_ENTRY:2026-03-16:YM1:07:30:7a2f3a2586e343a2b8e329b2ba4ae2c3`

---

## Step 2 — Lifecycle Trace

| Event | Timestamp | broker_order_id | Result |
|-------|-----------|-----------------|--------|
| ORDER_SUBMIT_SUCCESS | 12:30:06.387 | 6bf241a7... | Long entry stop submitted |
| ORDER_SUBMIT_SUCCESS | 12:30:07.094 | b982523b... | Short entry stop submitted |
| RECONCILIATION_MISMATCH_DETECTED | 12:30:07.650 | — | broker_working=2, local_working=0 |
| RECONCILIATION_MISMATCH_PERSISTENT | 12:30:17.693 | — | persistence_ms=10042 |
| MISMATCH_FAIL_CLOSED | 12:30:37.788 | — | Instrument blocked |

- **ORDER_SUBMIT_SUCCESS:** Both orders were logged and submitted.
- **Registry:** `RegisterOrder` is called in `NinjaTraderSimAdapter.NT.cs` line 1503 for entry orders.
- **Journal:** `GetOpenJournalEntriesByInstrument()` returns entries where `EntryFilled && !TradeCompleted`. Entry orders are unfilled, so they are not in the journal.

---

## Step 3 — Registry Loss Scenarios

| Scenario | Result |
|----------|--------|
| **A. Registry never recorded** | No — `RegisterOrder` is called on ORDER_SUBMIT_SUCCESS |
| **B. Registry recorded then removed incorrectly** | No — orders were just submitted; no cancel/fill/reject |
| **C. Registry lost during restart** | No — no restart around 12:30 UTC |
| **D. Order created outside expected path** | No — normal entry submission path |

**Actual cause:** `local_working` is derived from the **journal**, not the **IEA registry**. The journal only has filled positions; working entry orders are only in the IEA registry. So `local_working` is 0 even though the registry has the orders.

---

## Step 4 — Reconnect / Recovery

- **Disconnect:** Last DISCONNECT_FAIL_CLOSED at 02:23 UTC (~10 hours before mismatch).
- **RunRecovery:** Not triggered around 12:30.
- **CancelRobotOwnedWorkingOrders:** Not run around 12:30.
- **Conclusion:** No reconnect/recovery interaction with this incident.

---

## Step 5 — Mapping Logic

- broker_order_id ↔ intent_id: Correct (OCO group encodes intent).
- Instrument key: MYM used consistently.
- Stream: YM1 (07:30 slot).
- Orders are correctly attributed; mapping is not the cause.

---

## Step 6 — Output

### Exact Cause

**Wrong data source for `local_working`.** Reconciliation uses `_executionJournal.GetOpenJournalEntriesByInstrument()` for `local_working`, which counts **filled positions** (EntryFilled && !TradeCompleted). For ORDER_REGISTRY_MISSING, `local_working` should be the count of **working orders in the IEA registry** (owned + adopted). Working entry orders are never in the journal until they fill.

### Code Locations

| File | Location | Issue |
|------|----------|-------|
| `RobotEngine.cs` | `AssembleMismatchObservations()` ~4735–4754 | `local_working` from `GetOpenJournalEntriesByInstrument()` |
| `ExecutionJournal.cs` | `GetOpenJournalEntriesByInstrument()` ~1460 | Filters `entry.EntryFilled && !entry.TradeCompleted` — excludes unfilled entry orders |
| `MismatchClassification.cs` | `Classify()` line 25 | `brokerWorkingOrderCount > 0 && localWorkingOrderCount == 0` → ORDER_REGISTRY_MISSING |

### Can This Happen Again?

**Yes.** Any time entry stops are live (pre-fill), reconciliation will see broker_working=2 and local_working=0 and raise ORDER_REGISTRY_MISSING.

### Can It Affect Other Instruments?

**Yes.** Any instrument with working entry orders before fill will hit this.

---

## Fix Recommendation

**Use IEA registry working count for `local_working` when comparing to `broker_working`.**

1. Add a method on IEA (or `InstrumentExecutionAuthorityRegistry`) to return the working order count for an instrument, e.g. `GetOwnedPlusAdoptedWorkingCountForInstrument(account, execKey)`.
2. In `RobotEngine.AssembleMismatchObservations()`, for each instrument:
   - Resolve IEA via `InstrumentExecutionAuthorityRegistry.TryGet(account, execKey, out iea)`.
   - If IEA exists and `UseInstrumentExecutionAuthority` is true, set `local_working = iea.GetOwnedPlusAdoptedWorkingCount()` (or equivalent).
   - Otherwise keep current behavior (journal count) for backward compatibility.
3. Ensure `GetOwnedPlusAdoptedWorkingCount` includes both WORKING and SUBMITTED states if OnOrderUpdate can lag, so we do not get a brief false positive during the SUBMITTED→WORKING transition.

### Alternative (narrower)

Exclude ORDER_REGISTRY_MISSING when `broker_qty == 0 && local_qty == 0` (no position, only working orders). That would avoid false positives for pre-entry, but would also hide real registry loss when there are working orders and no position. The IEA-based fix is preferable.

---

## Fix Applied (2026-03-17)

- **OrderRegistry**: Added `GetOwnedPlusAdoptedWorkingCount()` (SUBMITTED, WORKING, PART_FILLED)
- **InstrumentExecutionAuthority**: Added `GetOwnedPlusAdoptedWorkingCount()` wrapper
- **RobotEngine.AssembleMismatchObservations**: `local_working` now from IEA registry when `UseInstrumentExecutionAuthority`; fail closed when IEA unavailable
- **Diagnostic**: `RECONCILIATION_ORDER_SOURCE_BREAKDOWN` when `broker_working != iea_working`
- **Tests**: OrderRegistryTests 16–18 (pre-entry, transition, empty registry)

---

## References

- `docs/robot/incidents/2026-03-11_MYM_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md` — journal cache fix
- `docs/robot/incidents/2026-03-16_YM1_ONLY_TRADE_INVESTIGATION.md` — YM1 timeline
- `InstrumentExecutionAuthority.RecoveryPhase3.cs` — `GetRegistrySnapshotForRecovery()` (OwnedCount, AdoptedCount)
- `OrderRegistry.cs` — `GetMetrics()` (OwnedOrdersActive, AdoptedOrdersActive)
