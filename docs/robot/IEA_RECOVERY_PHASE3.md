# IEA Recovery Phase 3: Deterministic Recovery and Incident-State Control

## Overview

Phase 3 adds an instrument-scoped recovery state model on top of the IEA position authority, terminal intent hardening, and order ownership system. When broker state, registry state, journal state, and intent state diverge, the engine enters deterministic recovery mode instead of continuing normal behavior opportunistically.

**Core principle:** Normal execution mode and recovery mode are explicitly separated. Normal mode assumes invariants hold. Recovery mode assumes invariants may be broken; no module may continue normal order/BE/protective management until recovery resolution is complete.

## Recovery States

| State | Description |
|-------|-------------|
| NORMAL | All invariants satisfied; normal execution allowed |
| RECOVERY_PENDING | Anomaly detected; recovery requested; reconstruction queued |
| RECONSTRUCTING | Gathering broker/registry/journal/intent truth |
| RECOVERY_ACTION_REQUIRED | Deterministic decision needed |
| FLATTENING | Recovery decided exposure must be removed; flatten in progress |
| HALTED | Unsafe to continue automatically; operator action required |
| RESOLVED | Recovery completed; waiting to re-enter NORMAL |

## Valid State Transitions

```
NORMAL → RECOVERY_PENDING
RECOVERY_PENDING → RECONSTRUCTING | RECOVERY_ACTION_REQUIRED
RECONSTRUCTING → RECOVERY_ACTION_REQUIRED
RECOVERY_ACTION_REQUIRED → FLATTENING | HALTED | RESOLVED
FLATTENING → RECONSTRUCTING | HALTED | RESOLVED
RESOLVED → NORMAL | RECONSTRUCTING
HALTED → RECONSTRUCTING
```

Invalid transitions emit `RECOVERY_STATE_TRANSITION_INVALID`.

## Recovery Triggers

All events that move an instrument from NORMAL into recovery:

| Trigger | Source | Replaced Path |
|---------|--------|---------------|
| EXECUTION_UNOWNED | Execution update path | IncrementUnownedDetected |
| UNOWNED_LIVE_ORDER_DETECTED | IEA ScanAndAdoptExistingProtectives | Direct NtFlattenInstrumentCommand |
| MANUAL_OR_EXTERNAL_ORDER_DETECTED | HandleOrderUpdateReal | Direct NtFlattenInstrumentCommand |
| REGISTRY_BROKER_DIVERGENCE | VerifyRegistryIntegrity | Event only (now triggers recovery) |
| RECONCILIATION_QTY_MISMATCH | ReconciliationRunner | StandDownStreamsForInstrument + freeze |
| COMPLETED_INTENT_RECEIVED_FILL | Execution update | Event + notification |
| TERMINAL_INTENT_HAS_WORKING_ORDERS | TerminalizeIntent | CancelProtectiveOrdersForIntent |
| IEA_ENQUEUE_FAILURE | EnqueueAndWait callback | StandDownStreamsForInstrument |

## RequestRecovery

```
RequestRecovery(instrument, reason, context, utcNow)
```

- Sets recovery state to RECOVERY_PENDING if not already active
- Attaches reason/context
- Suppresses normal management for that instrument
- Invokes callback to run deterministic reconstruction
- If recovery already active: merges/escalates reason instead of parallelizing

Events: `RECOVERY_REQUEST_RECEIVED`, `RECOVERY_ALREADY_ACTIVE`, `RECOVERY_ESCALATED`

## Suppression During Recovery

While an instrument is in any non-NORMAL recovery state:

- **Blocked:** BE management, protective replacement/modification, normal order submission, slot-expiry management, intent progression
- **Allowed:** Recovery reconstruction, explicit recovery flatten actions, anomaly logging, controlled journal/registry updates

Event when blocked: `RECOVERY_GUARD_BLOCKED_NORMAL_MANAGEMENT`

## Reconstruction Inputs

Four views gathered and compared:

| View | Source |
|------|--------|
| A. Broker/account truth | Live account position, live broker working orders |
| B. Order registry truth | Owned/adopted/unowned live orders, lifecycle states |
| C. Journal truth | Open/closed intents, entry/exit filled quantities, TradeCompleted |
| D. Runtime intent truth | Active intents, BE eligibility, terminal flags |

## Reconstruction Classifications

| Classification | Meaning |
|----------------|---------|
| CLEAN | All views consistent |
| POSITION_ONLY_MISMATCH | Broker qty ≠ journal qty |
| LIVE_ORDER_OWNERSHIP_MISMATCH | Unowned live orders or registry/broker divergence |
| JOURNAL_BROKER_MISMATCH | Reconciliation qty mismatch |
| TERMINALITY_MISMATCH | Terminal intent has working orders |
| MANUAL_INTERVENTION_DETECTED | Manual/external order detected |
| ADOPTION_POSSIBLE | Restart-safe adopted protectives |
| UNSAFE_AMBIGUOUS_STATE | Cannot prove safe automated action |

## Recovery Decisions

