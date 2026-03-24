# NinjaTrader CPU vs recent robot logging (adoption / reconciliation)

**Date:** 2026-03-23  
**Context:** High CPU attributed to NinjaTrader (not the Python watchdog). Review of `logs/robot/robot_ENGINE.jsonl` tail.

## What the logs show

1. **`ADOPTION_SCAN_START` / `ADOPTION_SCAN_SUMMARY`** (per execution instrument / IEA), often **seconds apart**, with:
   - `scanned_orders_total` in the **thousands** (lifetime counter),
   - `skipped_foreign_instrument_orders_total` in the **tens of thousands**,
   - `journal_file_count` in the **hundreds** (e.g. 834),
   - `broker_working_count` moderate (e.g. 16) but many **foreign-instrument** QTSW2-tagged orders skipped per scan.

2. **`RECONCILIATION_PASS_SUMMARY`** on a recurring basis (expected from periodic reconciliation).

3. **`RECONCILIATION_ORDER_SOURCE_BREAKDOWN`** when `broker_working != iea_working` — triggers **`TryRecoveryAdoption()`** → full **`ScanAndAdoptExistingOrders()`** (see `RobotEngine.AssembleMismatchObservations`).

4. **`MismatchEscalationCoordinator`** runs **`OnAuditTick` every 5 seconds** (`MISMATCH_AUDIT_INTERVAL_MS = 5000`), which calls `AssembleMismatchObservations` and, for instruments in the state-consistency gate, **`RunInstrumentGateReconciliation`** → **`TryRecoveryAdoption()`** again (`RobotEngine.cs` ~5265).

5. **`ENGINE_TIMER_HEARTBEAT`** — multiple lines per wall-clock time (one per strategy/run_id on the account).

6. **`SNAPSHOT_METRICS`** — e.g. `GetAccountSnapshot` ~2 calls/sec aggregated over a window.

## Why that costs CPU in NT

`ScanAndAdoptExistingOrders` iterates **`account.Orders`** for every **working/accepted** order, decoding tags and running convergence logic. With **many charts / instruments** and **many QTSW2 orders on “foreign” instruments**, each IEA still **walks the full order collection** and increments skip metrics — **repeatedly** when reconciliation and gate recovery invoke **`TryRecoveryAdoption`** on a **5s cadence**.

So: **not primarily “log file I/O”** — the **work is the scan**, logging only makes it visible.

## Mitigation applied (code)

**`InstrumentExecutionAuthority.TryRecoveryAdoption`** (`InstrumentExecutionAuthority.NT.cs`): throttle so a **full adoption scan** from this path runs at most **once per 10 seconds per IEA instance**. Bootstrap, first execution-update adoption, and deferred retry paths are **unchanged** (they do not use this throttle).

This cuts redundant scans when both mismatch audit (~5s) and gate reconciliation call recovery adoption in the same window.

## Follow-ups (if still hot)

- Reduce **charts** sharing one account if each attaches an IEA scanning the same global order list.
- Consider **archiving old execution journals** so `journal_file_count` and related work shrink (secondary to order-loop cost).
- If needed, add **DEBUG-only** logging when recovery adoption is skipped by throttle (for proving the throttle is active).
