# Daily Journal "No Streams" Investigation – 2026-03-13

## Summary

The Daily Journal showed "No streams for this date" for 2026-03-13 even though trades occurred. Root cause: **one or more intents with ledger invariant violations caused the entire journal build to fail**, returning empty streams.

## Root Cause

1. **Intent 44659b6859bae99b (ES2)**: EXECUTION_FILLED events in robot logs summed to exit_qty=8, but the execution journal showed entry_qty=4. This was an overfill incident (INTENT_OVERFILL_EMERGENCY, EXECUTION_JOURNAL_OVERFILL).

2. **Intent 57ab60fda6303524 (NG1)**: Similar exit_qty > entry_qty mismatch (zombie stop fill per NG_QTY_MISMATCH_INVESTIGATION.md).

3. **LedgerBuilder** raised `LedgerInvariantViolation` when `sum(exit_qty) > sum(entry_qty)` for any intent.

4. **get_daily_journal** caught all exceptions and returned `streams: []`, so the UI showed "No streams for this date".

## Fix

- **LedgerBuilder.build_ledger_rows()**: Added `skip_invalid_intents=False` parameter. When `True`, intents that raise `LedgerInvariantViolation` are skipped (with a warning) instead of failing the entire build.

- **get_daily_journal**: Now calls `build_ledger_rows(trading_date, skip_invalid_intents=True)` so the journal shows all valid streams even when some intents have data issues.

## Result

- 2026-03-13 daily journal now shows 11 streams (GC2, NG2, NQ2, RTY2, YM1, YM2 with trades; others from slot journals).
- Total PnL: $89.00.
- ES2 and NG1 intents with invariant violations are skipped; their streams still appear (from slot journals) with 0 trades.

## Related

- `docs/robot/incidents/2026-03-13_NG_QTY_MISMATCH_INVESTIGATION.md` – zombie stop fill for NG1
- `docs/robot/incidents/2026-03-13_MES_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md` – ES2 overfill
