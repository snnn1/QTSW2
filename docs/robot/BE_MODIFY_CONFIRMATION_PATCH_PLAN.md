# BE Modify Confirmation — Immediate Patch Plan (Copy-Safe, No Code)

**Date:** 2026-02-18  
**Context:** STOP_MODIFY_SUCCESS logged but stop never moved. Root cause: Change() returns void; dynamic assignment throws RuntimeBinderException; fallback path reports success without broker confirmation.

---

## 1. Locate ModifyStopToBreakEven Implementation

**File:** `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`  
**Method:** `ModifyStopToBreakEvenReal` (approx. lines 3264–3333)

**Call chain:**
- `InstrumentExecutionAuthority.NT.EvaluateBreakEvenCore` → `Executor.ModifyStopToBreakEven`
- `NinjaTraderSimAdapter.ModifyStopToBreakEven` → `ModifyStopToBreakEvenReal`

---

## 2. Fix Change() Call — Remove Dynamic / Void Assignment

**Location:** Lines ~3326–3372 in `ModifyStopToBreakEvenReal`

**Current pattern (problematic):**
```
dynamic dynAccountModify = account;
object? changeResult = dynAccountModify.Change(new[] { stopOrder });
if (changeResult != null && changeResult is Order[] changeArray)
    result = changeArray;
```

**Problem:** NinjaTrader `Account.Change(Order[])` returns `void`. Dynamic invocation assigns void to `object?` → RuntimeBinderException.

**Fix:**
- Remove any assignment of Change() return value.
- Call Change() imperatively: `account.Change(new[] { stopOrder });` (use concrete Account type, not dynamic).
- If NinjaTrader API requires dynamic, call without capturing: `dynAccountModify.Change(new[] { stopOrder });` and do not assign.
- After call, treat `stopOrder` as the modified order (NinjaTrader mutates in place).
- Remove fallback that re-calls Change() and assumes success; that path is what causes false STOP_MODIFY_SUCCESS.

---

## 3. Two-Stage Flow — REQUESTED vs CONFIRMED

**Replace:** Single `STOP_MODIFY_SUCCESS` after Change().

**With:**
1. **STOP_MODIFY_REQUESTED** — Emit immediately after issuing Change(). Include: `intent_id`, `broker_order_id`, `be_stop_price`, `instrument`, `requested_utc`.
2. **STOP_MODIFY_CONFIRMED** — Emit only when an OrderUpdate is observed for the same `broker_order_id` where:
   - Order is STOP (from tag or OrderMap).
   - `order.StopPrice` equals requested BE stop (within tick tolerance).
   - Order state is Working or Accepted.

**Implementation notes:**
- Add a pending-BE structure: `Dictionary<(string brokerOrderId, decimal beStopPrice), (string intentId, string tradingDate, string stream, DateTimeOffset requestedUtc)>`.
- Key: `broker_order_id` (or `(broker_order_id, be_stop_price)` for uniqueness).
- On Change(), register pending and emit STOP_MODIFY_REQUESTED.
- In `HandleOrderUpdateReal`, when processing STOP order with `OrderState.Accepted` or `OrderState.Working`, check if `(order.OrderId, (decimal)order.StopPrice)` matches a pending BE. If match: emit STOP_MODIFY_CONFIRMED, call `RecordBEModification`, remove from pending.
- Emit **ORDER_UPDATED** for STOP orders: `order_type=STOP`, `stop_price`, `order_state`, `broker_order_id` (see section 6).

---

## 4. Timeout — STOP_MODIFY_TIMEOUT

**Behavior:**
- If no STOP_MODIFY_CONFIRMED within N seconds (e.g. 10–15), emit **STOP_MODIFY_TIMEOUT**.
- Do **not** call `RecordBEModification` on timeout.
- Remove from pending.
- Intent remains eligible for BE retry on next tick (idempotency via `IsBEModified` stays false).

