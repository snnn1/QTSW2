# Incident Post-Mortem: YM2 Range Not Calculated (2026-02-17)

**Date**: 2026-02-17  
**Affected**: YM1, YM2 streams (MYM instrument)  
**Severity**: Medium – streams stood down, range never computed

---

## Summary

YM2 (and YM1) did not calculate range on 2026-02-17 because the engine detected a **RECONCILIATION_QTY_MISMATCH** on MYM and froze the instrument. All MYM streams were stood down while still in **PRE_HYDRATION**, before reaching ARMED, RANGE_BUILDING, or RANGE_LOCKED. The range is only computed during RANGE_BUILDING, so it never ran.

---

## Timeline

| Time (UTC) | Event |
|------------|-------|
| 2026-02-16 03:52 | STREAM_STAND_DOWN for YM1, YM2 (reason: QTY_MISMATCH) |
| 2026-02-16 17:31 | RECONCILIATION_QTY_MISMATCH: MYM account=2, journal=26 |
| 2026-02-16 23:28 | RECONCILIATION_QTY_MISMATCH repeated (different run) |

---

## Root Cause

### Primary: RECONCILIATION_QTY_MISMATCH on MYM

- **Account position**: 2 contracts (broker-reported)
- **Journal position**: 26 contracts (sum of `EntryFilledQuantityTotal` across open journals)

The engine compares account vs journal quantity for each instrument with a position. When they differ, it:

1. Logs `RECONCILIATION_QTY_MISMATCH`
2. Invokes `StandDownStreamsForInstrument(MYM, ...)`
3. Freezes the instrument (no new streams, no execution)

### Why journal=26?

Open journals are those with `EntryFilled=true` and `TradeCompleted=false`. The sum of `EntryFilledQuantityTotal` for such journals was 26. Likely contributors:

- **2026-02-03_YM2** (22 contracts): Large fill; position may have been closed externally (manual flatten, stop hit) without the journal receiving exit fills.
- **2026-02-16_YM1** (2 contracts): Recent fill; may have been open at mismatch time.
- **2026-02-04_YM1** (2 contracts): Older fill; orphaned when position closed externally.

### Why reconciliation didn’t fix it

`ReconciliationRunner` only reconciles **orphan journals** when the **broker is flat** for that instrument. With account=2, the broker was not flat, so reconciliation never ran for MYM. Orphan journals stayed open and the mismatch persisted.

---

## Impact

- YM1 and YM2 streams stood down with `FAILED_RUNTIME` / `STREAM_STAND_DOWN`
- Range never computed (streams never left PRE_HYDRATION)
- No entries, no stop brackets, no P&L for those streams

---

## Resolution (Completed)

Journals have since been reconciled (e.g. `CompletedAtUtc: 2026-02-16T03:53` on several MYM journals). Current state: all MYM journals show `TradeCompleted=true`. The mismatch was transient; reconciliation ran once the broker went flat.

---

## Prevention / Follow-Up (Implemented)

1. **Manual reconciliation tool**: `scripts/force_reconcile.ps1` + `data/pending_force_reconcile.json` – force-close orphan journals when operator confirms account correct.
2. **Softer freeze**: Streams without positions continue to RANGE_BUILDING; only execution blocked. YM2 would now compute range even during mismatch.
3. **Alerting**: `RECONCILIATION_QTY_MISMATCH` already in HealthMonitor whitelist – push notification sent when enabled.
4. **Auto-unfreeze**: When reconciliation pass finds matching qty, instrument is unfrozen automatically.

---

## References

- `RobotCore_For_NinjaTrader/Execution/ReconciliationRunner.cs` – qty mismatch check, orphan reconciliation
- `RobotCore_For_NinjaTrader/RobotEngine.cs` – `StandDownStreamsForInstrument`, `StandDownStream`
- `docs/robot/execution/RECONCILIATION_QTY_MISMATCH_RESOLUTION.md` – resolution steps
- Logs: `logs/robot/robot_ENGINE.jsonl`, `logs/robot/hydration_2026-02-17.jsonl`
