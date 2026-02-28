# M2K Journal/Broker Mismatch Investigation (2026-02-26)

## Summary

Reconciliation reported `journal_qty=0` while broker had 2 contracts (short M2K). The fill was processed (EXECUTION_FILLED, INTENT_FILL_UPDATE logged) but the execution journal did not reflect it for reconciliation.

## Root Cause: Instrument Key Mismatch

**The journal stores the canonical instrument (RTY) but reconciliation looks up by execution instrument (M2K).**

### Flow

1. **RecordEntryFill** receives `allocContext.ExecutionInstrument` from `context.ExecutionInstrument`
2. **ResolveIntentContextOrFailClosed** sets `context.ExecutionInstrument = intent.Instrument` (canonical)
3. For RTY stream, `intent.Instrument` = "RTY", so journal entry gets `Instrument = "RTY"`
4. **Reconciliation** gets account positions with `p.Instrument` = "M2K" (broker reports execution instrument)
5. **GetOpenJournalQuantitySumForInstrument("M2K", "M2K")** looks for entries where `entry.Instrument` matches "M2K"
6. Journal entry has `Instrument = "RTY"` → no match → journal_qty = 0

### Secondary: File Lock Contention

Logs show `EXECUTION_JOURNAL_ERROR: The process cannot access the file ... because it is being used by another process.` Multiple engine instances (one per strategy) each have their own ExecutionJournal but all write to the same directory. Concurrent reads/writes can cause SaveJournal to fail silently (error logged, cache updated in one instance, but file not persisted).

## Fix

### 1. Use Execution Instrument in Journal (Primary)

In `ResolveIntentContextOrFailClosed` and the allocation path, pass `intent.ExecutionInstrument` (not `intent.Instrument`) for the journal's Instrument field so reconciliation can match account positions (M2K) with journal entries.

### 2. Retry on SaveJournal Failure (Secondary)

Add retry logic for IOException in SaveJournal to handle transient file lock contention.

## Verification

After fix:
- Journal entry for intent 55008294713a2e64 should have `Instrument = "M2K"`
- Reconciliation should find it when account has M2K position
- journal_qty should match broker_qty
