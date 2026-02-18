# Entry Aggregation — Edge Cases Assessment

Assessment of the current implementation against the three required edge cases and logging spec.

**Status (post-implementation)**: All P0/P1/P2 fixes implemented. Build verified.

---

## 1. Partial Fill of Aggregated Entry

### Spec Requirements

| Requirement | Status | Current Behavior |
|-------------|--------|------------------|
| **Allocate as fills arrive, deterministic order** | ❌ **GAP** | Allocation is **stateless** and **order-dependent** |
| **First fill → intentIds[0] until satisfied, then next** | ❌ **GAP** | Each fill uses `allocPerIntent = fillQty / count`, `remainder = fillQty % count`. No cumulative tracking. |
| **Protective bracket covers current filled qty** | ✅ **OK** | `HandleEntryFill` uses `totalFilledQuantity`; `SubmitProtectiveStop` cancels/recreates when qty changes |
| **Journal: EntryFilledQuantityTotal increments** | ✅ **OK** | `RecordEntryFill` accumulates: `EntryFilledQuantityTotal += fillQuantity` |
| **EntryFilled = true on first allocation** | ✅ **OK** | Journal sets `EntryFilled = true` when first fill recorded |
| **No uncovered gap** | ⚠️ **RISK** | Allocation bug can misattribute qty → journal/coordinator mismatch |

### Current Allocation Logic (Bug)

```csharp
// NinjaTraderSimAdapter.NT.cs ~1908
allocPerIntent = fillQuantity / intentIdsToUpdate.Count;
remainder = fillQuantity % intentIdsToUpdate.Count;
for (i = 0; i < count; i++)
    allocQty = allocPerIntent + (i < remainder ? 1 : 0);
```

**Example**: NG1 + NG2, 2-contract aggregated order, partial fills 1 + 1:

- **Fill 1**: allocPerIntent=0, remainder=1 → intent[0] gets 1, intent[1] gets 0 ✓
- **Fill 2**: Same formula → intent[0] gets 1 again, intent[1] gets 0 ✗

**Result**: Intent[0] ends up with 2, intent[1] with 0. Wrong.

**Root cause**: No tracking of cumulative allocated qty per intent. Need `OrderInfo.AggregatedFilledByIntent` or read from journal before allocating.

**Recommended fix**: Before allocating, compute `remainingNeededPerIntent = policyQty - journal.EntryFilledQuantityTotal(intentId)`. Allocate fill qty to intents in lexicographic order, filling each until `remainingNeededPerIntent` is satisfied.

### Coordinator Double-Count (Bug)

```csharp
// Lines 1910-1937: Loop allocates and calls OnEntryFill(allocContext.IntentId, allocQty, ...)
// Lines 1974-1975: Then calls OnEntryFill(intentId, fillQuantity, ...) for primary
```

For aggregated orders, the loop already calls `OnEntryFill` for each intent with `allocQty`. The second call `OnEntryFill(intentId, fillQuantity)` adds `fillQuantity` (total) to the primary intent again → **double-count**.

**Fix**: For aggregated (`intentIdsToUpdate.Count > 1`), skip the second `OnEntryFill` call at line 1974.

---

## 2. Aggregation Transaction Atomicity

### Spec Requirements

| Step | On Failure | Current Behavior |
|------|------------|------------------|
| **Cancel existing orders** | Fail-closed, log `failed_step=CANCEL_*` | ❌ Single `try/catch`; no step identification |
| **Submit aggregated order** | Fail-closed | ❌ Same catch block |
| **Resubmit opposite-side orders** | Log, assess risk | ❌ Same catch block |
| **action_taken** | STAND_DOWN / FLATTEN / RETRY | ❌ Only logs `error`, `existing_intents`, generic note |
| **exposure_at_failure** | Account qty at failure | ❌ Not logged |

### Current Implementation

```csharp
try {
    // 1. Cancel existing
    account.Cancel(ordersToCancel.ToArray());
    // 2. Cancel current's opposite
    account.Cancel(new[] { oppOrd });
    // 3-4. Create and submit aggregated order
    var order = account.CreateOrder(...);
    account.Submit(ordersToSubmit.ToArray());
    // ...
} catch (Exception ex) {
    _log.Write(..., "ENTRY_AGGREGATION_FAILED", new {
        error = ex.Message,
        existing_intents = ...,
        note = "Aggregation failed - falling back..."
    });
    return OrderSubmissionResult.FailureResult(...);
}
```

**Gaps**:

1. **No step identification**: Cannot tell if cancel, submit, or resubmit failed.
2. **No fail-closed on cancel failure**: If cancel fails, we may have left orders in inconsistent state; we return failure but don't flatten.
3. **No exposure_at_failure**: If we had partial exposure before aggregation (unlikely) or during a retry, we don't log it.
4. **No action_taken**: Spec wants explicit STAND_DOWN / FLATTEN / RETRY.

