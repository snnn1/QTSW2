# Analyzer ↔ Robot Parity Lock

**Status**: Parity is locked as of `replay_2025-12-01__2025-12-05_fixed`

**Lock Date**: 2026-01-02

## What Parity Means

Whenever Robot emits an intent, it must exactly match Analyzer intent for the same `(trading_date, stream, direction)`.

## Explicitly Allowed

- **Analyzer-only intents**: Expected when Analyzer produces intents for streams/dates that Robot did not evaluate (e.g., different date ranges, disabled streams, or no breakouts occurred)

## Explicitly Forbidden

- **HARD_MISMATCH**: Robot and Analyzer intents for the same `(trading_date, stream)` must match exactly (within approved tolerances)
- **ROBOT_ONLY**: Robot must not produce intents that Analyzer does not have for the same `(trading_date, stream)`

## Required Process for Future Changes

1. Update Analyzer logic first (if needed)
2. Update parity spec (`configs/analyzer_robot_parity.json`) if semantics change
3. Re-run parity replay on snapshot dates
4. **HARD_MISMATCH must remain 0**
5. Document any new tolerances or exceptions

## Parity Verification

Parity is verified using:
- `modules/robot/parity/robot_intent_extractor.py` - Extracts Robot intents from DRYRUN logs
- `modules/analyzer/parity/analyzer_intent_extractor.py` - Extracts Analyzer intents from CSV/parquet outputs
- `modules/robot/parity/parity_diff.py` - Compares intents and generates parity report

## Reference Run

- **Run ID**: `replay_2025-12-01__2025-12-05_fixed`
- **Date Range**: 2025-12-01 to 2025-12-05
- **Robot Intents**: 40
- **Analyzer Intents**: 245
- **MATCH**: 40
- **HARD_MISMATCH**: 0
- **ES3 Stream**: Verified present on both sides

---

**This is governance, not code.** Parity rules are enforced by the parity diff tool and must be maintained for all future changes.
