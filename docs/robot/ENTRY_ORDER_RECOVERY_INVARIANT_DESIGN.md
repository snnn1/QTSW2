# Entry Order Recovery: Invariant-Based Design

## Objective

For every stream that is RANGE_LOCKED and flat, enforce:

**There must be exactly one valid entry-order set on the broker, or zero orders with an explicitly tracked recovery action that is allowed to rebuild one clean set.**

Never allow: partial sets, duplicate sets, or silent divergence between journal state and broker state.

---

## Recovery Action Model

| Action | When | Behavior |
|--------|------|----------|
| `None` | Valid set present or position exists | No action |
| `ResubmitClean` | Missing set | Submit one clean entry-order set |
| `CancelAndRebuild` | Broken set | Cancel all entry orders for stream, wait for confirmation, submit one clean set |

State stored: `Action`, `Reason`, `IssuedUtc`, `LastClassificationUtc`, `LastClassificationResult`.
Persisted via StreamJournal: `RecoveryAction`, `RecoveryActionReason`, `RecoveryActionIssuedUtc`.

---

## Valid Entry-Order Set Definition (Strict)

All of the following must hold:

1. Stream is RANGE_LOCKED
2. Position is flat
3. Both expected entry orders exist: long and short
4. Both orders for expected instrument
5. Both match expected intent IDs/tags
6. Both in WorkingOrders (implicit: broker snapshot only includes working orders)
7. Both match expected breakout prices (StopPrice)
8. Both belong to expected entry structure for that stream
9. No duplicate sibling entry orders (exactly 1 long, 1 short)
10. Protective orders excluded (:STOP, :TARGET)
11. OCO linkage: both share same OcoGroup (if OCO is design)

If any fails → set is **not valid**.

---

## Broker-State Classification

| Classification | Definition |
|----------------|------------|
| `PositionExists` | Instrument position ≠ 0 or entry already detected |
| `ValidSetPresent` | Exactly one correct entry-order set exists |
| `MissingSet` | No valid entry orders, no malformed remnants |
| `BrokenSet` | Partial, duplicate, wrong-price, rejected remnants, OCO mismatch |

---

## Two-Phase Reconciliation

### Phase A: Audit/Classify (during recovery)

- Inspect broker snapshot
- Classify each eligible stream
- Assign recovery action
- Log classification
- **Do NOT execute** — broker state may still be settling

### Phase B: Execute (next safe engine cycle)

- Re-check current broker reality
- Run pre-execution gate
- Execute stored recovery action only if conditions permit

---

## Pre-Execution Gate

Before any recovery action:

1. Stream still RANGE_LOCKED
2. Stream not committed
3. Entry not detected
4. Position still flat
5. Market/session valid (not at/past close)
6. No valid set exists if action is ResubmitClean
7. Broker confirmed broken orders gone if action is CancelAndRebuild

If any fails → skip action, clear/downgrade, log why.

---

## Cancel Confirmation Before Rebuild

For Broken set:

1. Classify as CancelAndRebuild
2. Cancel all entry orders for that stream only
3. **Wait for broker confirmation** that none are still working
4. Only then submit one clean replacement set

---

## New Event Types

- `ENTRY_ORDER_SET_VALID`
- `ENTRY_ORDER_SET_MISSING`
- `ENTRY_ORDER_SET_BROKEN`
- `ENTRY_ORDER_SET_DUPLICATE_DETECTED`
- `ENTRY_ORDER_SET_CANCEL_REQUESTED`
- `ENTRY_ORDER_SET_CANCEL_CONFIRMED`
- `ENTRY_ORDER_SET_REBUILD_REQUESTED`
- `ENTRY_ORDER_SET_RESUBMITTED`
- `ENTRY_ORDER_SET_RESUBMIT_SKIPPED_POSITION_EXISTS`
- `ENTRY_ORDER_SET_RESUBMIT_SKIPPED_VALID_EXISTS`
- `ENTRY_ORDER_SET_REBUILD_BLOCKED_CANCEL_INCOMPLETE`
- `ENTRY_ORDER_ACTION_CLEARED_STREAM_INELIGIBLE`
- `ENTRY_ORDER_INVARIANT_VIOLATED`

---

## Files Changed (modules/robot/core — IMPLEMENTED)

- `modules/robot/core/EntryOrderRecoveryTypes.cs` (new)
- `modules/robot/core/JournalStore.cs` (RecoveryAction, RecoveryActionReason, RecoveryActionIssuedUtc)
- `modules/robot/core/StreamStateMachine.cs` (classifier, two-phase, recovery state, strict HasValidEntryOrdersOnBroker)
- `modules/robot/core/RobotEventTypes.cs` (new events)
- `modules/robot/core/Execution/IExecutionAdapter.cs` (CancelOrders for stream-scoped cancel)
- `modules/robot/core/Execution/NullExecutionAdapter.cs`, `NinjaTraderSimAdapter.cs`, `NinjaTraderSimAdapter.NT.cs`, `NinjaTraderSimAdapter.Stubs.cs`, `NinjaTraderLiveAdapter.cs` (CancelOrders)
- `modules/robot/core/Tests/ForcedFlattenSlotExpiryReentryAlignmentTests.cs` (CancelOrders mock)

## Pending Sync

- **NT_ADDONS**: Apply same design (EntryOrderRecoveryTypes, JournalStore, StreamStateMachine, RobotEventTypes, adapters)
- **RobotCore_For_NinjaTrader**: Same

## Test Results

- `ORDER_RECONCILIATION` tests: PASS

## Remaining Race-Condition Risks

- Cancel-and-rebuild: One-cycle delay between cancel and rebuild; broker may not have confirmed cancel before next tick. Mitigation: ExecutePendingRecoveryAction checks orderIds.Count > 0 before rebuild; if orders remain, we send cancel again and wait.
- Late fill between Phase A and Phase B: Mitigated by pre-execution gate (position check).

## Temporary Compatibility

- `ReconcileEntryOrders` retained as wrapper delegating to `AuditAndClassifyEntryOrders` for backward compatibility.
