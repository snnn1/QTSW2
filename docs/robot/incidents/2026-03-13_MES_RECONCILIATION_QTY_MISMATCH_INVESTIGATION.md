# MES RECONCILIATION_QTY_MISMATCH Investigation (2026-03-13)

## Summary

**Root cause**: A second "flatten" triggered by `BE_STOP_VISIBILITY_TIMEOUT` at 15:02:43 sent a **SELL 4** order when the account was already flat. That order **opened** 4 short instead of closing, creating orphan positions. Journal correctly shows TradeCompleted (4 in, 4 out at 14:41:58). Broker had 4; journal had 0 → `broker_ahead` mismatch.

---

## Timeline

| Time (UTC) | Event | Details |
|------------|-------|---------|
| 14:41:56 | Entry fill #1 | broker_order_id 408453623508, fill 2, cumulative 2 |
| 14:41:56 | Entry fill #2 | broker_order_id 408453623501, fill 2, cumulative 4 (**overfill**) |
| 14:41:58 | Flatten executed | broker_order_id 408453623555, **BUY** 4, FLATTEN |
| 14:41:58 | TRADE_COMPLETED | Journal marked TradeCompleted=True, ExitQty=4 |
| 15:02:43 | BE_STOP_VISIBILITY_TIMEOUT | "Stop still not visible after 5s" → triggered flatten |
| 15:02:43 | FLATTEN_ATTEMPT | Second flatten attempt |
| 15:02:45 | EXECUTION_FILLED (UNMAPPED) | broker_order_id 408453623636, **SELL** 4, order_type UNMAPPED |
| 15:02:45 | EXECUTION_FILL_UNMAPPED | "No active exposures for instrument - PnL gap" |
| 15:03–15:04 | RECONCILIATION_QTY_MISMATCH | account_qty=4, journal_qty=0, broker_ahead |

---

## Root Cause Analysis

### 1. Correct first flatten (14:41:58)

- Entry: 4 short (2+2 overfill from two entry orders).
- Flatten: BUY 4 to close → account flat.
- Journal: TradeCompleted=True, ExitFilledQuantityTotal=4.

### 2. BE_STOP_VISIBILITY_TIMEOUT (15:02:43)

- The stop order (408453623547) was cancelled at 14:46:44 as part of the flatten flow.
- ~16 minutes later, the BE (breakeven) visibility check still expected the stop to be visible.
- When it was not found after 5s, the system treated this as an inconsistency and triggered a flatten.

### 3. Second flatten opened a new position (15:02:45)

- The flatten order at 15:02:45 was **SELL 4**.
- When the account is flat, SELL 4 **opens** 4 short.
- The journal had no open intents (TradeCompleted), so the fill was logged as `EXECUTION_FILL_UNMAPPED` and not applied to any journal.
- Result: broker shows 4 short; journal shows 0 open → `broker_ahead` mismatch.

### 4. Why the second flatten was SELL instead of BUY

- The first position was short (4 contracts).
- A correct flatten would be BUY 4 to close.
- The second flatten was sent as SELL 4, which is the wrong direction for closing a short.
- Likely cause: the flatten logic used the wrong side when the account was already flat, or it inferred direction from stale state.

---

## Evidence

### Overfill (14:41:56)

```json
{"event":"INTENT_FILL_UPDATE","data":{"intent_id":"44659b6859bae99b","fill_qty":"2","cumulative_filled_qty":"4","overfill":"True"}}
```

### First flatten (14:41:58) – correct

```json
{"event":"EXECUTION_FILLED","data":{"broker_order_id":"408453623555","side":"BUY","order_type":"FLATTEN","fill_quantity":"4","position_effect":"CLOSE"}}
```

### Second flatten (15:02:45) – wrong direction

```json
{"event":"EXECUTION_FILLED","data":{"broker_order_id":"408453623636","side":"SELL","order_type":"UNMAPPED","fill_quantity":"4"}}
{"event":"EXECUTION_FILL_UNMAPPED","data":{"error":"Broker flatten fill cannot be mapped to any intent","fill_price":"6663.5","fill_quantity":"4","note":"No active exposures for instrument - PnL gap"}}
```

---

## Immediate Resolution

1. **Force reconcile** (if broker position is confirmed correct and you want to clear the freeze):
   ```powershell
   .\scripts\force_reconcile.ps1 MES
   ```

2. **Or flatten and restart**:
   - Manually flatten the 4 MES short in NinjaTrader.
   - Restart the robot so reconciliation sees broker flat and journals match.

---

## Recommended Fixes

1. **BE_STOP_VISIBILITY_TIMEOUT → flatten logic**
   - Before sending a flatten, verify the account actually has a position.
   - If `Account.Positions` shows flat for the instrument, do not send a flatten.
   - Or: when flattening due to visibility timeout, use the **current** account position to determine direction and size, not the intent’s original direction.

2. **Flatten direction**
   - Ensure flatten orders always use the side that **closes** the current position (e.g. BUY to close short, SELL to close long).
   - Derive direction from `Account.Positions` at flatten time, not from intent state.

3. **Post-flatten cleanup**
   - After a successful flatten, clear or mark the intent as completed so BE visibility checks do not keep expecting the old stop order.

---

## Related

- `docs/robot/execution/RECONCILIATION_QTY_MISMATCH_RESOLUTION.md`
- `docs/robot/incidents/2026-03-11_MYM_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md`
- Journal: `data/execution_journals/2026-03-13_ES2_44659b6859bae99b.json`
