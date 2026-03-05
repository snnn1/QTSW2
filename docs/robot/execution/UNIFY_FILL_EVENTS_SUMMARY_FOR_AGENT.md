# Unify Fill Events — Summary for Agent Handoff

**Date**: 2026-02-18  
**Scope**: Execution fill canonicalization, P0 + P1 refactor

---

## What Was Done

### 1. Unify Fill Events (Initial Refactor)
- **EXECUTION_EXIT_FILL → EXECUTION_FILLED**: Exit fills (stop/target) now emit `EXECUTION_FILLED` with `order_type` (STOP/TARGET) instead of `EXECUTION_EXIT_FILL`.
- **trading_date**: Added to all fill payloads; fail-closed if null (`EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL`).
- **RobotLogger**: Promotes `trading_date` from `data` to top-level when missing.
- **EXECUTION_FILLED** already in `LIVE_CRITICAL_EVENT_TYPES`; `EXECUTION_EXIT_FILL` kept for backfill.

### 2. P0 — Broker Flatten Fix (CRITICAL)
- **Problem**: `BROKER_FLATTEN_FILL_RECOGNIZED` returned early; no `EXECUTION_FILLED`, no `RecordExitFill`, no `OnExitFill`.
- **Fix**: Added `ProcessBrokerFlattenFill()` — routes broker flatten fills through canonical exit path.
- **Flow**: Get active exposures via `_coordinator.GetActiveExposuresForInstrument()`, allocate fill qty to intents, emit `EXECUTION_FILLED` per intent with `order_type=FLATTEN`, call `RecordExitFill` and `OnExitFill`.
- **Unmapped**: If no intents match, emits `EXECUTION_FILL_UNMAPPED` (CRITICAL).
- **Instrument fallback**: Tries both `instrument` and `execInstKey` for exposure lookup (MES/ES mismatch).

### 3. P1 — Payload Enrichment
- All `EXECUTION_FILLED` now include: `execution_instrument_key`, `side` (BUY/SELL), `account`, `stream_key`, `session_class`, `source`.
- `DeriveSessionClass(stream)` helper: e.g. "ES1" → "S1", "ES2" → "S2".

### 4. P1 — Aggregated Entry Fill
- **Per-intent emission**: One `EXECUTION_FILLED` per allocated intent when aggregated (CL1+CL2 same price).
- `EmitEntryFill()` local function used for both aggregated and single-intent paths.

### 5. Ledger Builder
- **EXECUTION_FILLED only**: Removed dependency on `INTENT_EXIT_FILL` for PnL.
- **Invariants**: `fill_price > 0`, `fill_qty > 0`, `trading_date` non-null; `execution_instrument_key` and `side` required for non-synthetic exit fills.
- **Backfill**: Still converts `EXECUTION_EXIT_FILL` to synthetic `EXECUTION_FILLED` when no canonical fill exists for that order.
- Raises `LedgerInvariantViolation` on violation.

### 6. Schema
- `normalize_execution_filled` extracts: `execution_instrument_key`, `side`, `account`, `stream_key`, `session_class`.

### 7. Config
- Added to `LIVE_CRITICAL_EVENT_TYPES`: `EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL`, `EXECUTION_FILL_UNMAPPED`.
- Added to `RobotEventTypes.cs`: `EXECUTION_FILL_UNMAPPED` (CRITICAL).

---

## Key Files

| Area | Path |
|------|------|
| Broker flatten handler | `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` — `ProcessBrokerFlattenFill`, `AllocateFlattenFillToExposures`, `DeriveSessionClass` |
| Entry/exit fill emission | Same file — `ProcessExecutionUpdateContinuation`, `EmitEntryFill` |
| Ledger builder | `modules/watchdog/pnl/ledger_builder.py` |
| Schema | `modules/watchdog/pnl/schema.py` |
| Config | `modules/watchdog/config.py` — `LIVE_CRITICAL_EVENT_TYPES` |
| Event types | `RobotCore_For_NinjaTrader/RobotEventTypes.cs` |

---

## Gaps / Follow-ups (from EXECUTION_LOGGING_GAPS_ASSESSMENT.md)

- **Untracked fill** (`EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL`): No `EXECUTION_FILLED` — fail-closed before canonical path.
- **Unknown order fill** (`EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL`): Same.
- **Optional fields**: commission, fees, slippage_ticks, oco_id, nt_order_name — not yet in payloads.
- **INTENT_EXIT_FILL**: Still emitted by coordinator; ledger no longer uses it. Can deprecate once migration is complete.

---

## Build Status

- `Robot.Core.csproj` builds successfully (warnings only, no errors).