| Decision | When | Action |
|----------|------|--------|
| RESUME | CLEAN | Transition to RESOLVED → NORMAL |
| ADOPT | ADOPTION_POSSIBLE | Adopt protectives; transition to RESOLVED |
| FLATTEN | LIVE_ORDER_OWNERSHIP_MISMATCH, JOURNAL_BROKER_MISMATCH, TERMINALITY_MISMATCH, MANUAL_INTERVENTION_DETECTED | Route through IEA RequestFlatten |
| HALT | UNSAFE_AMBIGUOUS_STATE | Transition to HALTED; operator action required |

## Recovery Flatten

When decision is FLATTEN:

- Route through canonical IEA `RequestFlatten` path
- State moves to FLATTENING
- After flatten resolution, `OnRecoveryFlattenResolved` transitions to RESOLVED
- Reconstruction may run again to verify state

Events: `RECOVERY_FLATTEN_REQUESTED`, `RECOVERY_FLATTEN_RESOLVED`

## Resume Criteria

`CanResumeNormalExecution(instrument)` returns true only when:

- State is RESOLVED
- No flatten latch held for instrument
- No unowned live orders for instrument
- No terminal intent has working protective orders

## Halt Policy

When recovery cannot safely choose RESUME, ADOPT, or FLATTEN:

- Transition to HALTED
- Emit `RECOVERY_HALTED`, `RECOVERY_OPERATOR_ACTION_REQUIRED`
- Preserve forensic context
- Require explicit configured policy or operator action to clear

## Relation to Position Authority and Order Ownership

- **Phase 1:** What orders exist and who owns them (OrderRegistry)
- **Phase 2:** How we maintain ownership integrity (adoption, unowned handling)
- **Phase 3:** When reality and internal state diverge, deterministic recovery without unsafe opportunistic behavior

Recovery uses the same IEA flatten authority; it does not bypass it.

## Remaining Limitations / Race Conditions

- Reconstruction runs synchronously in callback; long-running journal reads may block
- Multi-instrument recovery: each instrument is scoped independently; account-level halt not yet implemented
- Disconnect/reconnect: ownership truth may be uncertain during sync window
- Recovery context persistence: structured logs only; no lightweight persisted incident record yet

## State Transition Table

| From | To | Condition |
|------|-----|-----------|
| NORMAL | RECOVERY_PENDING | RequestRecovery called |
| RECOVERY_PENDING | RECONSTRUCTING | Callback starts reconstruction |
| RECOVERY_PENDING | RECOVERY_ACTION_REQUIRED | Reconstruction result received |
| RECONSTRUCTING | RECOVERY_ACTION_REQUIRED | Reconstruction complete |
| RECOVERY_ACTION_REQUIRED | FLATTENING | Decision = FLATTEN |
| RECOVERY_ACTION_REQUIRED | HALTED | Decision = HALT |
| RECOVERY_ACTION_REQUIRED | RESOLVED | Decision = RESUME or ADOPT |
| FLATTENING | RECONSTRUCTING | Flatten sent; re-verify |
| FLATTENING | HALTED | Flatten failed persistent |
| FLATTENING | RESOLVED | Flatten fill received |
| RESOLVED | NORMAL | CanResumeNormalExecution true |
| RESOLVED | RECONSTRUCTING | Re-verify requested |
| HALTED | RECONSTRUCTING | Operator/retry |

## Recovery Decision Table

| Classification | Decision |
|----------------|----------|
| CLEAN | RESUME |
| ADOPTION_POSSIBLE | ADOPT (if broker qty ≠ 0) else RESUME |
| POSITION_ONLY_MISMATCH | FLATTEN if unowned live > 0 else RESUME |
| LIVE_ORDER_OWNERSHIP_MISMATCH | FLATTEN |
| JOURNAL_BROKER_MISMATCH | FLATTEN |
| TERMINALITY_MISMATCH | FLATTEN |
| MANUAL_INTERVENTION_DETECTED | FLATTEN |
| UNSAFE_AMBIGUOUS_STATE | HALT |

## Centralized / Replaced Module-Level Recovery Actions

| Module | Previous Behavior | Phase 3 Replacement |
|--------|-------------------|---------------------|
| IEA.NT ScanAndAdoptExistingProtectives | Enqueue NtFlattenInstrumentCommand on UNOWNED_LIVE_ORDER | RequestRecovery |
| IEA OrderRegistryPhase2 VerifyRegistryIntegrity | Emit REGISTRY_BROKER_DIVERGENCE only | RequestRecovery |
| RobotEngine onQuantityMismatch | StandDownStreamsForInstrument | StandDownStreamsForInstrument + RequestRecoveryForInstrument |
| NinjaTraderSimAdapter MANUAL_OR_EXTERNAL_ORDER | Enqueue NtFlattenInstrumentCommand | RequestRecovery |
| NinjaTraderSimAdapter ORPHAN_FILL_INTENT_NOT_FOUND | Enqueue NtFlattenInstrumentCommand | RequestRecoveryForInstrument |

Note: UNKNOWN_ORDER_FILL, UNTRACKED_FILL, INTENT_NOT_FOUND_AFTER_CONTEXT, and FailClosed protective paths still enqueue NtFlattenInstrumentCommand directly. These can be wired through RequestRecovery in a follow-up.

## Audit Mapping

See `docs/robot/IEA_RECOVERY_PHASE3_AUDIT.md` for current path → Phase 3 replacement mapping.
