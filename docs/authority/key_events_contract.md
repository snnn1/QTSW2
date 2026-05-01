# KEY_EVENTS contract (operator / audit)

Append-only JSONL at run root: `KEY_EVENTS.jsonl`. Each line is one JSON object. This document defines **ordering expectations**, **field meanings**, and **invariants** so a run can be explained without ENGINE logs.

## Evidence role

`KEY_EVENTS.jsonl` is an operator narrative stream, not the sole source of run truth. Summary and verdict builders must tolerate a missing or empty file and hydrate decision-critical counts from durable execution journals, stream journals, daily summaries, and reconciliation artifacts.

An empty `KEY_EVENTS.jsonl` is therefore a reporting/completeness issue, not by itself a safety failure. If durable artifacts prove intents, orders, fills, flatten activity, and shutdown flatness, the run summary should use those artifacts and annotate that key-event narrative coverage was absent.

## Line schema (all events)

| Field | Meaning |
|--------|---------|
| `ts_utc` | ISO-8601 timestamp when the decision was recorded (coarse; sub-second ordering uses **file order**). |
| `event` | Event type name (stable identifier). |
| `instrument` | Canonical or execution instrument when relevant; otherwise `null`. |
| `stream` | Stream id when relevant; otherwise `null`. |
| `reason` | Short top-level reason label (same family as `data.reason` when present). |
| `data` | Optional object; event-specific payload (minimal). |

## Ordering rules

1. **Within one `ApplyTimetable` / construction cycle** (same engine call, same `utcNow` often):
   - `STREAM_SKIPPED` lines appear in **timetable iteration order** (per enabled directive).
   - `TIMETABLE_APPLY_PARTIAL_REFUSAL` (if present) appears **after** all `STREAM_SKIPPED` for that application, when ENGINE already logged partial refusal.
   - `STREAMS_CONSTRUCTION_OUTCOME` is emitted from `EnsureStreamsCreated` **after** `ApplyTimetable` completes and **after** ENGINE `STREAMS_CREATED` — it is the **terminal setup summary** for that attempt.

2. **Global file order**: Consumers should treat **append order** as authoritative when `ts_utc` ties or when correlating cross-call events.

3. **Lifecycle A (initial construction story)**: `_lastStreamConstruction*` metrics used for `STREAMS_CONSTRUCTION_OUTCOME` are **not** overwritten on mid-day `ApplyTimetable` when streams already exist; mid-day may still append `STREAM_SKIPPED` / timetable KEY events without a second construction outcome.

4. **Cross-call caveat**: A later `STREAM_SKIPPED` may appear after an earlier `STREAMS_CONSTRUCTION_OUTCOME` from a different call; use `data.trading_date` and event order to interpret.

## Setup-phase events (decision boundaries)

### `STREAM_SKIPPED`

| `data` field | Meaning |
|----------------|----------|
| `stream` | Stream id (may be null if unknown). |
| `instrument` | Canonical instrument when known. |
| `reason` | Normalized skip reason (e.g. `canonical_mismatch`, `filtered_out`, `session_freeze_blocked`). |
| `trading_date` | Session trading date. |
| `session`, `slot_time` | Present when available in scope. |

**Dedupe**: at most one line per `(stream identity, trading_date)` for a terminal skip.

### `TIMETABLE_APPLY_PARTIAL_REFUSAL`

| `data` field | Meaning |
|----------------|----------|
| `trading_date` | Session trading date. |
| `decision_type` | `full_refusal` or `partial_refusal` (eligibility/tradability reduced vs enabled timetable rows). |
| `affected_streams` | Count of impacted directives (skipped + committed directive skips + blocked mid-session new streams). |

**Note**: `affected_streams` may exceed the count of `STREAM_SKIPPED` lines when blocks use `STREAM_ADDITION_BLOCKED` semantics folded into `STREAM_SKIPPED` with `session_freeze_blocked`.

### `STREAMS_CONSTRUCTION_OUTCOME`

| Top-level `reason` | Meaning |
|--------------------|--------|
| `NO_STREAMS` | No streams armed for trading after construction attempt. |
| `STREAMS_READY` | At least one stream exists in the engine map after construction. |

| `data` field | Meaning |
|----------------|----------|
| `trading_date` | Session trading date, or `UNKNOWN` if missing. |
| `total_candidates` | Enabled timetable directives count, or `UNKNOWN`. |
| `streams_created` | Final stream count. |
| `streams_skipped` | Skipped directive count from construction pass, or `UNKNOWN`. |
| `failure` | If `true`, construction threw before normal completion (exception path); still `NO_STREAMS`. |

**Dedupe**: One outcome per trading day, with optional upgrade `NO_STREAMS` → `STREAMS_READY` if a later attempt succeeds the same day.

## Invariants

1. **Reconciliation**: Sum of distinct setup skip causes should be explainable against `TIMETABLE_APPLY_PARTIAL_REFUSAL.affected_streams` when all blocks emit `STREAM_SKIPPED` (including `session_freeze_blocked`).

2. **No silent construction failure**: Exception during `ApplyTimetable` from `EnsureStreamsCreated` emits `STREAMS_CONSTRUCTION_OUTCOME` with `failure: true` before the exception propagates.

3. **Trading date**: `STREAMS_CONSTRUCTION_OUTCOME` never drops solely for empty date; `trading_date` may be `UNKNOWN`.

## Trading / execution events (existing)

Examples: `EXECUTION_BLOCKED`, `ENTRY_REJECTED`, `STREAM_STAND_DOWN`, `RANGE_LOCK_OUTCOME`, flatten lifecycle events. These answer **what blocked execution** after setup; see payloads in code (`KeyEventWriter`, adapters).

## Narrative reconstruction

### Required questions (operator)

| Question | `KeyEventsTradingNarrative` mapping |
|----------|-------------------------------------|
| Why didn’t we trade? | `Summarize()`, `StreamSkipReasonCounts`, `SkipReasonsByInstrument`, `LastTimetableDecisionType`, `ConstructionOutcomeHistory` |
| Was trading possible? | Split: **`WasSetupValid`** / **`WasTradingStructurallyPossible`** (streams armed, no construction exception) vs **`WasExecutionReachableAggregate`** (structural + no `EXECUTION_BLOCKED` / `ENTRY_REJECTED` in file, aggregate only) |
| What blocked trading? | Skips + timetable refusal + `HadExecutionDenials` + `OrderedHighlights` order |
| System failure vs valid no-trade? | `ConstructionFailedException` (`data.failure` on outcome) vs normal `NO_STREAMS` |
| Execution denied vs never attempted? | Denials present → attempted path hit gates; no streams → `WasSetupValid` false |

**Ordering:** `FirstEventName` / `LastEventTsUtc`, **`OrderedHighlights`** (chronological subset), **`ConstructionOutcomeHistory`** (retry / NO_STREAMS → STREAMS_READY).

**Lossy aggregation:** `StreamSkipReasonCounts` is global; use **`SkipReasonsByInstrument`** for NQ vs ES patterns.

**Drift guard:** `KeyEventsContractCatalog` + `KeyEventsContractValidation.ValidateCatalogAgainstReader()` — sample lines must include minimum `data` keys per setup event.

Reference implementation: `KeyEventsNarrativeReader`, `KeyEventsContractCatalog`, `KeyEventsContractValidation` under `system/modules/robot/core/`, tests in `Tests/KeyEventsNarrativeReconstructionTests.cs`.

Run the narrative reconstruction self-test (standalone; does not require a full `Robot.Core` build):

```bash
dotnet run --project system/modules/robot/core/key-events-narrative-tool/KeyEventsNarrative.Tool.csproj
```
