# M2K Unmanaged Exposure — Forensic Trace (Ownership Reconstruction Only)

**Date**: 2026-03-17  
**Scope**: M2K broker qty=2 vs journal qty=0 — root cause classification only (no fixes implemented)

---

## 1. First Mismatch Events

| Event | Timestamp (UTC) | Data |
|-------|-----------------|------|
| **RECONCILIATION_QTY_MISMATCH** | 2026-03-17T20:35:02 (from prior summary) | broker_qty=2, journal_qty=0, drift_class="broker_ahead" |
| **POSITION_DRIFT_DETECTED** | Same | broker_ahead |
| **EXPOSURE_INTEGRITY_VIOLATION** | Same | IEA reasons: BOOTSTRAP:STRATEGY_START; UNOWNED_LIVE_ORDER_DETECTED (4×) |

**Earlier same-day events (robot_M2K.jsonl):**

| Event | Timestamp | Data |
|-------|-----------|------|
| **RECONCILIATION_MISMATCH_DETECTED** | 16:00:03.787 | broker_qty=0, local_qty=0, broker_working=2, local_working=0, mismatch_type=ORDER_REGISTRY_MISSING |
| **MANUAL_OR_EXTERNAL_ORDER_DETECTED** | 16:00:09.005 | broker_order_id=408453624085, intent_id=f1cb54fdc75255a0, ownership_status=UNOWNED, source_context=BROKER_REGISTRY_MISSING_UNOWNED |
| **MANUAL_OR_EXTERNAL_ORDER_DETECTED** | 16:00:10.031 | broker_order_id=408453624088, intent_id=1df35893bdd8c859, ownership_status=UNOWNED, source_context=BROKER_REGISTRY_MISSING_UNOWNED |
| **RECONCILIATION_MISMATCH_FAIL_CLOSED** | 16:00:34–36 | broker_qty=0, local_qty=0, broker_working=2, local_working=0 |
| **PROTECTIVE_MISSING_STOP** | 17:22:04.616 | broker_position_qty=2, broker_direction=Long, intent_id=null |
| **PROTECTIVE_RECOVERY_SUBMITTED** | 17:22:04 | submitted=False, failure_reason=NO_JOURNAL_ENTRY |
| **PROTECTIVE_EMERGENCY_FLATTEN_TRIGGERED** | 17:22:08 | success=True |

---

## 2. Execution Journals for M2K/RTY

**Path**: `data/execution_journals/` (confirmed via `GetJournalPath` / `GetOpenJournalEntriesByInstrument`)

**2026-03-17 RTY2 entries:**

| File | Intent | Direction | EntryFilled | EntryFilledQuantityTotal | BrokerOrderId |
|------|--------|-----------|------------|--------------------------|---------------|
| `2026-03-17_RTY2_1df35893bdd8c859.json` | Short | Short | false | 0 | b8d1415782bb467dae28422e56d7bcac |
| `2026-03-17_RTY2_f1cb54fdc75255a0.json` | Long | Long | false | 0 | 3239fa838fbd4798aad4662415cb81a2 |

- Instrument in journal: `Instrument="M2K"` — no naming/mapping issue.
- Journal lookup uses `executionInstrument` and `canonicalInstrument`; M2K/RTY mapping is correct in `ReconciliationRunner` and `GetOpenJournalQuantitySumForInstrument`.

**Conclusion**: Journals exist for both intents, but neither has any filled quantity. No journal recovery path applies.

---

## 3. Earlier Events (ORDER_REJECTED, RECOVERY, FLATTEN, ADOPTION)

**2026-03-17 16:00:02 sequence:**

1. **INTENT_POLICY_REGISTERED** — intents f1cb54fdc75255a0 (Long), 1df35893bdd8c859 (Short)
2. **ORDER_SUBMIT_SUCCESS** — Long entry stop (broker_order_id=3239fa838fbd4798aad4662415cb81a2), Short entry stop (b8d1415782bb467dae28422e56d7bcac)
3. **REGISTRY_BROKER_DIVERGENCE** (8×) — broker has live orders registry does not; adopting:
   - 408453624052, 408453624055, 408453624073, 408453624076, 408453624079, 408453624082, 408453624085, 408453624088
4. **REGISTRY_BROKER_DIVERGENCE_ADOPTED** (8×) — all adopted
5. **MANUAL_OR_EXTERNAL_ORDER_DETECTED** (2×) — 408453624085 → intent f1cb54fdc75255a0, 408453624088 → intent 1df35893bdd8c859, both UNOWNED, BROKER_REGISTRY_MISSING_UNOWNED
6. **ORDER_REGISTRY_METRICS** — unowned_orders_detected=8, registry_integrity_failures=8

**EXECUTION_FILLED**: No `EXECUTION_FILLED` or `EXECUTION_PARTIAL_FILL` for M2K on 2026-03-17 (grep over `robot_M2K.jsonl` and `robot_ENGINE.jsonl`).

---

## 4. Broker/Order Tag Evidence

