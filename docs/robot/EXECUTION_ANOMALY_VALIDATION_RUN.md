# Execution Anomaly Validation Run

**Purpose:** Validate all eight anomaly detectors fire correctly and do not produce false positives during legitimate operations.

**Status:** [x] Automated subset run | [ ] Full manual run complete

---

## Automated Run (tools/run_anomaly_validation.py)

| Scenario | Result | Notes |
|----------|--------|-------|
| 5. Working order past threshold | PASS | ORDER_STUCK_DETECTED fires once; does not repeat |
| 6. Artificial delayed fill | PASS | EXECUTION_LATENCY_SPIKE_DETECTED with correct latency_ms |
| 7. Repeated recovery loop | PASS | RECOVERY_LOOP_DETECTED when count ≥ 3 in window |
| 8. ENGINE_START clears pending | PASS | No false stuck after restart |

**Robot-side (1–4) require NinjaTrader — run manually.**

---

## How to Run

1. Start watchdog and robot (sim or live).
2. Execute each scenario below.
3. Record which events appear in the feed (frontend_feed.jsonl, watchdog REST `/events`, or WebSocket).
4. Note any false positives, masking, or spam.

---

## Priority Validation Set

### 1. Unknown/unmapped fill simulation

**Setup:** Simulate a fill for an order that cannot be mapped to an owned/adopted registry entry (e.g. manual fill in NT, or inject a fill event for unknown order).

| Check | Result |
|-------|--------|
| EXECUTION_GHOST_FILL_DETECTED fires | [ ] Yes [ ] No |
| Structured context present (account, instrument, broker_order_id, quantity, price, side, mapped=false, reason) | [ ] Yes [ ] No |
| No other anomaly fires incorrectly | [ ] Yes [ ] No |

**Notes:**
```
(Record timestamp, event payload snippet, any issues)
```

---

### 2. Missing or wrong protective simulation

**Setup:** Create a position where broker-side protectives do not match expected (e.g. restart with mismatched stop/target qty or price; or manually modify protective in NT).

| Check | Result |
|-------|--------|
| PROTECTIVE_DRIFT_DETECTED fires | [ ] Yes [ ] No |
| Not masked by generic PROTECTIVE_MISMATCH_FAIL_CLOSE / PROTECTIVE_QUANTITY_MISMATCH_FAIL_CLOSE | [ ] Yes [ ] No |
| drift_type and expected/actual fields present | [ ] Yes [ ] No |

**Notes:**
```
(Record timestamp, event payload snippet, any issues)
```

---

### 3. Duplicate entry submission path

**Setup:** Trigger a second entry order submission for the same intent while the first is still working (e.g. rapid double-click or logic bug).

| Check | Result |
|-------|--------|
| DUPLICATE_ORDER_SUBMISSION_DETECTED fires | [ ] Yes [ ] No |
| Fires only when a true duplicate is blocked (not on cancel-replace or broker child orders) | [ ] Yes [ ] No |
| first_order_id, intent_id, role present | [ ] Yes [ ] No |

**Notes:**
```
(Record timestamp, event payload snippet, any issues)
```

---

### 4. Broker/runtime position mismatch

**Setup:** Induce broker_qty ≠ journal_qty (e.g. manual trade, journal corruption, or simulated mismatch).

| Check | Result |
|-------|--------|
| POSITION_DRIFT_DETECTED appears in feed | [ ] Yes [ ] No |
| RECONCILIATION_QTY_MISMATCH also fires (expected) | [ ] Yes [ ] No |
| Appears clearly in watchdog alerts (not buried in generic reconciliation text) | [ ] Yes [ ] No |
| broker_qty, engine_qty, journal_qty, drift_class present | [ ] Yes [ ] No |

**Notes:**
```
(Record timestamp, event payload snippet, any issues)
```

---

### 5. Working order left open past threshold

**Setup:** Submit an entry order and leave it working for >120s without fill or cancel (e.g. place limit far from market).

| Check | Result |
|-------|--------|
| ORDER_STUCK_DETECTED fires | [ ] Yes [ ] No |
| Fires once (or at most once per order), not every cycle | [ ] Yes [ ] No |
| working_duration_seconds, threshold_seconds present | [ ] Yes [ ] No |

**Notes:**
```
(Record timestamp, event payload snippet, any issues)
```

---

### 6. Artificial delayed fill

**Setup:** Submit an order and artificially delay the fill >5s (e.g. sim with delayed execution, or manual delay before accepting fill).

| Check | Result |
|-------|--------|
| EXECUTION_LATENCY_SPIKE_DETECTED fires | [ ] Yes [ ] No |
| latency_ms, threshold_ms, submitted_at, executed_at correct | [ ] Yes [ ] No |

**Notes:**
```
(Record timestamp, event payload snippet, any issues)
```

---

### 7. Repeated recovery trigger loop

**Setup:** Trigger 3+ disconnects/recoveries within 10 minutes (e.g. disconnect broker, reconnect, repeat).

| Check | Result |
|-------|--------|
| RECOVERY_LOOP_DETECTED fires | [ ] Yes [ ] No |
| Edge-triggers (fires when crossing threshold, not every cycle) | [ ] Yes [ ] No |
| Does not spam repeatedly | [ ] Yes [ ] No |
| count, window_seconds, current_recovery_state present | [ ] Yes [ ] No |

