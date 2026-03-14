# Terminal Intent Hardening – Design Document

## Objective

A completed intent must be **terminal and non-actionable** everywhere. After an intent is completed, no module may place, modify, or manage protective orders for it. If any order activity or fill arrives for a completed intent, treat it as a critical anomaly and handle it explicitly.

---

## Completion Paths (Pre-Hardening)

| Path | Location | How TradeCompleted is set |
|------|----------|---------------------------|
| **Target fill** | NinjaTraderSimAdapter.NT HandleExecutionUpdateReal | RecordExitFill → entry.TradeCompleted = true |
| **Stop fill** | NinjaTraderSimAdapter.NT HandleExecutionUpdateReal | RecordExitFill → entry.TradeCompleted = true |
| **Broker flatten** | NinjaTraderSimAdapter.NT ProcessBrokerFlattenFill | RecordExitFill("FLATTEN") → entry.TradeCompleted = true |
| **Reconciliation broker flat** | ExecutionJournal.RecordReconciliationComplete | entry.TradeCompleted = true (direct) |
| **Reconciliation manual override** | ExecutionJournal (SetTradeCompletedManualOverride) | entry.TradeCompleted = true (direct) |
| **Slot expiry / stream stand-down** | StreamStateMachine.CommitJournal | No direct TradeCompleted; stream ends, journal committed |

**Canonical terminalization point**: The execution adapter (NinjaTraderSimAdapter) is the only component that can cancel broker orders. Therefore the **terminalization helper** lives in the adapter and is invoked from:
1. Exit fill handler (target/stop)
2. ProcessBrokerFlattenFill
3. Post-reconciliation callback (when reconciliation marks complete, adapter must terminalize)

---

## Terminalization Helper Contract

```
TerminalizeIntent(intentId, tradingDate, stream, completionReason, utcNow)
```

**Responsibilities:**
1. Mark intent terminal (journal already has TradeCompleted from RecordExitFill or reconciliation)
2. Cancel all remaining protective orders for that intent
3. Verify terminal invariant: no working stop/target orders remain
4. If working orders found → emit TERMINAL_INTENT_HAS_WORKING_ORDERS (critical)
5. Remove from BE/order management (implicit via TradeCompleted guard)
6. Log success/failure

**Order of operations on exit fill:**
1. **Late-fill check**: If journal already has TradeCompleted for this intent → emit COMPLETED_INTENT_RECEIVED_FILL, route to anomaly path, return (do not process as normal fill)
2. RecordExitFill (marks TradeCompleted)
3. OnExitFill (coordinator)
4. TerminalizeIntent (cancel protective orders, verify invariant)
5. Cancel opposite entry
6. CheckAndCancelEntryStopsOnPositionFlat
7. CheckAllInstrumentsForFlatPositions

---

## New Events

| Event | When | Severity |
|-------|------|----------|
| COMPLETED_INTENT_RECEIVED_FILL | Fill arrives for intent already TradeCompleted | CRITICAL |
| TERMINAL_INTENT_HAS_WORKING_ORDERS | After terminalization, working protective orders still exist | CRITICAL |
| TERMINAL_INTENT_VERIFIED | Terminalization completed, no working orders remain | INFO |
| COMPLETED_INTENT_ORDER_UPDATE | Order update (non-fill) for protective order whose intent is completed | WARN |

---

## BE Eligibility (Strengthened)

All must be true:
- TradeCompleted == false
- EntryFilled == true
- EntryFilledQuantityTotal > 0
- ExitFilledQuantityTotal < EntryFilledQuantityTotal (remaining open qty)
- !IsBEModified (existing)
- Intent in IntentMap (engine considers it live)
- No HasPendingBEForIntent (existing in RobotCore)

---

## Remaining Race Conditions (Broker/Exchange Timing)

1. **Late fill after cancel sent**: Broker may deliver fill callback after we've sent cancel. We cannot prevent this. Mitigation: COMPLETED_INTENT_RECEIVED_FILL surfaces it; reconciliation can detect QTY_MISMATCH.
2. **OCO cancel delay**: Sibling may fill before OCO processes cancel. Mitigation: TerminalizeIntent cancels explicitly; late fill triggers COMPLETED_INTENT_RECEIVED_FILL.
3. **Restart during fill**: Strategy restarts between fill and terminalization. Mitigation: Journal has TradeCompleted; on restart, BE excludes it; orphan scan can detect lingering orders.
