# NQ1/NQ2 Fill — No Protective Orders, BE Not Detecting

**Date**: 2026-02-17  
**Stream**: NQ2 (user said NQ1; 2026-02-17 timetable has NQ2)  
**Instrument**: MNQ

---

## Summary

NQ filled but:
1. **No limit/stop orders** (protective bracket never placed)
2. **Break-even detection not running**
3. **RECONCILIATION_QTY_MISMATCH**: account_qty=1, journal_qty=0
4. **FLATTEN_INTENT_SUCCESS** at 18:40:00 — position was flattened

---

## Root Cause

**EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL** at 18:40:00 (run_id cdced2d852094bafa54034ad4a18426f = MNQ strategy):

- Fill callback arrived for an order **not in `_orderMap`**
- Same pattern as NG1/NG2 incident
- System took fail-closed path: **flattened** the position
- Journal was **never updated** (EntryFilled stayed false)
- Protective orders were **never submitted** (we never reached `HandleEntryFill`)

---

## Why Order Not in _orderMap?

Most likely: **multiple strategy instances for the same instrument (MNQ)**.

From `RobotSimStrategy.cs`:
```csharp
// When multiple strategy instances run on the same account, all instances receive OrderUpdate
// callbacks for ALL orders. Each instance should only process orders for its own instrument.
if (e.Order?.Instrument != Instrument) { return; }  // Filter by instrument
```

- Filter is by **instrument** only
- If there are **two charts with MNQ**, both instances receive the fill
- **Instance A** (submitted the order): has it in `_orderMap` → processes correctly
- **Instance B** (did not submit): does **not** have it in `_orderMap` → hits UNKNOWN_ORDER_CRITICAL → **flattens**

So the instance that did **not** submit the order can receive the fill, fail the lookup, and flatten the position before the submitting instance can place protective orders.

---

## BE_GATE_BLOCKED (INSTRUMENT_MISMATCH)

Logs show:
```
execution_instrument: MES (or MYM, M2K, MGC, MNG, MCL)
account_position_instrument: MNQ
```

- Account has an **MNQ** position
- Each strategy (MES, MYM, M2K, etc.) runs its own BE check
- When MES strategy runs BE, it sees account position in **MNQ**, not MES
- BE gate blocks with **INSTRUMENT_MISMATCH** to avoid acting on the wrong instrument

So BE is blocked on **non-MNQ** strategies because the position is in MNQ. That is expected.

For the **MNQ** strategy: journal shows `EntryFilled=false`, so the engine does not treat the stream as filled. BE logic depends on filled intents, so BE would not run for that stream even if the position existed.

---

## Timeline

| Time (UTC) | Event |
|------------|-------|
| ~18:39:xx | NQ2 entry order fills (long 24815.75 or short 24449) |
| 18:40:00.392 | EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL |
| 18:40:00.391 | FLATTEN_INTENT_SUCCESS (intent c19c8fb7f4145d8e) |
| 18:40:00.393 | ENGINE_STOP |
| 18:40:00.397 | UNKNOWN_ORDER_FILL_FLATTENED |
| 18:40:02+ | BE_GATE_BLOCKED (INSTRUMENT_MISMATCH) on other strategies |
| 18:40:04+ | RECONCILIATION_QTY_MISMATCH (MNQ account=1, journal=0) |

---

## Recommended Fix

**When order not in `_orderMap`, do not flatten.** Instead, **skip** and let the instance that submitted the order handle the fill.

Rationale:
- If Instance B (wrong instance) gets the fill: skip → Instance A (right instance) processes it
- If no instance has the order (e.g. restart): we would not flatten, leaving an unprotected position — but that is a rarer case and would need separate handling

**Implementation**: In `HandleExecutionUpdateReal`, when `!_orderMap.TryGetValue(intentId, out orderInfo)` and we are about to flatten:
- First check: do we have **any** orders in `_orderMap` for this instrument? If `_orderMap` is empty for this instrument, we likely did not submit any orders → **skip** (return without flattening).
- Alternatively: only flatten if we have the intent in `_intentMap` **and** we have at least one order in `_orderMap` for this instrument (proving we are the managing instance). If we have no orders at all, skip.

**Simpler approach**: When order not in `_orderMap`, **skip processing** (return early, no flatten). Log at DEBUG. The instance that has the order will process. Risk: if the correct instance also lost the order, no one flattens. Mitigation: reconciliation will still detect the mismatch and freeze the instrument; operator can flatten manually.

---

## Immediate Actions for User

1. **Flatten MNQ** if there is still a position (logs suggest it was already flattened).
2. **Check for duplicate MNQ charts**: ensure only **one** strategy instance runs for MNQ per account.
3. **Force reconcile** if needed: `.\scripts\force_reconcile.ps1 MNQ` after flattening.
4. **Re-enable MNQ** in execution policy after reconciliation.

---

## Fixes Implemented (Post-Incident)

For **single-instance** scenarios (one MNQ chart, one breakout) where order was not in OrderMap:

1. **Instrument matching** (`ExecutionInstrumentResolver.IsSameInstrument`): `hasAnyOrderForInstrument` now treats NQ (order.MasterInstrument.Name) and MNQ (orderInfo.Instrument) as the same market. Prevents incorrect wrong-instance skip.

2. **Retry for all order states**: Previously retried only when `orderState == Initialized`. Now retries 5×50ms for any state when order not in map (handles same-tick sim fill race).

3. **Diagnostic logging** (`EXECUTION_UPDATE_ORDER_MAP_RETRY`): When order not in map, logs intentId, orderId, order_map_count, and order_map_intent_ids before retry for debugging.

---

## References

- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` — `HandleExecutionUpdateReal`, EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL
- `RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs` — OnOrderUpdate, OnExecutionUpdate instrument filter
- `docs/robot/NG1_NG2_SAME_PRICE_INCIDENT_ANALYSIS.md` — similar “order not in map” pattern
