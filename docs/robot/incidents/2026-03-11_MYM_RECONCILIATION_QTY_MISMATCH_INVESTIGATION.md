# MYM RECONCILIATION_QTY_MISMATCH Investigation (2026-03-11)

## Summary

Reconciliation reported `journal_qty=0` for MYM while broker had 2 contracts. The journal file `2026-03-11_YM1_dd605e3e36e63e14.json` on disk has `EntryFilled=true`, `TradeCompleted=false`, `EntryFilledQuantityTotal=2` — correct. No `EXECUTION_JOURNAL_READ_SKIPPED` in logs, so file reads succeeded. **Root cause: per-instance ExecutionJournal cache serving stale data across multiple NinjaTrader strategy instances.**

## Observed State (Last Incident)

| Field | Value |
|-------|-------|
| **Timestamp** | 2026-03-11T12:49:06 – 12:51:06 UTC |
| **Instrument** | MYM |
| **Broker/account_qty** | 2 |
| **Journal_qty** | 0 |
| **Mismatch taxonomy** | `broker_ahead` |
| **Journal file** | `2026-03-11_YM1_dd605e3e36e63e14.json` |
| **Run IDs** | e0d50fb2, fa23da88, 44eb4aef, af510d7f, 842ec91f, 94e0ec35 (multiple instances) |

## Root Cause: Stale Per-Instance Cache

Each NinjaTrader chart with RobotSimStrategy creates its own `RobotEngine` → its own `ExecutionJournal` → its own in-memory `_cache`. All instances share the same journal directory (`data/execution_journals`).

**Scenario:**
1. Instance A (YM1 chart) submits order, gets fill at 12:44:17, writes journal with `EntryFilled=true`.
2. Instance B (MNQ or another chart) started earlier. During startup or a prior reconciliation, it read `2026-03-11_YM1_dd605e3e36e63e14.json` when it had `EntryFilled=false` (order submitted, not yet filled).
3. Instance B cached that entry. It never re-reads the file because `GetOpenJournalEntriesByInstrument` uses `_cache.TryGetValue(fileName, out var cached)` and prefers cache over disk.
4. When Instance B runs reconciliation, it uses the cached entry (`EntryFilled=false`), which fails the filter `entry.EntryFilled && !entry.TradeCompleted`. The journal is skipped → `journal_qty=0` for MYM.
5. Broker has 2 (from Instance A’s fill), journal reports 0 → `RECONCILIATION_QTY_MISMATCH` with `broker_ahead`.

## Why Previous Mitigations Didn’t Fix It

- **FileShare.Read / ReadJournalFileWithRetry** (2026-03-06): Addresses file lock. No `EXECUTION_JOURNAL_READ_SKIPPED` in logs → reads succeed. The bug is cache, not lock.
- **RECONCILIATION_CONTEXT**: Confirmed `journal_dir` and `open_instruments_qty`; the journal returned 0 because of stale cache, not wrong path.

## Fix Implemented

**ExecutionJournal.GetOpenJournalEntriesByInstrument**: Always read from disk for reconciliation; do not use the cache. This guarantees cross-instance consistency. The cache remains used for single-instance operations (RecordEntryFill, HasEntrySubmitted, etc.).

```csharp
// Before: if (_cache.TryGetValue(fileName, out var cached)) { entry = cached; } else { read from disk }
// After:  always read from disk; optionally update cache after read for consistency
```

## Immediate Resolution (Current Incident)

```powershell
.\scripts\force_reconcile.ps1 MYM
```

Or flatten the position and restart the robot.

## References

- `docs/robot/execution/RECONCILIATION_QTY_MISMATCH_RESOLUTION.md`
- `docs/robot/incidents/2026-03-04_MYM_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md`
- `docs/robot/incidents/2026-03-06_MES_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md`
