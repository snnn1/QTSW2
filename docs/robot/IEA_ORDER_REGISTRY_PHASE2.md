# IEA Order Registry: Phase 2 Design Note

## Overview

Phase 2 extends the IEA Order Registry (Phase 1) with ownership policy, restart recovery, anomaly classification, registry lifecycle discipline, and observability. Every live broker order is either OWNED or ADOPTED; unknown orders are detected immediately and handled via fail-closed policy.

## Ownership Model

| Status | Meaning |
|--------|---------|
| **OWNED** | Submitted by current runtime |
| **ADOPTED** | Discovered and intentionally taken over (e.g. restart protectives via ScanAndAdoptExistingProtectives) |
| **UNOWNED** | Known anomaly, not under robot ownership (manual, external, or orphan) |
| **TERMINAL** | Completed/canceled/rejected, no longer live |

## Restart Adoption Rules

On first execution update after restart, `ScanAndAdoptExistingProtectives` runs:

1. **Query** `Account.Orders` and `Account.Positions` (via NinjaTrader API)
2. **Detect** working stop/target orders with QTSW2 tag
3. **Match** to active intents (journal says entry filled)
4. **If match**: Register with `OrderOwnershipStatus.ADOPTED`, `OrderRole.STOP` or `TARGET`, `LifecycleState.WORKING`, `SourceContext = "RESTART_ADOPTION"`
5. **If no match**: Emit `UNOWNED_LIVE_ORDER_DETECTED`, register as UNOWNED, request flatten (fail-closed)

### Adoption Matching Criteria (Narrow — All Required)

| Criterion | Requirement |
|-----------|-------------|
| Tag | `tag.StartsWith("QTSW2:")` |
| Leg | `tag.EndsWith(":STOP")` or `tag.EndsWith(":TARGET")` — no entry orders |
| IntentId | `DecodeIntentId(tag)` non-empty |
| Active intent | `intentId` in `GetActiveIntentsForBEMonitoring` (journal says entry filled, intent not completed) |
| Order state | `Working` or `Accepted` |
| Not already tracked | `!OrderMap.TryGetValue(mapKey, out _)` |

**Exclusions**: Orders without QTSW2 tag are ignored. QTSW2 entry orders (no :STOP/:TARGET) are not adopted. Intent not in active set → UNOWNED.

## Manual Order Policy

Orders that appear without being registered or adopted:

- **Source**: NinjaTrader UI, ATM strategies, broker actions, other strategies
- **Classification**: `OrderOwnershipStatus.UNOWNED`, `OrderRole.EXTERNAL`
- **Event**: `MANUAL_OR_EXTERNAL_ORDER_DETECTED`
- **Policy** (default): Fail-closed flatten
- **Alternative** (configurable): Log only (not implemented in Phase 2)

## Lifecycle Diagram

```
CREATED → SUBMITTED → WORKING → PART_FILLED → FILLED (terminal)
                ↓                    ↓
            REJECTED (terminal)   CANCELED (terminal)
```

**Allowed transitions** (enforced by `ValidateLifecycleTransition`):

- CREATED → SUBMITTED
- SUBMITTED → WORKING | FILLED | REJECTED (FILLED for fast fill before WORKING)
- WORKING → PART_FILLED | FILLED | CANCELED
- PART_FILLED → FILLED | CANCELED
- FILLED, CANCELED, REJECTED → (terminal, no further transitions)

**Invalid transition** → Emit `ORDER_LIFECYCLE_TRANSITION_INVALID`, reject update.

## Anomaly Events

| Event | When |
|-------|------|
| `UNOWNED_LIVE_ORDER_DETECTED` | Restart scan: QTSW2 protective with no matching active intent |
| `MANUAL_OR_EXTERNAL_ORDER_DETECTED` | Order update for order not in registry |
| `ORDER_LIFECYCLE_TRANSITION_INVALID` | Illegal lifecycle transition attempted |
| `EXECUTION_UNOWNED` | Execution update for order not in registry |
| `REGISTRY_BROKER_DIVERGENCE` | Registry vs broker mismatch (integrity check) |

## Direct vs Alias Lookup

- **Direct**: `TryResolveByBrokerOrderId(brokerOrderId)` — primary path
- **Alias**: `TryResolveByAlias(intentId)`, `intentId:STOP`, `intentId:TARGET` — compatibility
- **Resolution path** logged: DirectId, Alias, Adopted, Unresolved

## Registry Retention / Cleanup

- **Terminal retention window**: 10 minutes (configurable constant)
- **Cleanup**: Remove entries where `LifecycleState` is FILLED/CANCELED/REJECTED and `TerminalUtc` is older than retention
- **Exclusion**: Do not remove if intent is still active (referenced by intent state)
- **Trigger**: Periodic (IEA heartbeat, ~60s)

**Forensic note**: 10 min in-memory retention is for operational hygiene. Event trail (`ORDER_REGISTRY_*`, anomaly events) persists in logs. Phase 3 may add configurable retention or optional persistence of terminal entries for audit.

## Registry Integrity Verification

- **Trigger**: Periodic (IEA heartbeat)
- **Checks**:
  1. Every WORKING registry order exists in `Account.Orders`
  2. Every live broker order exists in registry
- **On mismatch**: Emit `REGISTRY_BROKER_DIVERGENCE` with direction (`registry_has_broker_missing` or `broker_has_registry_missing`)

## Registry Metrics

| Metric | Description |
|--------|-------------|
| `owned_orders_active` | WORKING orders with OWNED status |
| `adopted_orders_active` | WORKING orders with ADOPTED status |
| `terminal_orders_recent` | TERMINAL orders in registry |
| `unowned_orders_detected` | Count of UNOWNED classifications |
| `registry_integrity_failures` | Count of integrity check failures |

Emitted periodically via `ORDER_REGISTRY_METRICS` event.

## What Remains for Phase 3

- Configurable manual order policy (Option B: log only)
- Deeper restart adoption (entry orders surviving restart)
- Stricter broker/journal/order reconciliation
- Migration away from alias-as-primary (canonical identity broker-order-centric)
