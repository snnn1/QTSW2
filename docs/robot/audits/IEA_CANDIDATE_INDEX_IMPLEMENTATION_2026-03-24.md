# IEA adoption candidate index — implementation note (2026-03-24)

## Goal

Remove repeated full-disk journal sweeps from `GetAdoptionCandidateIntentIds` / recovery adoption by maintaining a deterministic incremental in-memory index inside `ExecutionJournal`, and resolve adoption candidates **once** per `RecoveryAdoption` gated scan (fingerprint + scan body).

## Index shape

| Structure | Role |
|-----------|------|
| `_normInstToAdoptionCandidateIntentIds` | `Dictionary<string, HashSet<string>>` — normalized **journal entry instrument** (same normalization as `NormalizeJournalInstrumentSymbol`) → set of **intent ids** that are adoption candidates. |
| `_adoptionCandidateIntentToNormInst` | `Dictionary<string, string>` — intent id → bucket key for O(1) removal when an entry changes instrument or drops out of candidacy. |

**Candidate rule** (unchanged from prior full-scan behavior):

- `EntrySubmitted == true`
- `TradeCompleted == false`

**Lookup** for `GetAdoptionCandidateIntentIdsForInstrument(executionInstrument, canonicalInstrument)`:

- Iterate normalized instrument **buckets**; for each key `k`, if `JournalInstrumentMatchesExecutionKey(k, executionInstrument, canonicalInstrument)`, merge that bucket’s intent ids into the result `HashSet`.

This preserves **execution / canonical / micro** semantics because matching uses the same static helper as the legacy scan (which matched on `entry.Instrument` after normalization).

## Update points (index aligned with persisted truth)

All updates run **under the existing journal `_lock`**, from **in-memory** `ExecutionJournalEntry` state (no extra disk read for the sync itself):

1. **`SaveJournal`** — after a **successful** write, parse intent id from the file basename and call `SyncAdoptionCandidateIndexForIntentLocked`.
2. **Disk → cache paths** (best-effort consistency when entries are loaded without an immediate save in this process):
   - `GetEntry` (disk load into `_cache`)
   - `IsIntentSubmitted` (disk load into `_cache`)
   - `GetOpenJournalEntriesByInstrument` (reconciliation-style full read; updates `_cache` per file)
   - `WarmCacheForTradingDate` (preload into `_cache`)

`SyncAdoptionCandidateIndexForIntentLocked` removes the intent from any previous bucket, then re-adds it if it still satisfies the candidate predicate.

## Warm-up behavior

- **Constructor** (after journal directory validation): `RebuildAdoptionCandidateIndexFromDiskLocked()` runs once — full directory pass, same file naming / deserialize rules as before, populating the index from **disk truth**.
- On success: `_adoptionCandidateIndexWarmed = true`, log **`EXECUTION_JOURNAL_ADOPTION_INDEX_REBUILT`** with `journal_file_total` and `adoption_candidate_intents`.
- On failure: `_adoptionCandidateIndexWarmed = false`, log **`EXECUTION_JOURNAL_ADOPTION_INDEX_REBUILD_FAILED`**.

## Fallback behavior

- If **`!_adoptionCandidateIndexWarmed`**, `GetAdoptionCandidateIntentIdsForInstrument` uses the **legacy full scan** (`GetAdoptionCandidateIntentIdsForInstrumentFullScan`), **fail-closed** on correctness (same result set as before).
- Increment **`_adoptionCandidateIndexLookupFallbacks`**; log **`EXECUTION_JOURNAL_ADOPTION_INDEX_FALLBACK`** on the **1st** and every **25th** fallback (low spam).

## Recovery scan: single candidate resolution

- **`InstrumentExecutionAuthority.RunGatedAdoptionScanBody`**: for `RecoveryAdoption`, call `Executor.GetAdoptionCandidateIntentIds` **once** before the fingerprint stopwatch; store in `_preResolvedAdoptionCandidatesForScan`, set `phaseTelemetry.CandidatesMs` from that single call.
- **`TryBuildRecoveryScanFingerprint`**: uses `_preResolvedAdoptionCandidatesForScan.Count` when present (no second journal query).
- **`ScanAndAdoptExistingOrdersCore`**: for `RecoveryAdoption` with a non-null snapshot, reuses the same collection for `activeIntentIds` and does **not** time a second candidate phase (candidates ms remains the pre-scan measurement).

Bootstrap and other scan sources still time candidate lookup inside the scan core as before.

## Diagnostics (engine events)

| Event | When |
|-------|------|
| `EXECUTION_JOURNAL_ADOPTION_INDEX_REBUILT` | Cold rebuild completed (`warmed: true`) |
| `EXECUTION_JOURNAL_ADOPTION_INDEX_REBUILD_FAILED` | Constructor rebuild threw |
| `EXECUTION_JOURNAL_ADOPTION_INDEX_FALLBACK` | Full-scan fallback (rate-limited) |

`IEA_ADOPTION_SCAN_PHASE_TIMING` unchanged; recovery runs should show **lower** `phase_candidates_ms` and **lower** `fingerprint_build_ms` (fingerprint no longer performs a second candidate query).

## Files changed

| File |
|------|
| `RobotCore_For_NinjaTrader/Execution/ExecutionJournal.cs` |
| `modules/robot/core/Execution/ExecutionJournal.cs` (synced copy) |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.cs` |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` |
| `RobotCore_For_NinjaTrader/RobotEventTypes.cs` |
| `modules/robot/core/RobotEventTypes.cs` |
| `docs/robot/audits/IEA_CANDIDATE_INDEX_IMPLEMENTATION_2026-03-24.md` |

## Build

- `dotnet build RobotCore_For_NinjaTrader/Robot.Core.csproj -c Release` — **succeeds** after this change.
- `dotnet build modules/robot/core/Robot.Core.csproj -c Release` — run when no other process locks `Robot.Contracts.dll` (file lock is environmental).