**Recommended**: Wrap each logical step in its own try/catch or check, log `failed_step`, `nt_error`, `action_taken`, `exposure_at_failure`. On cancel failure after any cancel succeeded → consider flatten if account has position.

---

## 3. Aggregation Eligibility Scope

### Spec Requirements

| Requirement | Status | Current Behavior |
|-------------|--------|------------------|
| **Same instrument, price, direction** | ✅ | Enforced |
| **Same stop distance / protective params** | ❌ **GAP** | Not checked |
| **Same target distance** | ❌ **GAP** | Not checked |
| **Same session/trading_date** | ⚠️ **Partial** | `currentIntent.TradingDate` used for OCO; not explicitly compared |
| **If different → do not aggregate** | ❌ | Would aggregate NG1 (stop 3.169) + NG2 (stop 3.086) |

### Current Eligibility Check

```csharp
// TryAggregateWithExistingOrders - only checks:
if (other.Direction != direction) continue;
if (other.EntryPrice != stopPrice) continue;
// instrument match
if (orderInfo.State != "SUBMITTED" && ... != "WORKING") continue;
```

**Missing**: `StopPrice`, `TargetPrice`, `TradingDate`, `Session` agreement.

**Risk**: NG1 (stop 3.169, target 2.988) and NG2 (stop 3.086, target 2.988) would aggregate. One protective bracket uses the primary intent's stop/target. The other stream's risk profile is not matched.

**Recommended**: Add guard:

```csharp
if (other.StopPrice != currentIntent.StopPrice) continue;
if (other.TargetPrice != currentIntent.TargetPrice) continue;
if (other.TradingDate != currentIntent.TradingDate) continue;
```

If aggregation with different params is ever desired, use merged policy (e.g. tightest stop, smallest target) and log `eligibility_compromise = true` with the chosen values.

---

## 4. Logging Enhancements

### ENTRY_AGGREGATION_ATTEMPT

| Field | Spec | Current |
|-------|------|---------|
| execution_instrument | ✓ | ❌ Missing |
| direction | ✓ | ✓ |
| price | ✓ | ✓ (stop_price) |
| intent_ids | ✓ | ✓ (current_intent, existing_intents) |
| qty_per_intent + total_qty | ✓ | ❌ Only total_quantity |
| eligibility_passed + reason | ✓ | ❌ Not applicable (no eligibility check) |

### ENTRY_AGGREGATION_SUCCESS

| Field | Spec | Current |
|-------|------|---------|
| agg_tag | ✓ | ❌ Missing |
| agg_order_id | ✓ | ✓ (broker_order_id) |
| oco_group | ✓ | ✓ |
| replaced_order_ids | ✓ | ❌ Missing |
| resubmitted_order_ids | ✓ | ❌ Missing |

### ENTRY_AGGREGATION_FAILED

| Field | Spec | Current |
|-------|------|---------|
| failed_step | ✓ | ❌ Missing |
| nt_error | ✓ | ✓ (error) |
| action_taken | ✓ | ❌ Missing |
| exposure_at_failure | ✓ | ❌ Missing |

### AGG_ENTRY_FILL_ALLOCATED (New Event)

| Field | Spec | Current |
|-------|------|---------|
| fill_qty | ✓ | ❌ No dedicated event |
| allocations: {intentId: qty} | ✓ | ❌ |
| cumulative_qty_per_intent | ✓ | ❌ |

**Current**: Allocation is done inside the fill handler with no dedicated log. EXECUTION_PARTIAL_FILL / EXECUTION_FILLED exist but don't include per-intent allocation breakdown.

---

## Summary: Priority Fixes

| Priority | Item | Effort | Risk if Unfixed |
|----------|------|--------|-----------------|
| **P0** | Partial fill allocation (deterministic, cumulative) | Medium | Journal/coordinator mismatch, wrong P&L |
| **P0** | Coordinator double-count for aggregated | Low | Overstated exposure, wrong risk |
| **P1** | Eligibility guard (stop/target/trading_date) | Low | One bracket doesn't match both streams |
| **P1** | Transaction atomicity (step-level, fail-closed) | Medium | Orphan orders, inconsistent state |
| **P2** | Logging enhancements | Low | Harder diagnostics |

---

## References

- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`: `TryAggregateWithExistingOrders`, fill handling ~1906-1986
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs`: `HandleEntryFill`
- `RobotCore_For_NinjaTrader/Execution/ExecutionJournal.cs`: `RecordEntryFill`
- `docs/robot/NG1_NG2_SAME_PRICE_INCIDENT_ANALYSIS.md`
