# Unknown Order Fill — Flatten Enqueued MNQ — Investigation

**Date:** 2026-02-18 (investigation); incidents from 2026-01-28, 2026-02-05, 2026-02-26, 2026-03-03  
**Scope:** EXECUTION_UPDATE_UNKNOWN_ORDER, UNKNOWN_ORDER_FILL_FLATTENED, Flatten Enqueued MNQ

---

## Executive Summary

**Root cause:** Execution updates (fills) are delivered to an adapter instance that does not have the order in its `_orderMap`. The system correctly treats this as an unknown order and enqueues a flatten (fail-closed).

**Contributing factors:**
1. **Multiple MNQ strategy instances** — 7 different `run_id`s received the same execution update (2026-01-28)
2. **Strategy restart / instance swap** — New MNQ instance starts, becomes the MNQ endpoint, receives fills for orders submitted by a previous instance (2026-03-03)
3. **Order tracking race** — Execution update arrives before order is added to `_orderMap` (within ~118ms of order creation)
4. **ExecutionUpdateRouter** — Only one endpoint per (account, MNQ); all instances invoke the same endpoint, but each instance has its own `_orderMap`

---

## Incident Timeline: 2026-03-03 (Most Recent MNQ)

| Time (Chicago) | Event | Details |
|----------------|-------|---------|
| **09:25:46** | INTENT_EXPOSURE_REGISTERED | Intent `7ad6bafcd43cf21f`, NQ1, Short, MNQ. Submitted by run_id `72f4acbd` (NQ chart) |
| **09:38:47** | ENGINE_START | New MNQ instance starts: run_id `3eeac718`, ninjatrader_instrument = MNQ |
| **10:04:28** | EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL | Fill for intent `7ad6bafcd43cf21f` — order not in map |
| **10:04:28** | NT_ACTION_ENQUEUED | Flatten enqueued: `UNKNOWN_ORDER:7ad6bafcd43cf21f`, instrument MNQ |
| **10:05:01** | NT_ACTION_START | Flatten executed on strategy thread |

**Analysis:** The NQ chart (run_id `72f4acbd`) submitted the entry and had the order in its map. A new MNQ chart (run_id `3eeac718`) started ~13 minutes later. When the protective stop/target filled, the fill was routed to the MNQ endpoint — now owned by the new instance. The new instance’s `_orderMap` was empty (fresh start), so the order was unknown → flatten enqueued.

---

## Incident Timeline: 2026-01-28 (7 Instances, Same Order)

| Time (UTC) | Event | run_id | broker_order_id |
|------------|-------|--------|-----------------|
| 17:59:17.659 | ORDER_CREATED_VERIFICATION | 5f5c7137 | 9e8344f808d84673 |
| 17:59:17.777 | EXECUTION_UPDATE_UNKNOWN_ORDER | 965e8283, 0abc8e49, 4eb4733f, **5f5c7137**, 74614110, b82cc842, f6e758fd | 9e8344f808d84673 |
| 17:59:17.921 | ORDER_SUBMIT_SUCCESS | 5f5c7137 | 9e8344f808d84673 |

**Finding:** The submitting instance (`5f5c7137`) also logged EXECUTION_UPDATE_UNKNOWN_ORDER. The execution update arrived at 17:59:17.777, but ORDER_SUBMIT_SUCCESS was at 17:59:17.921 — **144ms later**. The order is added to `_orderMap` at submit success, so the execution update arrived before the order was in the map → **race condition**.

**7 run_ids:** NinjaTrader’s `Account.ExecutionUpdate` fires for every strategy instance subscribed to the account. The ExecutionUpdateRouter routes to a single endpoint, but the log file `robot_MNQ.jsonl` merges events from all instances. The 7 run_ids indicate 7 MNQ strategy instances were running; each received the callback. The router invokes one endpoint; the others may have logged before routing or the merge shows multiple instances’ views.

---

## Root Causes (Prioritized)

### 1. Strategy Restart / Instance Swap (2026-03-03)
- **What:** New MNQ instance starts and registers as the MNQ endpoint. Fills for orders submitted by a previous instance are routed to it.
- **Why:** Each instance has its own `_orderMap`. A new instance has no prior orders.
- **Mitigation:** Document that restarting the MNQ chart while a position is open can trigger unknown-order flatten. Consider persisting order state across restarts or blocking restart when position exists.

### 2. Order Tracking Race (2026-01-28)
- **What:** Execution update (OrderUpdate for Initialized/Submitted) arrives before the order is added to `_orderMap`.
- **Why:** Order is added at ORDER_SUBMIT_SUCCESS; NinjaTrader can send execution/order updates before submit completes.
- **Mitigation:** The 500ms grace period (`UNRESOLVED_GRACE_MS`) and `DeferUnresolvedExecution` are intended to handle this. If the order is still missing after 500ms, flatten is triggered. Verify that the grace period is sufficient and that orders are added to the map as early as possible (e.g., at order creation, not just at submit success).

### 3. Multiple MNQ Instances
- **What:** 7 MNQ strategy instances running simultaneously.
- **Why:** Multiple MNQ charts or misconfiguration.
- **Mitigation:** Only one instance can register for (account, MNQ). The first succeeds; others get `EXEC_ROUTER_ENDPOINT_CONFLICT` and fail to start. If 7 instances were running, they may have started at different times (stale registrations) or the router may allow overwrite in some paths. Audit endpoint registration and conflict handling.

---

## Code References

| Component | Path | Relevant Logic |
|-----------|------|----------------|
| Unknown order handling | `NinjaTraderSimAdapter.NT.cs` ~1938–1967 | `TriggerUnknownOrderFlatten` → `EnqueueNtAction(NtFlattenInstrumentCommand)` when IEA |
| Order map lookup | `NinjaTraderSimAdapter.NT.cs` ~1269 | `OrderMap.TryGetValue(intentId, out orderInfo)` — if false, log EXECUTION_UPDATE_UNKNOWN_ORDER |
| Execution routing | `ExecutionUpdateRouter.cs` | Single endpoint per (account, executionInstrumentKey) |
| Grace period | `NinjaTraderSimAdapter.NT.cs` ~1926 | `UNRESOLVED_GRACE_MS` (500ms) before flatten |

---

## Recommendations

1. **Add order to map at creation:** Consider adding the order to `_orderMap` when `ORDER_CREATED_VERIFICATION` fires, not only at `ORDER_SUBMIT_SUCCESS`, to reduce the race window.
2. **Restart warning:** When an MNQ instance starts, check for open MNQ positions. If any exist, log a warning that fills may be treated as unknown if they were submitted by a previous instance.
3. **Audit multi-instance behavior:** Confirm whether multiple MNQ instances can run and how the router behaves when instances start/stop.
4. **Register EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL:** Add to `RobotEventTypes` registry to avoid `UNREGISTERED_EVENT_TYPE` warnings.

---

## Related Incidents

- **M2K** (2026-02-05, 2026-02-26): Same pattern — unknown order fill, flatten enqueued or flattened
- **MGC** (2026-02-26): EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL, order not in map after retries
- **MNG** (2026-02-17): Unknown order fill, position flattened
- **MES** (2026-02-05): Unknown order fill, position flattened
