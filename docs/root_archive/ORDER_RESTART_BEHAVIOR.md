# Order Behavior on Restart/Shutdown

## Quick Answer

**Orders persist in NinjaTrader** - they survive restarts and shutdowns. However, there's a **potential gap** for stop entry orders.

---

## What Happens

### ✅ Orders Persist
- NinjaTrader orders are stored in the broker/account system
- They remain active even if:
  - Strategy restarts
  - NinjaTrader closes and reopens
  - Computer restarts (if NT auto-starts)

### ⚠️ Stop Entry Orders (Breakout Orders)

**Current Protection:**
- ✅ Checks execution journal: `IsIntentSubmitted()`
- ✅ Checks stream journal: `StopBracketsSubmittedAtLock = true`
- ⚠️ **Does NOT check `account.Orders` for existing orders**

**Risk:**
- If journal is lost but orders exist → **may try to place duplicates**
- OCO group should prevent both from filling, but duplicate orders waste resources

**What Should Happen:**
- Check `account.Orders` for existing orders with matching OCO group
- Reuse if found (like protective orders do)

### ✅ Protective Stop-Loss Orders

**Has Full Protection:**
```csharp
// Checks account.Orders for existing orders
var existingStop = account.Orders.FirstOrDefault(o =>
    GetOrderTag(o) == stopTag &&
    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));

if (existingStop != null)
{
    // Reuse existing order
    return SuccessResult(existingStop.OrderId);
}
```

**Result:** ✅ Safe - no duplicates even if journal is lost

### ✅ Target Orders

**Has Full Protection:**
- Same pattern as protective stops
- Checks `account.Orders` before creating
- Reuses existing orders

---

## Scenarios

### Scenario 1: Strategy Restart (NT Running)

**Stop Entry Orders:**
- Journal intact → checks `StopBracketsSubmittedAtLock = true`
- ✅ Skips placement (no duplicates)

**Protective Orders:**
- Checks `account.Orders` → finds existing
- ✅ Reuses them

**Result:** ✅ Safe

### Scenario 2: NT Shutdown & Restart

**Stop Entry Orders:**
- Journal persists (file-based)
- Orders persist in account
- ✅ Should skip (journal check)

**Protective Orders:**
- Checks `account.Orders` → finds existing
- ✅ Reuses them

**Result:** ✅ Safe (if journal intact)

### Scenario 3: Journal Lost

**Stop Entry Orders:**
- Journal missing → `StopBracketsSubmittedAtLock = false`
- Orders still exist in account
- ⚠️ **May try to place duplicates** (no account check)

**Protective Orders:**
- Checks `account.Orders` → finds existing
- ✅ Reuses them

**Result:** ⚠️ Stop entry orders may duplicate

---

## Current Implementation

### Stop Entry Orders (Gap)

```csharp
// Only checks journal - no account check
if (_executionJournal != null && 
    (_executionJournal.IsIntentSubmitted(longIntentId, TradingDate, Stream) ||
     _executionJournal.IsIntentSubmitted(shortIntentId, TradingDate, Stream)))
{
    // Skip - already submitted
    return;
}
// Missing: Check account.Orders for existing orders
```

### Protective Orders (Safe)

```csharp
// Checks account.Orders first
var existingStop = account.Orders.FirstOrDefault(o =>
    GetOrderTag(o) == stopTag &&
    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));

if (existingStop != null)
{
    // Reuse existing order
    return SuccessResult(existingStop.OrderId);
}
// Then creates if not found
```

---

## Recommendations

1. **Keep Journals Safe** - They prevent duplicates for stop entry orders
2. **Monitor Order Count** - Check NT order management after restart
3. **Add Account Check** - Should add `account.Orders` check for stop entry orders (like protective orders)
4. **Use OCO Groups** - Prevents both sides from filling simultaneously

---

## Summary

| Order Type | Survives Restart? | Survives NT Shutdown? | Idempotency? | Risk |
|------------|-------------------|----------------------|--------------|------|
| Stop Entry Orders | ✅ Yes | ✅ Yes | ⚠️ Journal only | ⚠️ Medium (if journal lost) |
| Protective Stops | ✅ Yes | ✅ Yes | ✅ Account check | ✅ Low |
| Target Orders | ✅ Yes | ✅ Yes | ✅ Account check | ✅ Low |

**Bottom Line:** Orders persist and continue working. Stop entry orders rely on journal for idempotency (could duplicate if journal lost). Protective orders are fully protected with account checks.