**Notes:**
```
(Record timestamp, event payload snippet, any issues)
```

---

### 8. Reconnect/bootstrap with adoption (negative test)

**Setup:** Disconnect, reconnect, allow bootstrap/adoption to complete. Legitimate recovery with order adoption.

| Check | Result |
|-------|--------|
| EXECUTION_GHOST_FILL_DETECTED does NOT fire falsely | [ ] Pass [ ] Fail |
| ORPHAN_ORDER_DETECTED / MANUAL_OR_EXTERNAL does NOT fire for adopted orders | [ ] Pass [ ] Fail |
| ORDER_STUCK_DETECTED does NOT fire for orders just adopted | [ ] Pass [ ] Fail |
| Recovery completes normally | [ ] Yes [ ] No |

**Notes:**
```
(Record timestamp, any false positives, any issues)
```

---

## 6. Chaos-Style Operational Test (Last Step Before Real Trading)

Before using with a prop account, run these chaos-style scenarios. If robot + watchdog behave correctly, the system is operationally ready.

### A. Disconnect during trade

**Setup:** Open a position (entry filled, protectives working). Disconnect broker/connection mid-trade.

| Check | Result |
|-------|--------|
| DISCONNECT_FAIL_CLOSED_ENTERED or DISCONNECT_RECOVERY_STARTED fires | [ ] Yes [ ] No |
| Position handled safely (flatten or recovery) | [ ] Yes [ ] No |
| Watchdog surfaces recovery state | [ ] Yes [ ] No |
| No spurious anomaly events | [ ] Yes [ ] No |

**Notes:**
```
```

---

### B. Order reject

**Setup:** Trigger an order rejection (e.g. invalid price, insufficient margin, exchange reject).

| Check | Result |
|-------|--------|
| ORDER_REJECTED or ORDER_SUBMIT_FAIL fires | [ ] Yes [ ] No |
| Robot does not retry blindly | [ ] Yes [ ] No |
| Watchdog shows rejection in feed | [ ] Yes [ ] No |
| No false ghost/orphan from rejected order | [ ] Yes [ ] No |

**Notes:**
```
```

---

### C. Stop cancellation

**Setup:** Cancel a protective stop (manually in NT or via broker) while position is open.

| Check | Result |
|-------|--------|
| Order state updates propagate | [ ] Yes [ ] No |
| PROTECTIVE_DRIFT or protective-failure path fires if expected | [ ] Yes [ ] No |
| Robot responds (e.g. flatten, re-place) | [ ] Yes [ ] No |
| No false duplicate/stuck from cancel | [ ] Yes [ ] No |

**Notes:**
```
```

---

### D. Partial fills

**Setup:** Submit order for N contracts; receive partial fill(s) before full fill.

| Check | Result |
|-------|--------|
| EXECUTION_PARTIAL_FILL and EXECUTION_FILLED fire correctly | [ ] Yes [ ] No |
| Journal and position reconcile | [ ] Yes [ ] No |
| No false position drift from partial sequence | [ ] Yes [ ] No |

**Notes:**
```
```

---

### E. Restart during open position

**Setup:** Stop robot/strategy while position is open. Restart. Allow bootstrap/recovery.

| Check | Result |
|-------|--------|
| Bootstrap/adoption or recovery runs | [ ] Yes [ ] No |
| Open position and protectives reconciled | [ ] Yes [ ] No |
| No false ghost fill, orphan, or stuck from restart | [ ] Yes [ ] No |
| Recovery completes or operator notified | [ ] Yes [ ] No |

**Notes:**
```
```

---

### Chaos Test Summary

| Scenario | Pass | Fail | Notes |
|----------|------|------|-------|
| A. Disconnect during trade | | | |
| B. Order reject | | | |
| C. Stop cancellation | | | |
| D. Partial fills | | | |
| E. Restart during open position | | | |

---

## Summary

| Scenario | Pass | Fail | Notes |
|----------|------|------|-------|
| 1. Unknown/unmapped fill | | | |
| 2. Missing/wrong protective | | | |
| 3. Duplicate entry submission | | | |
| 4. Broker/runtime position mismatch | | | |
| 5. Working order past threshold | | | |
| 6. Artificial delayed fill | | | |
| 7. Repeated recovery loop | | | |
| 8. Reconnect/bootstrap adoption (no false positives) | | | |

---

## 7. Burn-In (Final Real Test Before Prop)

Before using with a prop account, run a **runtime burn-in**:

| Requirement | Target |
|-------------|--------|
| Duration | At least 3–5 sessions |
| Conditions | Real-time market conditions |
| Code changes | None during burn-in |

**Watch for:**
- Anomaly noise (false positives)
- Watchdog latency (FEED_INGESTION_DELAY, WATCHDOG_LOOP_SLOW)
- Stream disappearance
- False stalls (ENGINE_TICK_STALL_DETECTED when engine is fine)
- Execution edge cases (partial fills, rejects, restarts)

Burn-in confirms the system is operationally stable, not just logically correct.

---

## Threshold Tuning / Suppression Notes

*(Fill in after validation if any detectors need adjustment)*

- Ghost fill:
- Protective drift:
- Duplicate order:
- Position drift:
- Stuck order:
- Latency spike:
- Recovery loop:
- Adoption/recovery exclusions:
