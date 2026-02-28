# Break-Even on Real Trades: OCO Change() Reverted — Root Cause & Fix

**Date**: 2026-02-26  
**Status**: Fixed (cancel+replace path verified on MYM)

---

## Summary

Break-even (BE) was not working on real trades (e.g. MES): the BE trigger fired, we requested a stop move to BE, but the broker **reverted** the change. The stop stayed at the original price. The fix was to stop using `account.Change()` on OCO-linked orders and instead use **cancel+replace**.

---

## What We Were Doing Wrong

### 1. Using `account.Change()` on OCO Stop Orders

Real trades use **OCO** (One-Cancels-Other) for protectives: stop and target are linked. When price hit the BE trigger, we called:

```csharp
account.Change(new[] { existingStop });  // Modify stop price to BE
```

We expected the broker to update the stop order in place.

### 2. Broker Rejected / Reverted the Change

**Observed behavior (MES logs):**

- Requested stop price: **6896.75** (BE)
- Actual stop price after "change": **6894.25** (original)
- Order cycled: `ChangePending` → `ChangeSubmitted` → `Working` at **6894.25**

The broker accepted the `Change()` call but then **reverted** the order to the original price. No error was thrown; the order simply did not move.

### 3. Why OCO Makes Change() Unreliable

OCO orders are linked. Modifying one leg (stop) while the other (target) stays the same can conflict with broker logic:

- Some brokers do not support in-place modification of OCO legs
- NinjaTrader / broker may silently revert to preserve OCO integrity
- The revert is not reported as a rejection; it appears as a normal order update

---

## The Fix: Cancel + Replace

Instead of `Change()`, we now:

1. **Cancel** both OCO legs (stop and target)
2. **Submit** a new stop at the BE price
3. **Submit** a new target at the original target price
4. **Link** them with a new OCO group (`QTSW2:OCO_BE:{intentId}:{guid}`)

This avoids modifying an existing OCO order. We create a fresh OCO pair at the correct prices.

---

## Evidence It Works: MYM Test Inject (2026-02-26)

**Run 23:49 (intent `3700269fe9159dc7`):**

| Step | Event |
|------|-------|
| 1 | Short entry filled @ 49241 |
| 2 | Initial stop placed @ 49254 |
| 3 | BE trigger hit → cancel+replace executed |
| 4 | Old stop 408453621470 → `CancelPending` → `Cancelled` |
| 5 | New stop 408453621480 submitted @ **49242** (BE) |
| 6 | Trade closed @ 49243 when BE stop hit |

The stop moved from 49254 → 49242 (BE) via cancel+replace. No revert.

---

## Implementation Location

- **File**: `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`
- **Method**: `ModifyStopToBreakEvenReal`
- **Comment**: `// NT OCO FIX: account.Change() on OCO stop orders is rejected/reverted by broker.`
- **Events**: `BE_REPLACE_START`, `BE_REPLACE_SUCCESS`, `BE_REPLACE_STOP_FAIL`, `BE_REPLACE_TARGET_FAIL`, `BE_REPLACE_SKIP`

---

## Lessons Learned

1. **OCO + Change() = unreliable** — Do not assume in-place modification works for OCO orders.
2. **Silent revert** — The broker can revert without an explicit rejection; monitor `OrderUpdate` for requested vs actual price.
3. **Cancel+replace is robust** — Creating new orders at the desired prices avoids broker-specific OCO modification quirks.
4. **Test inject validates the path** — MYM test inject uses the same `ModifyStopToBreakEvenReal` path as real trades, so success there confirms the fix.

---

## Related Incidents

- **NQ1/NQ2 fill, no protectives** (`2026-02-17_NQ1_FILL_NO_PROTECTIVES_INVESTIGATION.md`): Different root cause (order not in map, wrong instance flattened).
- **NG1/NG2 same price** (`NG1_NG2_SAME_PRICE_INCIDENT_ANALYSIS.md`): Range overlap, not BE-related.
