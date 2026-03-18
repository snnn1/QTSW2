# Real ORDER_STUCK Orders — Diagnosis Report

**Date:** 2026-03-12  
**Scope:** Last 36 ORDER_STUCK_DETECTED candidates (post ORDER_REJECTED fix)  
**Feed:** logs/robot/frontend_feed.jsonl

---

## 1. Root Cause Distribution

| Classification | % | Count | Description |
|----------------|---|-------|-------------|
| **A** | 77.8% | 28 | Legitimately waiting — valid order, price not reached, no cancel expected |
| **B** | 16.7% | 6 | Should have been cancelled but wasn't — position flat or slot expiry |
| **E** | 5.6% | 2 | Missing lifecycle event — cancel/fill arrived after threshold (feed ordering) |

**C (recovery/replacement) and D (broker mismatch):** 0% — not detected from feed analysis.

---

## 2. Top 3 Failure Mechanisms

### 1. **Position flat but entry orders still working** (B — 6 orders)

- **Instruments:** MCL (2), M2K (2), MNG (2)
- **Mechanism:** Position is 0 for instrument, but entry orders remain working. Slot expiry, forced flatten, or market close should have cancelled them.
- **Code reference:** `StreamStateMachine` — slot expiry and forced-flatten paths must cancel entry orders. `ExecutePendingRecoveryAction` / `ReconcileEntryOrders` should detect orphaned entry orders when position is flat.

### 2. **ORDER_CANCELLED arrived after threshold** (E — 2 orders)

- **Broker order IDs:** 408453623864, 408453623874 (MYM protective orders)
- **Mechanism:** ORDER_CANCELLED exists in the feed for these orders, but the event was processed after the 90s protective threshold. Multi-file merge (robot_ENGINE + robot_<instrument>) can reorder events.
- **Code reference:** `event_feed.py` — merge sorts by timestamp; slight clock skew or write order can delay cancel processing. `state_manager.check_stuck_orders` runs every cycle; if cancel arrives late, order is already flagged as stuck.

### 3. **Long-duration working orders** (A — expected)

- **Mechanism:** Entry/stop orders working 3000+ seconds (e.g. 4421s) with open position. Price has not reached the order; no cancel expected.
- **Code reference:** Expected behavior. ORDER_STUCK threshold (120s entry, 90s protective) is a monitoring alert, not a guarantee orders resolve quickly.

---

## 3. Verdict: Expected Behavior vs System Bug

| Category | Verdict |
|----------|---------|
| **A (78%)** | **Expected behavior** — orders are valid, working, and waiting for price. |
| **B (17%)** | **System bug / gap** — entry orders should be cancelled when position flattens or slot expires. |
| **E (6%)** | **Feed ordering / timing** — cancel events exist but arrive after threshold; minor. |

**Overall:** Mixed. Majority is expected; ~17% indicates a cancellation gap (B).

---

## 4. Patterns

| Pattern | Value |
|---------|-------|
| **By instrument** | YM: 26 (72%), MYM: 4, MCL: 2, M2K: 2, MNG: 2 |
| **By role** | Entry: 21 (58%), Stop: 15 (42%) |
| **Avg working time** | 1227.6 seconds (~20 min) |
| **Max working time** | 4421 seconds (~74 min) |
| **Extreme (3000s+)** | 8 orders |

---

## 5. Cross-Check with System State

**Position snapshot (end of feed):**
- MYM: 2
- YM: 14

**Stream state:** Empty in feed (STREAM_STATE_TRANSITION may not include stream_id in expected format).

**B orders (position flat):** MCL, M2K, MNG all have position 0 at detection. Entry orders for these instruments should have been cancelled when position went flat (slot end, flatten, or market close).

---

## 6. Specific Fixes

### B — Position flat, entry orders not cancelled

1. **Slot expiry:** Ensure `StreamStateMachine` cancels entry orders when slot ends. Verify `SlotExpired` / `MARKET_CLOSE_NO_TRADE` paths call cancel.
2. **Forced flatten:** When flatten occurs, cancel all working orders for that instrument/stream.
3. **Reconciliation:** `ReconcileEntryOrders` / `HasValidEntryOrdersOnBroker` — when position is flat, treat working entry orders as orphaned and cancel.

### E — Late cancel events

1. **Feed merge:** Prefer processing events in strict timestamp order; consider buffering events within a small window before applying.
2. **Threshold:** Optionally increase protective threshold (e.g. 90s → 120s) to reduce false positives from late cancels.
3. **Backfill:** When ORDER_CANCELLED is processed, remove from any "recently stuck" cache to avoid duplicate alerts.

### A — Legitimate long waits

1. **Threshold tuning:** 120s/90s may be too aggressive for instruments with low volatility. Consider instrument-specific thresholds.
2. **Suppression:** During known quiet periods (e.g. lunch), optionally suppress or downgrade ORDER_STUCK_DETECTED.

---

## 7. How to Run

```bash
python tools/diagnose_real_order_stuck.py --limit 50
python tools/diagnose_real_order_stuck.py --limit 50 --feed logs/robot/frontend_feed.jsonl
```

Output includes lifecycles, classification, root cause distribution, failure mechanisms, and recommendations.
