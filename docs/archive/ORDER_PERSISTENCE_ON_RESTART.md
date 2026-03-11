# Order Persistence on Restart/Shutdown

## Summary

**Orders persist in NinjaTrader** - they survive strategy restarts and NinjaTrader shutdowns. The robot has **idempotency logic** to detect and reuse existing orders.

---

## What Happens to Orders

### NinjaTrader Behavior

1. **Orders Persist in Account**
   - NinjaTrader orders are stored in the broker/account system
   - Orders remain active even if:
     - Strategy is stopped/restarted
     - NinjaTrader is closed and reopened
     - Computer is restarted (if NT auto-starts)

2. **Order States**
   - **Working orders** remain in the market
   - **Filled orders** are recorded in account history
   - **Rejected orders** are logged but not resubmitted

### Robot Behavior on Restart

The robot has **idempotency checks** to prevent duplicate orders:

#### 1. Stop Entry Orders (Breakout Orders)

**Current Implementation:**
- Checks execution journal: `IsIntentSubmitted()` (line 3258-3260)
- If intent was already submitted → skips placement
- **Does NOT check for existing orders in account** (potential gap)

**What This Means:**
- ✅ If journal shows orders were submitted → won't duplicate
- ⚠️ If journal is missing but orders exist → **may try to place duplicates**
- ⚠️ If NT shutdown cleared journal but orders persist → **may duplicate**

#### 2. Protective Stop-Loss Orders

**Has Idempotency:**
```csharp
// Check for existing order by tag
var existingStop = account.Orders.FirstOrDefault(o =>
    GetOrderTag(o) == stopTag &&
    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));

if (existingStop != null)
{
    // Reuse existing order, update if needed
    return OrderSubmissionResult.SuccessResult(existingStop.OrderId, utcNow, utcNow);
}
```

**What This Means:**
- ✅ Checks `account.Orders` for existing orders
- ✅ Reuses existing orders if found
- ✅ Updates price/quantity if they differ
- ✅ Prevents duplicates

#### 3. Target Orders

**Has Idempotency:**
- Same pattern as protective stops
- Checks `account.Orders` for existing target orders
- Reuses or updates as needed

---

## Scenarios

### Scenario 1: Strategy Restart (NT Still Running)

**What Happens:**
1. Strategy calls `OnStateChange()` → `State.Realtime`
2. Robot engine initializes
3. Streams load from journal
4. **Stop Entry Orders:**
   - Checks journal: `StopBracketsSubmittedAtLock = true`
   - Checks execution journal: Intent already submitted
   - **Skips placement** (idempotent via journal)
5. **Protective Orders:**
   - Checks `account.Orders` for existing orders
   - Reuses if found, creates if missing

**Result:** ✅ No duplicates, existing orders continue working

### Scenario 2: NinjaTrader Shutdown & Restart

**What Happens:**
1. NT shuts down → orders remain in broker system (SIM account)
2. NT restarts → orders still exist in account
3. Strategy starts → robot initializes
4. **Stop Entry Orders:**
   - Journal persists (file-based)
   - Checks journal: `StopBracketsSubmittedAtLock = true`
   - **Should skip placement** (if journal intact)
5. **Protective Orders:**
   - Checks `account.Orders` → finds existing orders
   - Reuses them

**Result:** ✅ Orders continue working, no duplicates

### Scenario 3: Journal Lost but Orders Exist

**What Happens:**
1. Journal file deleted/corrupted
2. Orders still exist in NT account
3. Strategy restarts
4. **Stop Entry Orders:**
   - Journal missing → `StopBracketsSubmittedAtLock = false`
   - Execution journal missing → intent not found
   - **May try to place orders** → **POTENTIAL DUPLICATE** ⚠️
5. **Protective Orders:**
   - Checks `account.Orders` → finds existing
   - Reuses them ✅

**Result:** ⚠️ Stop entry orders may duplicate, protective orders safe

### Scenario 4: Orders Filled During Shutdown

**What Happens:**
1. Orders fill while NT is closed
2. NT restarts → fills are recorded in account
3. Strategy starts → robot initializes
4. **Entry Orders:**
   - Journal shows `EntryDetected = false` (if journal intact)
   - Execution journal may show fill (if persisted)
   - Robot checks execution journal for fills
5. **Protective Orders:**
   - If entry filled → protective orders should be placed
   - If entry not detected → protective orders won't be placed

**Result:** ⚠️ May need manual reconciliation

---

## Current Gaps

### Stop Entry Orders

**Missing:** Check for existing orders in `account.Orders` before placement

**Current Logic:**
```csharp
// Only checks execution journal
if (_executionJournal != null && 
    (_executionJournal.IsIntentSubmitted(longIntentId, TradingDate, Stream) ||
     _executionJournal.IsIntentSubmitted(shortIntentId, TradingDate, Stream)))
{
    // Skip - already submitted
    return;
}
```

**Should Also Check:**
```csharp
// Check account.Orders for existing orders with OCO group
var existingOrders = account.Orders.Where(o => 
    GetOrderOcoGroup(o) == ocoGroup &&
    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));
    
if (existingOrders.Any())
{
    // Orders already exist - reuse them
    return;
}
```

---

## Recommendations

### For Stop Entry Orders

1. **Add Account Check** (like protective orders):
   - Check `account.Orders` for existing orders with matching OCO group
   - Reuse if found, create if missing

2. **Journal Backup:**
   - Keep journal files safe
   - Consider journal backup/restore on restart

3. **Order Reconciliation:**
   - On restart, scan `account.Orders` for robot-owned orders
   - Reconcile with journal state
   - Log reconciliation results

### For Protective Orders

✅ **Already Safe** - has idempotency checks

---

## Best Practices

1. **Don't Delete Journals** - They prevent duplicate orders
2. **Monitor Order Count** - Check NT order management for duplicates
3. **Restart During Market Hours** - Orders will be active immediately
4. **Check Account After Restart** - Verify orders are still working
5. **Use OCO Groups** - Prevents both sides from filling simultaneously

---

## Summary

| Order Type | Survives Restart? | Survives NT Shutdown? | Idempotency? | Risk of Duplicates |
|------------|-------------------|----------------------|--------------|-------------------|
| Stop Entry Orders | ✅ Yes | ✅ Yes | ⚠️ Partial (journal only) | ⚠️ Medium (if journal lost) |
| Protective Stops | ✅ Yes | ✅ Yes | ✅ Yes (account check) | ✅ Low |
| Target Orders | ✅ Yes | ✅ Yes | ✅ Yes (account check) | ✅ Low |

**Bottom Line:** Orders persist, but stop entry orders could duplicate if journal is lost. Protective orders are safe due to account checks.