**QTSW2-tagged orders:**

- Robot-submitted: 3239fa838fbd4798aad4662415cb81a2 (Long), b8d1415782bb467dae28422e56d7bcac (Short) — UUID-style NT IDs.
- Orphan/adopted: 408453624052 … 408453624088 — numeric NT order IDs.

**Ownership reconstruction:**

- `TryAdoptBrokerOrderIfNotInRegistry` adopts QTSW2-tagged orders when `intentId` is in `activeIntentIds`.
- If `intentId` is not in `activeIntentIds`, it calls `RegisterUnownedOrder(..., "BROKER_REGISTRY_MISSING_UNOWNED")` and returns false.
- `MANUAL_OR_EXTERNAL_ORDER_DETECTED` is emitted from the **order update handler** when:
  - `OrderMap.TryGetValue(intentId, out orderInfo)` fails, and
  - `_iea.TryResolveByBrokerOrderId(order.OrderId, out _)` fails.

**Interpretation:**

- All 8 orphans were adopted (REGISTRY_BROKER_DIVERGENCE_ADOPTED).
- MANUAL_OR_EXTERNAL_ORDER_DETECTED for 408453624085 and 408453624088 indicates an order update (e.g. fill) arrived at an IEA instance whose `OrderMap` did not contain those orders.
- Likely cause: multiple IEA instances (e.g. multiple M2K charts). One instance adopted; another received the fill callback and had an empty OrderMap for that order.

---

## 5. Protective_Missing_Stop Classification

- **protective_missing_stop**: broker_position_qty=2, intent_id=null, stop_qty=0.
- **PROTECTIVE_RECOVERY_SUBMITTED**: submitted=False, failure_reason=NO_JOURNAL_ENTRY.
- **PROTECTIVE_EMERGENCY_FLATTEN_TRIGGERED**: success=True.

**Conclusion**: This is the expected behavior for unowned broker exposure. There is no journal entry with filled quantity, so the system correctly refuses to add protective stops and escalates to emergency flatten. This is not a bug in protective logic.

---

## 6. Root Cause Classification

| Factor | Finding |
|--------|---------|
| **Position origin** | QTSW2-tagged order (robot or prior-session orphan) filled; fill was not journaled. |
| **Journal gap** | No `EXECUTION_FILLED` for M2K on 2026-03-17; journals have `EntryFilledQuantityTotal=0`. |
| **Ownership path** | Orphan orders adopted by one IEA instance; fill callback reached another instance without OrderMap entry → MANUAL_OR_EXTERNAL_ORDER_DETECTED, no `RecordEntryFill`. |
| **protective_missing_stop** | Expected for unowned broker exposure; fail-closed behavior is correct. |

**Root cause**: **Prior recovery/restart side effect** — QTSW2-tagged orphan orders from a prior session were adopted by one IEA instance. When one of them (or the robot’s own order) filled, the fill was delivered to an instance that did not have the order in OrderMap, so the fill was never journaled. Broker position=2, journal=0.

---

## 7. Evidence Summary

| Evidence Type | Location | Finding |
|--------------|----------|---------|
| First RECONCILIATION_MISMATCH | robot_M2K.jsonl ~line 1467 | 16:00:03, broker_qty=0, broker_working=2, local_working=0 |
| First MANUAL_OR_EXTERNAL_ORDER_DETECTED | robot_M2K.jsonl ~lines 117, 120 | 408453624085→f1cb54fdc75255a0, 408453624088→1df35893bdd8c859 |
| Journal state | data/execution_journals/ | Both RTY2 intents: EntryFilledQuantityTotal=0 |
| EXECUTION_FILLED | logs/robot/robot_M2K.jsonl | None for M2K on 2026-03-17 |
| QTSW2 tag usage | RobotEngine.cs:5333–5334 | Tag/OcoGroup prefix "QTSW2:" used for ownership |
| Fill→journal path | NinjaTraderSimAdapter.NT.cs ~2835 | RecordEntryFill only when order in OrderMap |

---

## 8. Recommended Action

| Option | Applicability |
|--------|---------------|
| **(a) Manual flatten / manual cleanup** | **Yes** — If broker still has M2K exposure, flatten manually. Emergency flatten was triggered but position may persist across instances. |
| **(b) Journal recovery** | **No** — No evidence of a journal entry that should exist; journals correctly reflect zero filled quantity. |
| **(c) Code fix** | **Consider** — Ensure fill callbacks for adopted orders are journaled even when the fill is delivered to an IEA instance that did not perform adoption (e.g. shared journal + registry, or routing fills to the adopting instance). |
| **(d) Ignore as expected fail-closed** | **Partial** — protective_missing_stop and emergency flatten are correct. The underlying issue (fill not journaled due to multi-instance OrderMap) is a code/design gap, not something to ignore. |

**Immediate action**: **(a) Manual flatten** if broker still shows M2K position.  
**Follow-up**: **(c) Code fix** — investigate fill routing and journal updates for adopted orders across multiple IEA instances.
