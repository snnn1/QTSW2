# MYM RECONCILIATION_QTY_MISMATCH Investigation (2026-03-04)

## Summary

Reconciliation reported `journal_qty=0` for MYM while broker had 2 contracts. The diagnostic script (reading from disk) found the journal with `Instrument=MYM`, `EntryFilledQuantityTotal=2` — so the journal exists and is correct. The robot at runtime reports `journal_qty=0`.

## Observed State

| Source | MYM journal_qty | Notes |
|--------|-----------------|-------|
| `scripts/diagnose_reconciliation.ps1` | 2 | Reads from `data/execution_journals/` on disk |
| Robot at runtime (ReconciliationRunner) | 0 | `RECONCILIATION_QTY_MISTCH` in logs |

- **Journal file**: `2026-03-04_YM1_41f19a9af8eb3dba.json`
- **Instrument**: MYM (correct — execution instrument fix from 2026-02-26 M2K incident applied)
- **Project root**: `C:\Users\jakej\QTSW2` (verified in `PROJECT_ROOT_RESOLVED` logs)

## Possible Root Causes

### 1. File read failure (most likely)

`GetOpenJournalEntriesByInstrument` reads each journal file. If `File.ReadAllText(path)` throws (e.g. file lock from another strategy instance), the catch block skips the file silently. Result: journal never added to `openByInstrument` → `journal_qty=0`.

**Mitigation added**: `EXECUTION_JOURNAL_READ_SKIPPED` event when a file read fails. Check logs for this event to confirm.

### 2. Per-strategy ExecutionJournal isolation

Each strategy instance has its own `RobotEngine` and `ExecutionJournal`. All use the same `projectRoot` → same `_journalDir`. In theory they should see the same files. If one instance resolves a different project root (e.g. env change), it would read from a different directory.

**Mitigation added**: `RECONCILIATION_CONTEXT` now includes `journal_dir` and `open_instruments_qty` so we can verify which directory is used and what the journal actually returned.

### 3. Instrument key mismatch (ruled out)

The 2026-02-26 M2K incident was caused by journal storing canonical (RTY) while reconciliation looked up execution (M2K). The current MYM journal has `Instrument=MYM` — correct. This is not the cause.

## Diagnostic Changes Added

1. **ExecutionJournal**:
   - `JournalDirectory` property for debugging
   - `EXECUTION_JOURNAL_READ_SKIPPED` when a file read throws (path, error, exception type)

2. **ReconciliationRunner**:
   - `RECONCILIATION_CONTEXT` now includes `journal_dir` and `open_instruments_qty` (summary of what the journal returned per instrument)

## Next Steps

1. **Rebuild and redeploy** — the diagnostic changes will emit new log events.
2. **Reproduce** — when `RECONCILIATION_QTY_MISMATCH` occurs again, check:
   - `EXECUTION_JOURNAL_READ_SKIPPED` — indicates file lock or read failure
   - `RECONCILIATION_CONTEXT.open_instruments_qty` — if MYM is missing or 0, the journal read failed or returned empty
   - `RECONCILIATION_CONTEXT.journal_dir` — should be `C:\Users\jakej\QTSW2\data\execution_journals`
3. **If file lock confirmed**: Add `FileShare.Read` when writing journals so concurrent reads succeed; or add retry logic for reads.

## Immediate Resolution (current incident)

Use force reconcile to unfreeze MYM:

```powershell
.\scripts\force_reconcile.ps1 MYM
```

Or flatten the position and restart the robot so reconciliation can close orphan journals when broker is flat.

## References

- `docs/robot/execution/RECONCILIATION_QTY_MISMATCH_RESOLUTION.md`
- `docs/robot/incidents/2026-02-26_M2K_JOURNAL_BROKER_MISMATCH_INVESTIGATION.md`
- `scripts/diagnose_reconciliation.ps1`
