# NG Quantity Mismatch Investigation – 2026-03-13

## Summary

The robot traded the wrong amount for NG on 2026-03-13. Root cause: **a zombie stop order for NG1 filled ~3 hours after the NG1 trade had already completed**, creating an extra position that was never journaled. This led to `QTY_MISMATCH: account=3, journal=2` and the risk latch.

---

## Policy & Expected Behavior

- **Execution policy** (`configs/execution_policy.json`): NG/MNG `base_size=2`, `max_size=2`
- **Expected**: Each stream (NG1, NG2) trades up to 2 contracts per slot

---

## Timeline – 2026-03-13

### NG1 (intent `57ab60fda6303524`)

| Time (UTC) | Event |
|------------|-------|
| 14:00:08 | Entry OCO submitted: Long @ 3.319, Short @ 3.139, qty 2 each |
| 14:20:17 | **Partial fill 1** (Short @ 3.139) – `EXECUTION_PARTIAL_FILL`, `remaining_qty=1` |
| 14:20:17 | **Target hit** – `TRADE_COMPLETED`, entry_qty=1, exit_qty=1, completion_reason=TARGET |
| 14:20:18 | Stop 408453623488 (NG1) – Submitted, Working |
| 14:20:18 | Entry order 408453623464 – **Cancelled** (correct; OCO cancel after fill) |

**NG1 outcome**: 1 contract in, 1 out at target. Trade completed. Position flat.

### NG2 (intent `b52b94a0e17c00b8`)

| Time (UTC) | Event |
|------------|-------|
| 15:30:05 | Entry OCO submitted: Long @ 3.185, Short @ 3.116, qty 2 each |
| 16:40:21 | **Full fill 2** (Short @ 3.116) – `EXECUTION_FILLED`, fill_quantity=2 |
| 16:40:22 | Long leg cancelled (OCO) |
| — | NG2 position: 2 short, still open |

**NG2 outcome**: 2 contracts short, correctly filled and journaled.

---

## Bug: Zombie Stop Order for NG1

### What Happened

1. **14:20:17** – NG1 trade completed (1 in, 1 out at target). Position flat.
2. **14:20:18** – Stop 408453623488 (NG1) remained Working instead of being cancelled.
3. **16:40:40** – ~2.5 hours later:
   - Stop 408453623488 – CancelPending → Cancelled
   - **New stop 408453623772** at 3.14 (break-even) – Submitted, Working, **intent_id=57ab60fda6303524 (NG1)**

   A break-even stop was placed for NG1 even though NG1’s position was already closed.

4. **17:40:24** – Stop 408453623772 **Filled**.

   This was a stop for a closed trade. When it filled, it opened a new position (1 contract) that was never tied to any journal.

### Impact

- **Broker**: 3 contracts (2 short from NG2 + 1 from errant NG1 stop fill)
- **Journal**: 2 contracts (NG2 only; errant fill not journaled)
- **Risk latch**: `QTY_MISMATCH: account=3, journal=2`

---

## Root Cause

1. **NG1 stop not cancelled on target completion**  
   When the target hit at 14:20:17, the stop 408453623488 should have been cancelled immediately. It stayed Working until 16:40:40.

2. **Break-even logic ran for a completed trade**  
   At 16:40:40, break-even logic created stop 408453623772 for NG1 (intent 57ab60fda6303524) even though NG1 was already `TradeCompleted` and flat.

3. **Zombie stop filled**  
   Stop 408453623772 filled at 17:40:24, opening 1 contract that was never journaled.

---

## Secondary Issue: NG1 Partial Fill

- **Policy**: 2 contracts per stream
- **NG1**: Only 1 contract filled before target hit (~77 ms later)
- **Effect**: Under-traded by 1 contract for NG1 (market conditions, not a logic bug)

---

## Recommendations

1. **Immediate**: Manually reconcile the extra position (close or journal as appropriate).
2. ~~**Code fix**: Ensure that when a trade completes (target/stop hit), all bracket orders for that intent are cancelled immediately and no further bracket/break-even orders are placed for that intent.~~ **DONE**
3. ~~**Code fix**: Add a guard in break-even logic: do not place or adjust stops for intents in `TradeCompleted` state.~~ **DONE**
4. **Monitoring**: Treat `EXECUTION_UPDATE_UNKNOWN_ORDER` and similar warnings as signals to audit order lifecycle and completion handling.

---

## Fixes Applied

### Fix 1: TradeCompleted guard in GetActiveIntentsForBEMonitoring
- **Files**: `NinjaTraderSimAdapter.cs` (modules/robot/core, RobotCore_For_NinjaTrader, NT_ADDONS)
- **Change**: Skip intents where `journalEntry.TradeCompleted` is true. Prevents break-even logic from ever placing or modifying stops for completed trades.

### Fix 2: Cancel protective orders when target/stop fills
- **Files**: `NinjaTraderSimAdapter.NT.cs` (modules/robot/core, RobotCore_For_NinjaTrader)
- **Change**: When a protective stop or target fills, call `CancelProtectiveOrdersForIntent(intentId)` to cancel the sibling order. Defense in depth – OCO should cancel it, but if OCO fails or is delayed, we explicitly cancel to prevent zombie stops.

---

## Terminal Intent Hardening (Upgrade Pass)

A full lifecycle hardening pass was implemented. See `docs/robot/incidents/2026-03-13_TERMINAL_INTENT_HARDENING_DESIGN.md`.

### Summary of Hardening

| Upgrade | Implementation |
|---------|----------------|
| **Late-fill protection** | Before processing protective fill, check `IsIntentCompleted`. If true → emit `COMPLETED_INTENT_RECEIVED_FILL`, notification, return (do not process). |
| **Terminalization helper** | `TerminalizeIntent(intentId, tradingDate, stream, completionReason, utcNow)` – cancels protective orders, verifies invariant, logs `TERMINAL_INTENT_VERIFIED` or `TERMINAL_INTENT_HAS_WORKING_ORDERS`. |
| **BE eligibility** | Added `ExitFilledQuantityTotal >= EntryFilledQuantityTotal` guard – exclude intents with no remaining open quantity. |
| **IsIntentCompleted** | `ExecutionJournal.IsIntentCompleted(intentId, tradingDate, stream)` for late-fill and orphan checks. |
| **New events** | `COMPLETED_INTENT_RECEIVED_FILL`, `TERMINAL_INTENT_HAS_WORKING_ORDERS`, `TERMINAL_INTENT_VERIFIED` |

### Remaining Race Conditions (Broker/Exchange Timing)

1. **Late fill after cancel sent**: Broker may deliver fill callback after we've sent cancel. Cannot prevent. Mitigation: `COMPLETED_INTENT_RECEIVED_FILL` surfaces it; reconciliation detects `QTY_MISMATCH`.
2. **OCO cancel delay**: Sibling may fill before OCO processes cancel. Mitigation: `TerminalizeIntent` cancels explicitly; late fill triggers `COMPLETED_INTENT_RECEIVED_FILL`.
3. **Restart during fill**: Strategy restarts between fill and terminalization. Mitigation: Journal has `TradeCompleted`; on restart, BE excludes it; orphan scan can detect lingering orders.

### Test

```bash
dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test TERMINAL_INTENT
```

---

## References

- `logs/robot/robot_MNG.jsonl` – lines 795–861
- `data/execution_journals/2026-03-13_NG*.json`
- `data/risk_latches/risk_latch_DEMO4338364-2_MNG.json`
- `configs/execution_policy.json` – NG/MNG base_size=2, max_size=2