**Implementation:**
- Periodic check (e.g. in Tick or reconciliation) or timer: for each pending BE, if `(utcNow - requestedUtc) > N seconds`, emit STOP_MODIFY_TIMEOUT and remove.
- Alternatively: on next BE evaluation for same intent, if pending exists and timed out, emit STOP_MODIFY_TIMEOUT and clear.

---

## 5. Journal Gating

**Current:** `RecordBEModification` called immediately after Change() in `ModifyStopToBreakEvenReal`.

**New:**
- Do **not** call `RecordBEModification` in `ModifyStopToBreakEvenReal`.
- Call `RecordBEModification` only when STOP_MODIFY_CONFIRMED is emitted (i.e. in `HandleOrderUpdateReal` when broker confirms stop price).
- Exception: "stop already tighter" path (lines 3303–3321) can keep immediate RecordBEModification — we have verified the stop is already at or better than BE.

---

## 6. ORDER_UPDATED for STOP Orders

**Location:** `HandleOrderUpdateReal` (NinjaTraderSimAdapter.NT.cs, ~1070).

**Add:** When processing an order that is a STOP (from OrderInfo.OrderType == "STOP" or tag contains `:STOP`), emit:

**ORDER_UPDATED** with:
- `order_type`: "STOP"
- `broker_order_id`: order.OrderId
- `stop_price`: (decimal)order.StopPrice
- `order_state`: order.OrderState.ToString()
- `intent_id`: from tag

**Event type:** Add to RobotEventTypes (e.g. INFO level).

---

## 7. Event Types to Add

| Event | Level | When |
|-------|-------|------|
| STOP_MODIFY_REQUESTED | INFO | After Change() issued |
| STOP_MODIFY_CONFIRMED | INFO | When OrderUpdate confirms stop price |
| STOP_MODIFY_TIMEOUT | WARN | No confirmation within N seconds |
| ORDER_UPDATED | INFO | STOP order update (stop_price, state) |

---

## 8. Files to Modify

| File | Changes |
|------|---------|
| `NinjaTraderSimAdapter.NT.cs` | ModifyStopToBreakEvenReal: fix Change(), REQUESTED flow, remove immediate RecordBEModification; add pending-BE structure; HandleOrderUpdateReal: confirmation logic, ORDER_UPDATED for STOP |
| `RobotEventTypes.cs` | Add STOP_MODIFY_REQUESTED, STOP_MODIFY_CONFIRMED, STOP_MODIFY_TIMEOUT, ORDER_UPDATED |
| `OrderModificationResult` | May need to return "Requested" vs "Confirmed" — or keep Success but gate journal at confirmation layer |

---

## 9. OrderModificationResult Semantics

**Option A:** Keep `Success` for "Change() issued successfully"; caller (IEA/EvaluateBreakEven) does not record journal — adapter records only on CONFIRMED.

**Option B:** Introduce `OrderModificationResult.Requested(utcNow)` — meaning "request sent, awaiting confirmation". Caller treats as "do not record journal yet". Adapter records on confirmation.

**Recommendation:** Option A. ModifyStopToBreakEven returns Success when Change() is issued without exception. Journal recording moves entirely to confirmation path in HandleOrderUpdateReal.

---

## 10. Summary Checklist

- [ ] Remove dynamic assignment of Change() result; call imperatively.
- [ ] Emit STOP_MODIFY_REQUESTED after Change().
- [ ] Add pending-BE tracking (broker_order_id → intentId, tradingDate, stream, requestedUtc).
- [ ] In HandleOrderUpdateReal, on STOP order update: check pending, confirm stop price, emit STOP_MODIFY_CONFIRMED, call RecordBEModification.
- [ ] Do not call RecordBEModification in ModifyStopToBreakEvenReal (except "already tighter" path).
- [ ] Add STOP_MODIFY_TIMEOUT when no confirmation within N seconds; do not record journal.
- [ ] Emit ORDER_UPDATED for STOP orders (stop_price, state, broker_order_id).
- [ ] Register new event types in RobotEventTypes.
