# IEA Order Registry: Phase 1 Design Note

## Overview

Phase 1 of Execution Ownership extends the IEA with a canonical runtime order registry. Every live order is either owned by the robot, explicitly adopted, or treated as anomalous.

## Canonical Order Identity

- **Primary**: Broker/native order id (`order.OrderId` from NinjaTrader)
- **Secondary**: Intent-based aliases (`intentId`, `intentId:STOP`, `intentId:TARGET`) for compatibility
- **Rule**: Direct broker id lookup is the canonical path; alias lookup is the compatibility path

## Ownership Statuses

| Status | Meaning |
|--------|---------|
| OWNED | Submitted by current runtime |
| ADOPTED | Discovered and intentionally taken over (e.g. restart protectives via ScanAndAdoptExistingProtectives) |
| UNOWNED | Known anomaly, not under robot ownership |
| TERMINAL | Completed/canceled/rejected, no longer live |

## Direct vs Alias Lookup

- **Direct**: `TryResolveByBrokerOrderId(brokerOrderId)` — primary path for execution updates and order updates
- **Alias**: `TryResolveByAlias(intentId)` or `TryResolveByAlias(intentId:STOP)` — fallback when broker id not in registry (e.g. legacy paths)
- **Resolution path** is logged: `DirectId`, `Alias`, `Adopted`, `Unresolved`

## What Is Registered

- Entry orders (single and aggregated)
- Stop orders (protective)
- Target orders (protective)
- Flatten orders (from RequestFlatten)
- Adopted orders (from ScanAndAdoptExistingProtectives)

## Lifecycle Updates

- On fill: `UpdateOrderLifecycle(brokerOrderId, FILLED, utcNow)`
- On order update: lifecycle derived from `OrderState` (Working, Filled, Canceled, Rejected)
- Terminal orders: `OwnershipStatus` set to TERMINAL, `TerminalUtc` set

## What Remains for Phase 2

- Deeper restart adoption (e.g. entry orders surviving restart)
- Manual-order policy (how to classify/handle manual orders)
- Stricter broker/journal/order reconciliation
- Migration away from alias-as-primary (canonical identity should become broker-order-centric over time)
