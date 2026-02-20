# Order CancelPending Investigation

**Date:** 2026-02-19  
**Issue:** `ORDER_REJECTED` with error "Order can't be submitted: order status is CancelPending"

## Root Cause

**This is expected OCO (One-Cancels-Other) behavior, not a true rejection.**

### Timeline (from robot_MNQ.jsonl)

1. **17:39:28** – INTENT_POLICY_REGISTERED for NQ2 intents (f00de9da, 53c514f8)
2. **17:39:29** – Entry stop orders submitted (OCO bracket: buy stop + sell stop)
3. **17:39:43** – Order `02fb8f51...` goes to **CancelPending** (OCO sibling filled)
4. **17:39:44** – ORDER_SUBMIT_FAIL (first rejection callback)
5. **17:39:45** – ORDER_REJECTED with "Order can't be submitted: order status is CancelPending"

### What Happens

- Breakout strategy uses OCO_ENTRY: buy stop and sell stop in same OCO group
- When price breaks **up** → buy stop fills, sell stop is **cancelled**
- When price breaks **down** → sell stop fills, buy stop is **cancelled**
- NinjaTrader reports the cancelled sibling as `Rejected` with error "order status is CancelPending"
- This is NinjaTrader's way of indicating the order was cancelled (OCO), not a broker rejection

### Evidence

- `orderUpdate_OrderState` in first callback: **CancelPending**
- `orderUpdate_Error`: **UnableToSubmitOrder**
- Order type: **ENTRY_STOP** (not protective STOP/TARGET)
- OCO group: `QTSW2:OCO_ENTRY:2026-02-19:NQ2:10:30:...`

## Fix

Treat `Rejected` + "CancelPending" as **OCO sibling cancellation** (expected):

1. Do **not** log as `ORDER_REJECTED` (ERROR)
2. Do **not** call `RecordRejection` (no broker rejection occurred)
3. Log as `ORDER_CANCELLED` or `OCO_SIBLING_CANCELLED` (INFO)
4. Set `orderInfo.State = "CANCELLED"`

## Implementation

In `HandleOrderUpdateReal`, when `orderState == OrderState.Rejected`:

- If `comment` or `errorMsg` contains "CancelPending" (case-insensitive):
  - Treat as OCO cancellation
  - Log as `OCO_SIBLING_CANCELLED` (INFO), update state to CANCELLED, skip RecordRejection

### Forensic Traceability (Senior Quant Review)

`OCO_SIBLING_CANCELLED` logs the following fields for complete audit depth:

| Field | Description |
|-------|-------------|
| `oco_group_id` | OCO group identifier from order |
| `sibling_order_id` | This order's ID (the cancelled sibling) |
| `filled_order_id` | The other order in the OCO group that filled (if found) |
| `execution_instrument_key` | Resolved execution instrument key |
| `intent_id` | Intent ID from order tag (if mapped) |
