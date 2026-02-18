# NG1 / NG2 Same-Price Fill Incident — Analysis

**Scenario**: NG1 and NG2 both filled at the same price; an error occurred; a buy order "broke" something.

---

## 1. Range Overlap (2026-02-17)

From `logs/robot/ranges_2026-02-17.jsonl`:

| Stream | Range | Breakout Long | Breakout Short | Range End (Chicago) |
|--------|-------|--------------|----------------|---------------------|
| NG1 | 3.039 – 3.17 | 3.171 | **3.038** | 09:00 |
| NG2 | 3.039 – 3.087 | 3.088 | **3.038** | 10:30 |

**Finding**: NG1 and NG2 share the same breakout short level: **3.038**.

When price hits 3.038:
- NG1’s short breakout triggers (if NG1 is still active)
- NG2’s short breakout triggers (if NG2 is active)
- Both submit sell (short) entry orders at the same price
- Both can fill → net position = 2 short (e.g. -2 MNG)

---

## 2. Intent Isolation

- **Intent ID** = hash of `(TradingDate, Stream, Instrument, Session, SlotTimeChicago, Direction, EntryPrice, …)`
- NG1 and NG2 have different `Stream` → different intent IDs
- Orders are tagged with `RobotOrderIds.EncodeTag(intentId)` → each order is tied to one intent
- `_orderMap` and `_intentMap` are keyed by `intentId` → no cross-stream collision

**Conclusion**: Fills are attributed to the correct stream. No cross-stream misattribution from intent IDs.

---

## 3. “Buy Order That Broke It”

Possible interpretations:

| Interpretation | Description |
|----------------|-------------|
| **A. Protective stop (buy to cover)** | When a short position’s stop fills, a buy order closes it. If NG1 and NG2 both had shorts and one stop filled, the buy is expected. |
| **B. Protective target (buy to cover)** | Same as above for target fills. |
| **C. Wrong flatten** | A flatten (buy to cover) was triggered for the wrong intent or at the wrong time. |
| **D. Opposite-entry cancel bug** | When one exit fills, we cancel the opposite entry for the **same stream**. Logic uses `filledIntent.Stream` and `otherIntent.Stream`, so NG1/NG2 are isolated. No cross-stream cancel. |
| **E. Double fill / overfill** | Both NG1 and NG2 short entries filled → 2 short. User may have expected only one stream to trade. |

---

## 4. Potential Failure Modes

### 4.1 Both Shorts Fill → Double Position

- Price hits 3.038
- NG1 and NG2 both submit short entries
- Both fill → 2 short contracts
- If only one stream was intended to trade, this is an overfill from a design perspective (two streams, same instrument, overlapping breakout levels)

### 4.2 Instrument Match in `CheckAndCancelEntryStopsOnPositionFlat`

```csharp
// NinjaTraderSimAdapter.NT.cs ~3740
if (intent.Instrument != instrument)
    continue;
```

- `intent.Instrument` = canonical (e.g. NG)
- `instrument` = from `orderInfo.Instrument` (e.g. MNG for execution)
- If `instrument` is MNG and `intent.Instrument` is NG, the check fails and no intents are cancelled
- **Effect**: When position goes flat, entry stops might not be cancelled if instrument naming is inconsistent

### 4.3 `CancelIntentOrders` Iterates All Account Orders

```csharp
// CancelIntentOrdersReal iterates account.Orders and matches by decodedIntentId
foreach (var order in account.Orders)
{
    var decodedIntentId = RobotOrderIds.DecodeIntentId(tag);
    if (decodedIntentId == intentId && !tag.EndsWith(":STOP") && !tag.EndsWith(":TARGET"))
        ordersToCancel.Add(order);
}
```

- Matching is by `intentId` only
- NG1 and NG2 have different `intentId`s → no cross-stream cancellation

### 4.4 OCO / Opposite-Entry Logic

- When a protective stop or target fills, we cancel the opposite entry for the **same stream**
- Search uses `otherIntent.Stream == filledIntent.Stream` → NG1 and NG2 are separate
- No cross-stream cancellation

---

## 5. Recommended Investigation Steps

1. **Logs**
   - Search `logs/robot/robot_NG.jsonl` and `logs/robot/robot_ENGINE.jsonl` for:
     - `EXECUTION_FILLED`, `ORDER_SUBMIT_ATTEMPT`, `OPPOSITE_ENTRY_CANCELLED`, `ENTRY_STOP_CANCELLED`
     - Errors around the incident time
   - Check `logs/robot/frontend_feed.jsonl` for NG1/NG2 fill events

2. **Execution journals**
   - List `data/execution_journals/*NG*.json` for the incident date
   - Confirm which intents filled and at what prices
   - Check for duplicate or unexpected entries

3. **Instrument matching**
   - Verify whether `CheckAndCancelEntryStopsOnPositionFlat` receives NG or MNG
   - If it receives MNG but intents use NG, add a canonical/execution instrument mapping so the match works

4. **Design**
   - Decide whether NG1 and NG2 are allowed to both trade when breakouts overlap
   - If not, consider:
     - Staggering slot times
     - Mutual exclusion (e.g. only one NG stream trades at a time)
     - Explicit config to avoid overlapping breakouts

