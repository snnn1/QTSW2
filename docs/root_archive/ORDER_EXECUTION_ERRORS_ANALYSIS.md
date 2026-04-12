# Order Execution Errors Analysis

## Summary of Issues

### 1. EXECUTION_UPDATE_UNKNOWN_ORDER (24+ errors)
**Error**: `Order not found in tracking map`  
**Pattern**: Execution updates arriving for orders not in `_orderMap`  
**Example**: `broker_order_id: 3c153cdc86a54427ab8e20f53d53594c`, `tag: QTSW2:35a137740523fa3e`

**Root Cause Analysis**:
- Orders are being created (`ORDER_CREATED_VERIFICATION`) with `order_id` set
- Orders are being rejected (`ORDER_REJECTED`)
- Execution updates arrive for these orders but they're not in `_orderMap`

**Possible Causes**:
1. **Timing Issue**: Execution updates arrive before order is added to `_orderMap`
2. **Rejected Orders**: Orders rejected before being tracked still receive execution updates
3. **Order ID Mismatch**: `order.OrderId` doesn't match the `order_id` used in tracking
4. **Multiple Execution Updates**: Same order getting multiple execution updates (fills, state changes)

### 2. ORDER_REJECTED (Many errors, especially MYM - 85 errors)
**Error**: Orders being rejected by broker  
**Pattern**: StopMarket orders for MYM being rejected  
**Example**: `order_id: bfabc5e2760d4d5a927e15e4c9c92c1f`, `instrument: MYM`, `order_type: StopMarket`

**Root Cause**: 
- Orders are being submitted but rejected by NinjaTrader/broker
- Error messages not being extracted (old code - needs rebuild)
- Likely broker-side rejection (instrument not supported, price out of range, etc.)

### 3. ORDER_SUBMIT_FAIL
**Error**: `Order rejected`  
**Pattern**: Occurs right before `ORDER_REJECTED`  
**Impact**: Orders fail to submit, then get rejected

## Error Breakdown by Instrument

| Instrument | Errors |
|------------|--------|
| MYM        | 85     |
| MGC        | 37     |
| MNG        | 33     |
| M2K        | 30     |
| ENGINE     | 27     |
| MNQ        | 21     |
| MES        | 12     |
| RTY        | 2      |
| GC         | 1      |
| NQ         | 1      |
| YM         | 1      |

## Root Causes

### Issue 1: Order Tracking Race Condition
**Problem**: Execution updates arrive before orders are tracked in `_orderMap`

**Current Flow**:
1. Order created (`ORDER_CREATED_VERIFICATION`)
2. Order submitted to NinjaTrader
3. Order added to `_orderMap` (`_orderMap[intentId] = orderInfo`)
4. Execution updates arrive → but order might not be in map yet

**Fix Needed**: Ensure order is added to `_orderMap` BEFORE submission, or handle execution updates for untracked orders gracefully

### Issue 2: Rejected Orders Still Get Execution Updates
**Problem**: When orders are rejected, they may still receive execution updates (state changes, fills)

**Current Behavior**:
- Order rejected → `orderInfo.State = "REJECTED"`
- Order stays in `_orderMap` but execution updates may arrive
- If order was never added to map, execution updates fail

**Fix Needed**: 
- Keep rejected orders in `_orderMap` but mark as rejected
- Handle execution updates for rejected orders gracefully
- Log but don't error on execution updates for rejected orders

### Issue 3: Order Rejection Reasons Not Visible
**Problem**: Error messages not being extracted (old code running)

**Fix**: Rebuild with new error extraction code that uses `OrderEventArgs.ErrorCode` and dynamic property access

## Recommended Fixes

### Fix 1: Add Order to Map Before Submission
**File**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`

**Change**: Move `_orderMap[intentId] = orderInfo;` to BEFORE `account.Submit()` call

### Fix 2: Handle Execution Updates for Untracked Orders
**File**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` (HandleExecutionUpdateReal)

**Change**: 
- Log execution updates for untracked orders as INFO (not WARN)
- Don't return early - try to handle gracefully
- Check if order was rejected before tracking

### Fix 3: Keep Rejected Orders in Map
**File**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` (HandleOrderUpdateReal)

**Change**: 
- Don't remove rejected orders from `_orderMap`
- Mark as rejected but keep for execution update handling
- Handle execution updates for rejected orders (may be state changes)

### Fix 4: Rebuild with Error Extraction Fix
**Action**: Rebuild NinjaTrader project to get new error extraction code

## Next Steps

1. ✅ Fix order tracking race condition
2. ✅ Handle execution updates for untracked/rejected orders gracefully
3. ✅ Rebuild to get error extraction fixes
4. ⏳ Investigate why MYM orders are being rejected (broker-side issue?)
