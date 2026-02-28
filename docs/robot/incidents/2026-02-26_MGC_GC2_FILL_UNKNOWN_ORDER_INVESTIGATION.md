# MGC GC2 Fill — Unknown Order / Flatten Investigation (2026-02-26)

**Date:** 2026-02-26  
**Stream:** GC2 (10:00 S2 slot)  
**Instrument:** MGC  
**Outcome:** GC2 long entry filled at 5210.5, but system treated it as untracked → flattened immediately. Journal never updated. No protectives placed.

---

## Executive Summary

| Item | Finding |
|------|---------|
| **Root cause** | Strategy **restarted** between order submission (16:45 UTC) and fill (19:48 UTC). New instance had empty OrderMap; fill arrived at wrong instance. |
| **Evidence** | Different `run_id` at submission vs fill: `983a2fb4...` vs `9fdbbdf9...` |
| **Consequence** | EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL → FLATTEN_IMMEDIATELY. Journal never updated. Position closed by flatten. |

---

## Timeline (UTC)

| Time | Event | run_id | Details |
|------|-------|--------|---------|
| **16:45:05** | ORDER_SUBMIT_SUCCESS (Long) | 983a2fb4... | GC2 long stop @ 5210.4, qty 2, broker_id 70c6f6b8... |
| **16:45:05** | ORDER_SUBMIT_SUCCESS (Short) | 983a2fb4... | GC2 short stop @ 5145.1, qty 2, broker_id d34187d0... |
| **~16:45–19:48** | *(Strategy/NinjaTrader restart)* | — | run_id changes; OrderMap/IntentMap cleared |
| **19:48:14** | EXECUTION_UPDATE_UNKNOWN_ORDER | 9fdbbdf9... | Fill for 2ddc6d287f1bc236, broker_id 408453620750, Filled |
| **19:48:14** | EXECUTION_UPDATE_UNKNOWN_ORDER | 9fdbbdf9... | OCO sibling 91b7d42bc7804ef5, broker_id 408453620748, CancelPending |
| **19:48:14.953** | EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL | 9fdbbdf9... | Order not in map after 5 retries → FLATTEN_IMMEDIATELY |
| **19:48:15** | EXECUTION_UPDATE_UNKNOWN_ORDER | 9fdbbdf9... | OCO sibling Cancelled |
| **19:49:57** | EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL | 9fdbbdf9... | Flatten order filled (tag "Close"), qty 2 @ 5210.6 |

---

## Root Cause: Restart Between Submit and Fill

### run_id Mismatch

- **Submission (16:45):** `run_id = 983a2fb482a64202a62b910cd99bc83b`
- **Fill (19:48):** `run_id = 9fdbbdf95680408f90ab189cc39fee0e`

The `run_id` is assigned when a strategy instance starts. A different `run_id` at fill time means the strategy (or NinjaTrader) was restarted between 16:45 and 19:48 UTC.

### Why Order Not in Map

1. **Old instance** (run_id 983a2fb4): Submitted GC2 brackets at 16:45. Had orders in OrderMap.
2. **Restart:** NinjaTrader or strategy restarted. New instance started with empty OrderMap and IntentMap.
3. **New instance** (run_id 9fdbbdf9): Received fill callback at 19:48. Looked up order by intentId → not in OrderMap.
4. **Retry:** 5×50ms retry did not help (order was never in this instance's map).
5. **Fail-closed:** EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL → Flatten.

### Broker Order ID Format

- **At submit:** `70c6f6b8ffc34ccda258dac7779a9188` (NinjaTrader internal)
- **At fill:** `408453620750` (broker format)

NinjaTrader can report different ID formats. The fill at 408453620750 corresponds to the long stop that filled.

---

## What Should Have Happened

1. Fill arrives at instance that submitted the order.
2. HandleEntryFill runs → RecordEntryFill → protective stop + target.
3. Journal updated with EntryFilled=true.

## What Actually Happened

1. Fill arrived at **restarted** instance (empty OrderMap).
2. Unknown order path → flatten.
3. Journal never updated.
4. Flatten order filled (UNTrackED_FILL) → redundant flatten.

---

## Recommendations

### 1. Hydration on Restart (Medium-Term)

When strategy restarts, **hydrate OrderMap/IntentMap from execution journal** for open positions. If journal shows EntryFilled=true for an intent, and account has position, adopt the position and protectives instead of treating fills as unknown.

### 2. Skip Flatten When Wrong Instance (Already Implemented for NQ1/NG1)

The NQ1/NG1 fix: when order not in map, **skip** (don't flatten) if we have no orders for this instrument — likely wrong instance. Let the instance that has the order handle it.

**Caveat:** After restart, NO instance has the order. So skip would leave position unprotected. Hydration would address this.

### 3. Log Restart Events

Log `STRATEGY_RESTART` or `ENGINE_RESTART` with run_id when strategy starts. Makes it easier to correlate run_id changes with restarts.

### 4. Consider Journal-Based Order Recovery

On startup, if execution journal has open entries (EntryFilled=true, TradeCompleted=false) for this instrument, pre-populate IntentMap from journal so that when broker sends late fill callbacks (e.g. from prior session), we can match them.

---

## Related

- [2026-02-17 NQ1 Fill No Protectives](2026-02-17_NQ1_FILL_NO_PROTECTIVES_INVESTIGATION.md) — same "order not in map" pattern; wrong instance
- [NG1/NG2 Same Price Incident](../NG1_NG2_SAME_PRICE_INCIDENT_ANALYSIS.md) — same pattern
- [M2K Journal/Broker Mismatch](2026-02-26_M2K_JOURNAL_BROKER_MISMATCH_INVESTIGATION.md) — same day, different issue

---

*End of Investigation*
