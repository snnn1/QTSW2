# MES RECONCILIATION_QTY_MISMATCH Investigation (2026-03-06)

## Summary

Reconciliation reported `journal_qty=0` for MES while broker had 2 contracts. The diagnostic script (reading from disk) found the journal `2026-03-06_ES2_f6dee8795a328711.json` with `Instrument=MES`, `EntryFilledQuantityTotal=2` — so the journal exists and is correct. The robot at runtime reports `journal_qty=0`.

## Observed State

| Source | MES journal_qty | Notes |
|--------|-----------------|-------|
| `scripts/diagnose_reconciliation.ps1` | 2 | Reads from `data/execution_journals/` on disk |
| Robot at runtime (ReconciliationRunner) | 0 | `RECONCILIATION_QTY_MISMATCH` in logs |

- **Journal file**: `2026-03-06_ES2_f6dee8795a328711.json`
- **Instrument**: MES (correct)
- **Stream**: ES2

## Root Cause (Same as 2026-03-04 MYM)

**File read failure** — `GetOpenJournalEntriesByInstrument` reads each journal file. If `File.ReadAllText(path)` throws (e.g. file lock from another strategy instance writing at the same moment), the catch block skips the file and logs `EXECUTION_JOURNAL_READ_SKIPPED`. Result: journal never added to `openByInstrument` → `journal_qty=0`.

## Mitigation Implemented

1. **Read with FileShare.ReadWrite** — Use `FileStream` with `FileShare.ReadWrite` when reading journals so reads succeed even when another process has the file open for writing.
2. **Write with FileShare.Read** — Use `FileStream` with `FileShare.Read` when writing journals so concurrent reads succeed during writes.
3. **Retry on IOException** — Retry up to 3 times with backoff when reading fails (handles transient locks).

## Immediate Resolution (Current Incident)

Use force reconcile to unfreeze MES:

```powershell
.\scripts\force_reconcile.ps1 MES
```

Or flatten the position and restart the robot so reconciliation can close orphan journals when broker is flat.

## References

- `docs/robot/execution/RECONCILIATION_QTY_MISMATCH_RESOLUTION.md`
- `docs/robot/incidents/2026-03-04_MYM_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md`
- `scripts/diagnose_reconciliation.ps1`
