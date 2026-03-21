# Investigation: S1 Orders Cancelled (2026-03-19)

**Date**: 2026-03-19  
**Source**: Robot logs (`logs/robot/robot_MES.jsonl`, execution journals, frontend feed)  
**Summaries hub:** [docs/robot/summaries/README.md](../summaries/README.md) · [Master roll-up Mar 17–20](../summaries/MASTER_RECENT_ISSUES_AND_FIXES_2026-03-17_through_2026-03-20.md)

---

## 1. TIMELINE (from robot logs)

| Time (Chicago) | Time (UTC) | Event | run_id | Source |
|----------------|------------|-------|--------|--------|
| **9:00:02** | 14:00:02 | ES1 orders submitted (Long dc8066d4892d6bff, Short 9f7d0020c22f50f8) | c11de464... | robot_MES.jsonl |
| **9:00:03** | 14:00:03 | ORDER_SUBMIT_SUCCESS – broker_order_id a881b2ec..., aeed4bb2... | c11de464... | robot_MES.jsonl |
| **9:10:03** | 14:10:03 | RECONCILIATION_MISMATCH_DETECTED – ORDER_REGISTRY_MISSING, broker_working=2 local_working=0 | (multiple) | robot_MES.jsonl |
| **9:10:05** | 14:10:05 | MANUAL_OR_EXTERNAL_ORDER_DETECTED – source_context UNOWNED_ENTRY_RESTART | 65244a0b... | robot_MES.jsonl |
| **9:10:05** | 14:10:05 | UNOWNED_LIVE_ORDER_DETECTED – "Restart scan: QTSW2 entry with no matching active intent" | 65244a0b... | robot_MES.jsonl |
| **9:10:05** | 14:10:05 | FLATTEN_REQUESTED → FLATTEN_SUBMITTED (reason: UNOWNED_LIVE_ORDER_DETECTED) | 65244a0b... | robot_MES.jsonl |
| **9:10:16** | 14:10:16 | FLATTEN_VERIFY_PASS | 65244a0b... | robot_MES.jsonl |

---

## 2. ROOT CAUSE

### Strategy restart with registry cleared

1. **9:00 AM** – Run `c11de464...` submitted ES1 entry orders (Long + Short) for slot 09:00.
2. **~9:10 AM** – A new run `65244a0b...` started (different `run_id`).
3. **Restart scan** – The new run’s IEA scanned the broker and found 2 QTSW2 entry orders (broker_order_id 408453624401, 408453624406) with intent_ids dc8066d4892d6bff and 9f7d0020c22f50f8.
4. **No adoption** – Those intent_ids were not in the new run’s “active intent” / adoption-candidate set.
5. **UNOWNED_ENTRY_RESTART** – The robot treated the orders as unowned and called `RequestRecovery` → flatten.
6. **Orders cancelled** – The flatten path cancelled the working orders.

### Why the run_id changed

- The 7:30–8:08 AM connection loss likely led to a strategy restart or NinjaTrader reload.
- When the strategy restarted, it got a new `run_id` and a fresh in-memory IEA registry.
- The broker still had the ES1 orders from the previous run.
- The new run saw broker_working=2 and local_working=0 → ORDER_REGISTRY_MISSING → UNOWNED → flatten.

---

## 3. CODE PATH

**File**: `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` (lines 677–692)

```
ScanAndAdoptExistingOrders (restart scan)
  → For each QTSW2 entry order on broker:
      if (activeIntentIds.Contains(intentId))
          → ADOPTION_SUCCESS (adopt into registry)
      else
          → RegisterUnownedOrder(..., "UNOWNED_ENTRY_RESTART")
          → RequestRecovery(instrument, "UNOWNED_LIVE_ORDER_DETECTED", ...)
          → RECOVERY_PHASE3 → NtFlattenInstrumentCommand → cancel orders
```

`activeIntentIds` comes from `GetAdoptionCandidateIntentIds()`, which uses journal entries with `EntrySubmitted && !TradeCompleted`.

---

## 4. WHY ADOPTION DID NOT RUN

Execution journal `2026-03-19_ES1_dc8066d4892d6bff.json` exists and has:
- `Instrument: "MES"` (matches IEA ExecutionInstrumentKey)
- `EntrySubmitted: true`, `TradeCompleted: false` (valid adoption candidate)
- `BrokerOrderId: "a881b2ec..."` (journal format; broker log shows 408453624401 – likely NT display vs stored ID)

So `GetAdoptionCandidateIntentIds("MES")` should return `dc8066d4892d6bff`. Adoption should have succeeded.

Possible reasons it did not:

1. **Journal path / working directory** – New run may have used a different journal directory (e.g. different cwd or config).
2. **Scan before journal available** – First scan may run before journal files are visible (e.g. NT startup, path not yet resolved).
3. **ADOPTION_SCAN_START not logged** – No visibility into `adoption_candidate_count` or `broker_working_count` at scan time; add logging to diagnose.

---

## 5. IMPACT

- **ES1** – Both entry orders cancelled at 9:10 AM.
- **Other S1 streams** – If the same restart affected all strategy instances, GC1, NQ1, YM1, RTY1, CL1, NG1 could have been treated the same way (UNOWNED_ENTRY_RESTART → flatten → cancel).

---

## 6. RECOMMENDATIONS

1. **Adoption timing** – Ensure restart adoption runs after journals are loaded, or retry adoption when ORDER_REGISTRY_MISSING is first seen.
2. **Reconciliation recovery** – Use `TryRecoveryAdoption` before escalating ORDER_REGISTRY_MISSING (see incident report 2026-03-17).
3. **Restart adoption delay** – Add a short delay or retry loop before treating orders as UNOWNED on restart, to allow journal load to complete.
4. **Logging** – Log when `GetAdoptionCandidateIntentIds` is empty or when adoption is skipped, to distinguish “no candidates” from “candidates not yet loaded”.

**Update (follow-up investigation):** See [2026-03-19 ES1 / ES2 adoption & submission](2026-03-19_ES1_ES2_ADOPTION_AND_SUBMISSION_INVESTIGATION.md) for root-cause analysis (burst execution updates vs adoption deferral scan cap, NT full instrument names) and implemented code fixes.
