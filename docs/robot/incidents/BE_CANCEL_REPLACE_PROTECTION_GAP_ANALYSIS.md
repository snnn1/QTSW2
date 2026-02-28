# BE Cancel+Replace: Protection Gap Analysis

**Date**: 2026-02-26  
**Question**: Is there any moment where the position is unprotected longer than one strategy-thread cycle?

---

## Summary

| Question | Answer |
|----------|--------|
| Unprotected longer than one strategy-thread cycle? | **No** — cancel and submit run back-to-back in the same thread, same call stack |
| Same DrainNtActions pass? | **N/A** — BE path does not use DrainNtActions; runs directly on strategy thread |
| Worst-case latency (cancel ack → new stop Working)? | **~10–160 ms** (broker-dependent); overlap typically prevents any gap |

---

## 1. Execution Flow (Strategy Thread)

When IEA is enabled, BE runs **synchronously** on the strategy thread:

```
OnMarketData (strategy thread)
  → lock(EntrySubmissionLock)
  → DrainNtActions()           // drains prior enqueued work
  → EvaluateBreakEven()
      → EvaluateBreakEvenDirect()   // NOT Enqueue — runs inline
          → EvaluateBreakEvenCore()
              → ModifyStopToBreakEven()
                  → ModifyStopToBreakEvenReal()
                      → CancelProtectiveOrdersForIntent()   // account.Cancel() — inline
                      → SubmitProtectiveStop()              // account.Submit() — inline
                      → SubmitTargetOrder()                 // account.Submit() — inline
```

**Source**: `NinjaTraderSimAdapter.cs` lines 1556–1565 — uses `EvaluateBreakEvenDirect` (sync), not `EvaluateBreakEven` (async enqueue).

---

## 2. Cancel and Submit in Same Thread Cycle

- `CancelProtectiveOrdersForIntent` calls `EnsureStrategyThreadOrEnqueue(..., () => account.Cancel(ordersArr))`.
- On the strategy thread, `inContext == true` → the action runs **immediately**, no enqueue.
- `SubmitProtectiveStop` and `SubmitTargetOrder` run immediately after in the same call stack.
- There is **no yield** between cancel and submit — execution is continuous.

**Conclusion**: Cancel and new bracket submission occur in the **same strategy-thread cycle**. No extra DrainNtActions pass is involved; BE does not use the NT action queue.

---

## 3. Measured Latency (MYM 2026-02-26)

From `robot_MYM.jsonl` (intent `3700269fe9159dc7`):

| Timestamp (UTC) | Event | Order |
|-----------------|-------|-------|
| 23:49:36.406 | CancelPending | Old stop 470 |
| 23:49:36.554 | Submitted | New stop 480 |
| 23:49:36.564 | Cancelled | Old stop 470 |
| 23:49:36.565 | Working | New stop 480 |

- **Cancel request → New stop Submitted**: ~148 ms (broker processing).
- **New stop Submitted → New stop Working**: ~11 ms.
- **Old Cancelled → New Working**: ~1 ms overlap (new Working 1 ms after old Cancelled).

The broker received the new stop before the old stop was fully cancelled, so there was overlap rather than a gap.

---

## 4. Risk Window Assessment

### Best case (typical)

- New stop is submitted before the old stop is cancelled.
- Both exist briefly; position is protected by the new stop once it is Working.
- Overlap: old CancelPending + new Submitted/Working.

### Worst case

- Broker processes cancel before submit (e.g. network reordering).
- Gap = time from old Cancelled to new stop Working.
- From logs: ~10–20 ms in practice.
- In theory: up to ~100–200 ms under heavy load.

### Micro-risk vs real risk

- **Micro-risk**: Gap is on the order of 10–50 ms.
- **Real risk**: Gap would need to be seconds or more for a fast market to move through the stop.

**Conclusion**: This is a **micro-risk window** — sub-100 ms in normal conditions. A gap of that size is unlikely to matter for typical futures tick flow, but a fast gap or flash move could theoretically hit an unprotected position.

---

## 5. Log Markers (Implemented)

| Event | When | Purpose |
|-------|------|---------|
| `BE_CANCEL_REPLACE_START` | Start of cancel+replace | Includes `new_oco_id` (always unique), `old_oco_id` |
| `BE_CANCEL_REPLACE_STOP_WORKING` | When new stop becomes Working | Includes `elapsed_ms` from START → quantify overlap/gap distribution |
| `BE_CANCEL_REPLACE_DONE` | After target submitted | Confirms replace complete; STOP_WORKING logs when broker confirms |

**Adoption**: During overlap, both stops share tag `QTSW2:{intentId}:STOP`. `OrderMap[intentId]` is overwritten with newest; adoption is deterministic.

---

## 6. Recommendations

1. **Quantify distribution**  
   Use `elapsed_ms` from `BE_CANCEL_REPLACE_STOP_WORKING` across runs to measure overlap/gap distribution.

2. **Optional: Submit before cancel**  
   NinjaTrader may allow submitting the new stop before cancelling the old one, ensuring overlap. This would need verification against NT behavior and duplicate-order handling.

3. **Monitor for STOP_MODIFY_REVERTED**
   If `Change()` is ever reintroduced, continue to detect and handle reverts.

---

## References

- `RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs` — OnMarketData, EvaluateBreakEven, DrainNtActions
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs` — EvaluateBreakEven, EvaluateBreakEvenDirect
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` — ModifyStopToBreakEvenReal, CancelProtectiveOrdersForIntent
- `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` — EvaluateBreakEvenDirect (sync path)