---

## 6. Summary

| Item | Status |
|------|--------|
| Intent isolation (NG1 vs NG2) | OK – different intent IDs, no cross-stream mix-up |
| Opposite-entry cancel | OK – scoped to same stream |
| Same breakout short (3.038) | Both streams can trigger at same price |
| Instrument match (NG vs MNG) | Possible bug – verify and fix if needed |
| Double fill when both trigger | By design if both streams are active; may need policy change |

**Next**: Run the log/journal checks above and fix the instrument-matching logic if NG/MNG mismatch is confirmed.

---

## 7. Log Investigation Results (2026-02-17 18:03)

### Execution Journals (Critical Finding)

| Intent | Stream | EntryPrice | EntryFilled | EntryFilledQuantityTotal |
|--------|--------|------------|-------------|--------------------------|
| f254587d2cd46f70 | NG1 | 3.038 (Short) | **false** | **0** |
| 48901fb7277dc5a2 | NG2 | 3.038 (Short) | **false** | **0** |

**The journals were never updated** — both show `EntryFilled: false` and `EntryFilledQuantityTotal: 0` even though the orders filled at 3.038. This means `HandleEntryFill` was never called, so **no protective stop/target orders were ever submitted**.

### Log Sequence (robot_ENGINE.jsonl)

| Time (UTC) | Event | Details |
|------------|-------|---------|
| 18:03:38.759 | FLATTEN_INTENT_SUCCESS | intent 48901fb7277dc5a2 (NG2), position_qty: 0 |
| 18:03:40.852 | UNREGISTERED_EVENT_TYPE | UNKNOWN_ORDER_FILL_FLATTENED |
| 18:03:41.082 | FLATTEN_INTENT_SUCCESS | intent f254587d2cd46f70 (NG1), position_qty: 0 |
| 18:03:41.243 | FLATTEN_INTENT_SUCCESS | intent UNKNOWN_UNTrackED_FILL |
| 18:03:41.245 | UNREGISTERED_EVENT_TYPE | UNTrackED_FILL_FLATTENED |

### Root Cause Chain

1. **Both NG1 and NG2 short entries filled at 3.038** (18:03:34.062).
2. **Fill callbacks hit "order not in _orderMap" path** — the adapter treated the fills as untracked and triggered flatten (fail-closed).
3. **Protective orders never placed** — because we never reached `HandleEntryFill`, no stop/target orders were submitted. The 2-short position was unprotected.
4. **Flatten created the buy-to-cover** — the adapter called `Flatten()` for the first "untracked" fill. That triggered the broker's close order (buy to cover @ 3.042).
5. **Buy-to-cover fill was also untracked** — when the flatten order filled, the robot received an execution update for an order without a QTSW2 tag (broker-generated close). That hit `UNTrackED_FILL` and triggered another flatten (redundant).
6. **Multiple flattens** — the robot flattened for NG2 intent, NG1 intent, and UNKNOWN_UNTrackED_FILL. All succeeded (position was already flat after the first flatten).

### Why "Order Not in _orderMap"?

Possible causes when both fills arrive at the same tick (18:03:34.062):

- **Race condition**: Both fills processed concurrently; one or both orders not yet visible in `_orderMap` when the callback runs.
- **Order ID / intent mismatch**: Lookup by `intentId` failed (e.g. wrong key or timing).
- **Multiple adapter instances**: If NG1 and NG2 use different strategy instances, each has its own `_orderMap`; a fill might be routed to the wrong instance.

### Recommendations

1. **Add logging** when adding to `_orderMap` and when a fill is received, including `intentId`, `orderId`, and whether the order was found.
2. **Extend retry window** for the "order not in map" race when both fills arrive on the same tick.
3. **Recognize broker flatten orders** — when we call `Flatten()`, the resulting close order will not have our tag. Avoid treating that fill as `UNTrackED_FILL` and triggering another flatten.
4. **NG1/NG2 overlap** — consider mutual exclusion or staggered slots when both streams share the same breakout level (3.038).

---

## 8. Implementation (2026-02-17)

### Broker flatten recognition
- When we call `Flatten()`, the broker creates a close order with no QTSW2 tag.
- Added `_lastFlattenInstrument` and `_lastFlattenUtc` to track recent flatten calls.
- When a fill arrives with no tag within 10 seconds of a flatten for that instrument, we treat it as our broker flatten order and skip redundant flatten (avoids UNTrackED_FILL cascade).

### Entry order aggregation
- When NG2 submits a stop entry at the same price as NG1's existing working order (e.g. both short at 3.038), we aggregate:
  1. Cancel NG1's short (OCO auto-cancels NG1's long)
  2. Cancel NG2's long (submitted before NG2's short in the same lock cycle)
  3. Submit one aggregated short order for total quantity (2) with tag `QTSW2:AGG:id1,id2`
  4. Re-submit NG1 long and NG2 long with same OCO group
  5. One broker order, two stream journals with allocated quantity
- Fill handling: when aggregated order fills, we allocate quantity to each stream's journal and coordinator.
- Protective orders: submitted once for total quantity (primary intent).
