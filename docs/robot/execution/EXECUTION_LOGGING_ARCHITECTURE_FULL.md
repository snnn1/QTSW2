# Execution Logging — Full System Architecture

**Date**: 2026-03-03  
**Scope**: Complete technical documentation of execution logging architecture end-to-end.  
**Purpose**: Full-system explanation before any further changes. No code modifications.

---

## 1. High-Level Overview

### 1.1 What Constitutes an "Execution Event"

An **execution event** in this system is any log entry that records or describes broker-side order execution activity: fills (entry, exit, partial), order lifecycle (submitted, acknowledged, rejected, cancelled), flatten operations, and execution-related failures or blocks.

Execution events originate from:
- **NinjaTrader callbacks**: `Account.ExecutionUpdate` fires when a fill occurs
- **Adapter logic**: Order submission, flatten, protective order placement
- **Coordinator**: Intent exposure state changes on entry/exit fills
- **Journal**: Persistence of fills for idempotency and audit

### 1.2 Event Type Comparison

| Event Type | Canonical? | Diagnostic? | Consumed by Ledger? | Written to Journals? | In frontend_feed.jsonl? |
|------------|------------|-------------|---------------------|---------------------|-------------------------|
| **EXECUTION_FILLED** | ✓ Yes | No | ✓ Yes (exit fills) | No (journal is separate) | ✓ Yes |
| **EXECUTION_EXIT_FILL** | No (legacy) | Migration/backfill | ✓ Yes (converted to synthetic EXECUTION_FILLED) | No | ✓ Yes |
| **INTENT_EXIT_FILL** | No | Coordinator internal | No (ledger uses EXECUTION_FILLED only) | No | ✓ Yes |
| **BROKER_FLATTEN_FILL_RECOGNIZED** | N/A | Yes (no longer emitted) | No | No | No (not in LIVE_CRITICAL) |

**Clarifications:**
- **EXECUTION_FILLED**: Canonical fill event. Emitted for entry, exit (STOP/TARGET), and broker flatten fills. Contains `order_type` (ENTRY, STOP, TARGET, FLATTEN).
- **EXECUTION_EXIT_FILL**: Legacy. Robot now emits EXECUTION_FILLED for exits. Kept in LIVE_CRITICAL for backfill; ledger converts to synthetic EXECUTION_FILLED when no canonical fill exists for that order.
- **INTENT_EXIT_FILL**: Emitted by `InstrumentIntentCoordinator.OnExitFill`. Ledger no longer uses it for PnL; can be deprecated after migration.
- **BROKER_FLATTEN_FILL_RECOGNIZED**: Was diagnostic; no longer emitted. Broker flatten fills now route through `ProcessBrokerFlattenFill` and emit EXECUTION_FILLED.

### 1.3 Flow Diagram (Text)

```
NinjaTrader fill (ExecutionUpdate callback)
    │
    ▼
RobotSimStrategy.OnExecutionUpdate
    │ (ExecutionUpdateRouter: account + execution_instrument_key → endpoint)
    ▼
IEA.EnqueueExecutionUpdate (or HandleExecutionUpdate if no IEA)
    │
    ▼
NinjaTraderSimAdapter.ProcessExecutionUpdate / HandleExecutionUpdateReal
    │ (Decode tag → intentId, orderType; broker flatten recognition)
    ▼
ProcessExecutionUpdateContinuation
    │ (ResolveIntentContextOrFailClosed; entry vs exit branch)
    ├── ENTRY: RecordEntryFill, OnEntryFill, EmitEntryFill (EXECUTION_FILLED)
    ├── STOP/TARGET: RecordExitFill, OnExitFill, EXECUTION_FILLED
    └── Broker flatten: ProcessBrokerFlattenFill → EXECUTION_FILLED per intent
    │
    ▼
RobotLogger.Write(RobotEvents.ExecutionBase(...))
    │
    ▼
RobotLoggingService.Log → queue → FlushBatch
    │
    ▼
robot_<instrument>.jsonl (or robot_ENGINE.jsonl for ENGINE events)
    │
    ▼
EventFeedGenerator.process_new_events (watchdog)
    │ (Filter: LIVE_CRITICAL_EVENT_TYPES only; rate-limit some events)
    ▼
frontend_feed.jsonl
    │
    ▼
LedgerBuilder._load_execution_fills (Phase 3.1: reads robot_*.jsonl; feed is UI-only)
    │ (EXECUTION_FILLED, EXECUTION_PARTIAL_FILL, EXECUTION_EXIT_FILL backfill)
    ▼
Ledger rows (journal + exit fills → PnL)

Execution journals (separate path):
    RecordEntryFill / RecordExitFill → data/execution_journals/{trading_date}_{stream}_{intent_id}.json
```

---

## 2. Entry Fill Path (Step-by-Step)

### 2.1 Detection

**Where first detected**: NinjaTrader `Account.ExecutionUpdate` callback in `RobotSimStrategy.OnExecutionUpdate` (`RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs`).

**Routing**: `ExecutionUpdateRouter.TryGetEndpoint(accountName, orderInstrumentKey)` routes to the correct adapter. With IEA: `_iea.EnqueueExecutionUpdate(execution, order)`.

### 2.2 Processing Method

**Primary method**: `NinjaTraderSimAdapter.HandleExecutionUpdateReal` → `ProcessExecutionUpdateContinuation` (`RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`).

**Entry branch**: When `orderTypeForContext == "ENTRY"` (or not STOP/TARGET), the entry fill path runs.

### 2.3 Events Emitted

- **EXECUTION_FILLED** (full fill) or **EXECUTION_PARTIAL_FILL** (partial)
- **INTENT_FILL_UPDATE** (per-fill accounting)
- **AGG_ENTRY_FILL_ALLOCATED** (when aggregated: CL1+CL2 same price)

### 2.4 Payload Fields (EXECUTION_FILLED for Entry)

- `order_id`, `intent_id`, `instrument`, `execution_instrument_key`, `side` (BUY/SELL)
- `order_type` (ENTRY), `fill_price`, `fill_quantity`, `filled_total`, `remaining_qty`
- `order_quantity`, `broker_order_id`, `stream`, `stream_key`, `trading_date`
- `account`, `session_class`, `source` ("robot")

### 2.5 intent_id Assignment

- Decoded from order tag via `RobotOrderIds.DecodeIntentId(encodedTag)`.
- Tag format: `QTSW2:{intentId}` for entry, `QTSW2:{intentId}:STOP` or `:TARGET` for protective orders.

### 2.6 Allocation (Aggregated Fills)

- **Single intent**: One `EXECUTION_FILLED` with that intent's `fill_quantity`.
- **Aggregated** (CL1+CL2 same price): `AllocateFillToIntents` splits fill across intents; **one EXECUTION_FILLED per intent** via `EmitEntryFill` local function with `allocQty` and `allocFilledTotal`.

### 2.7 Journal vs Feed vs Ledger

- **Execution journal**: `RecordEntryFill` per intent → `data/execution_journals/{trading_date}_{stream}_{intent_id}.json`
- **robot_<instrument>.jsonl**: All events via RobotLoggingService
- **frontend_feed.jsonl**: Only if event type in `LIVE_CRITICAL_EVENT_TYPES` (EXECUTION_FILLED is included)
- **Ledger builder**: Uses journals for entry; EXECUTION_FILLED used for **exit** fills only

---

## 3. Exit Fill Path (Stop / Target)

### 3.1 Detection

Same as entry: `ExecutionUpdate` callback. Tag decoded; if `:STOP` or `:TARGET`. Order may not be in OrderMap; `OrderInfo` created from tag if protective order.

### 3.2 Event Emitted

**EXECUTION_FILLED** (not EXECUTION_EXIT_FILL). `order_type` = "STOP" or "TARGET".

### 3.3 Payload

- `fill_price`, `fill_quantity`, `filled_total`, `remaining_qty` = 0
- `trading_date` from `context.TradingDate`
- `execution_instrument_key`, `side`, `account`, `stream_key`, `session_class`, `source`

### 3.4 RecordExitFill

Called before `_log.Write`: `_executionJournal.RecordExitFill(intentId, tradingDate, stream, fillPrice, fillQuantity, orderTypeForContext, utcNow)`.

### 3.5 Coordinator

`_coordinator?.OnExitFill(intentId, fillQuantity, utcNow)` → emits `INTENT_EXIT_FILL` (coordinator internal; ledger does not use it).

### 3.6 Ledger vs Feed

- Ledger: Consumes EXECUTION_FILLED with `order_type` in STOP/TARGET/FLATTEN
- Feed: EXECUTION_FILLED in LIVE_CRITICAL → passes through

### 3.7 Divergence from Entry

- Exit: `RecordExitFill` + `OnExitFill`; single event per fill (no aggregation for protective orders)
- Entry: `RecordEntryFill` + `OnEntryFill`; per-intent emission when aggregated

---

## 4. Broker Flatten Path

### 4.1 Recognition

When `intentId` is empty (no tag) and we recently called `Flatten()` for that instrument:

- `_lastFlattenInstrument` matches (via `ExecutionInstrumentResolver.IsSameInstrument`)
- Within `FLATTEN_RECOGNITION_WINDOW_SECONDS` of `_lastFlattenUtc`

**Location**: `HandleExecutionUpdateReal` (before untracked-fill flatten path)

### 4.2 Handler

**ProcessBrokerFlattenFill** (`NinjaTraderSimAdapter.NT.cs` ~2690):

1. Get active exposures: `_coordinator.GetActiveExposuresForInstrument(instrument)` (fallback to `execInstKey` for MES/ES mismatch)
2. If no exposures: emit `EXECUTION_FILL_UNMAPPED` (CRITICAL), return
3. Allocate: `AllocateFlattenFillToExposures` (proportional by remaining exposure)
4. Per intent: `RecordExitFill`, `OnExitFill`, `_log.Write(EXECUTION_FILLED)` with `order_type=FLATTEN`

### 4.3 Current Behavior (Canonical)

- **EXECUTION_FILLED** emitted per intent
- **RecordExitFill** called
- **OnExitFill** triggered
- Written to journal (via RecordExitFill)
- Reaches feed (EXECUTION_FILLED in LIVE_CRITICAL)
- PnL reconstructable from feed for mapped flatten fills

### 4.4 Unmapped Case

If no exposures match: `EXECUTION_FILL_UNMAPPED` emitted; no EXECUTION_FILLED, no RecordExitFill. PnL gap.

---

## 5. trading_date Handling

### 5.1 Where Computed

- **Intent context**: `ResolveIntentContextOrFailClosed` → `context.TradingDate` from `intent.TradingDate`
- **Engine**: `_activeTradingDate` from timetable
- **ProcessBrokerFlattenFill**: `intent.TradingDate ?? ""`

### 5.2 Where It May Be Null

- Coordinator `OnExitFill` → `INTENT_EXIT_FILL` with `tradingDate: ""` (coordinator has no trading_date in scope)
- `ResolveIntentContextOrFailClosed` fails if trading_date is null/empty → `EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL` (fail-closed)
- ProcessBrokerFlattenFill: if `trading_date` null, emits `EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL`, skips that intent

### 5.3 Event Types Including trading_date

- EXECUTION_FILLED: in payload (`trading_date`, `stream_key`)
- RobotLogger promotes `trading_date` from `data` to top-level when missing (`RobotLogger.ConvertToRobotLogEvent`)

### 5.4 Filtering

- Ledger: `_load_execution_fills` filters by `event_trading_date == trading_date`
- Journal: `RecordExitFill` rejects empty `tradingDate` → `EXECUTION_JOURNAL_VALIDATION_FAILED`, fail-closed

### 5.5 Null trading_date

- Adapter: Fail-closed; no EXECUTION_FILLED emitted for that fill
- Ledger: Invariant requires `trading_date` non-null; raises `LedgerInvariantViolation`

---

## 6. execution_instrument_key and Account Attribution

### 6.1 Origin

- **IEA**: `_iea.ExecutionInstrumentKey` — from `InstrumentExecutionAuthority` constructor (`accountName`, `executionInstrumentKey`)
- **Fallback**: `ExecutionUpdateRouter.GetExecutionInstrumentKeyFromOrder(order.Instrument)`
- Resolved at adapter init: `ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(accountName, instrument, engineExecutionInstrument)`

### 6.2 In Fill Payloads

- All EXECUTION_FILLED (entry, exit, flatten) include `execution_instrument_key` and `account` (P1 enrichment)
- `account` from `_iea?.AccountName ?? ExecutionUpdateRouter.GetAccountNameFromOrder(order)`

### 6.3 Implications

- **Multi-account**: Each (account, execution_instrument_key) has separate IEA; fills attributed per endpoint
- **Replay**: Replay uses `ProcessExecutionUpdateCore`; determinism depends on same execution events producing same state

---

## 7. Allocation / Multi-Intent Fills

### 7.1 Entry Aggregation

- One broker fill can satisfy multiple intents (e.g. CL1+CL2 same price)
- `AllocateFillToIntents` allocates by lexicographic intent order
- **One EXECUTION_FILLED per intent** with that intent's `allocQty`

### 7.2 Feed Reflection

- Feed has one event per intent; allocation is explicit

### 7.3 Ledger

- **Entry**: From journal (`RecordEntryFill` per intent)
- **Exit**: From EXECUTION_FILLED events
- Event stream can independently reconstruct per-intent PnL for exits; entry relies on journal

---

## 8. Live Feed Filtering

### 8.1 LIVE_CRITICAL_EVENT_TYPES

Defined in `modules/watchdog/config.py`. Includes:

- **EXECUTION_FILLED**: ✓
- **EXECUTION_PARTIAL_FILL**: ✓
- **EXECUTION_EXIT_FILL**: ✓ (migration/backfill)
- **EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL**: ✓
- **EXECUTION_FILL_UNMAPPED**: ✓
- **INTENT_EXIT_FILL**: ✓

### 8.2 Filtering Logic

`EventFeedGenerator._process_event`:
- Only events in `LIVE_CRITICAL_EVENT_TYPES` pass
- Requires `run_id`, `timestamp_utc`
- Rate-limited: ENGINE_TICK_CALLSITE (5s), BAR_RECEIVED_NO_STREAMS (60s)

### 8.3 frontend_feed.jsonl Guarantee

- **Not guaranteed** to contain all fills from raw logs: only LIVE_CRITICAL events pass
- EXECUTION_FILLED, EXECUTION_PARTIAL_FILL, EXECUTION_EXIT_FILL are in LIVE_CRITICAL → all canonical fills that reach robot logs should reach feed (subject to watchdog processing)

---

## 9. Journal vs Event Stream

### 9.1 Execution Journals

- **Path**: `data/execution_journals/{trading_date}_{stream}_{intent_id}.json`
- **Content**: Entry/exit fills, submission records, trade completion
- **Purpose**: Idempotency, audit, PnL source for entry quantities and prices

### 9.2 robot_<instrument>.jsonl

- **Path**: `logs/robot/robot_{instrument}.jsonl` (or `robot_ENGINE.jsonl`)
- **Content**: All events routed by RobotLoggingService (by instrument)
- **Purpose**: Raw log; watchdog reads from here

### 9.3 frontend_feed.jsonl

- **Path**: `logs/robot/frontend_feed.jsonl`
- **Content**: LIVE_CRITICAL events only, with `event_seq`, `timestamp_chicago`
- **Purpose**: Watchdog UI, ledger builder (for exit fills), event processor

### 9.4 Canonical vs Redundant

- **Journals**: Canonical for entry fills and idempotency
- **Event stream (feed)**: Canonical for exit fills (EXECUTION_FILLED)
- **Redundancy**: Entry in both journal and EXECUTION_FILLED; ledger uses journal for entry, events for exit

### 9.5 PnL from Feed Alone

- **Today**: No. Ledger requires **both** journals (entry) and EXECUTION_FILLED (exit). Entry quantities/prices come from journal.
- **Why**: Journal has `RecordEntryFill` with contract multiplier, direction; event stream has fill events but ledger joins on journal for entry data.

---

## 10. Determinism & Replay Compatibility

### 10.1 Replay Engine

- **ReplayDriver**: Processes `ReplayEventType.ExecutionUpdate` → `_iea.ProcessExecutionUpdateCore(eu)`
- **InstrumentExecutionAuthority.Replay.cs**: `ProcessExecutionUpdateCore` injects execution into same flow as live
- Replay depends on execution events being replayed in order; EXECUTION_FILLED is a **downstream log**, not a replay input. Replay input is `ReplayExecutionUpdate` (execution, order, time, qty, etc.)

### 10.2 Missing Flatten Fills

- If broker flatten fill is **mapped** (ProcessBrokerFlattenFill): EXECUTION_FILLED emitted → replay would need equivalent ReplayExecutionUpdate to reproduce
- If **unmapped**: EXECUTION_FILL_UNMAPPED; no EXECUTION_FILLED → accounting gap; replay would not see the close

### 10.3 Accounting from Event Logs

- Ledger builds from journals + frontend_feed EXECUTION_FILLED
- Replay does not rebuild ledger from logs; it replays execution/order events into IEA. Determinism: same replay input → same IEA state

### 10.2 execution_sequence in Replay (Phase 3.3)

- **ReplayExecutionUpdate.ExecutionSequence**: Optional. When present (e.g. from incident pack derived from EXECUTION_FILLED), preserved. When absent, ReplayDriver assigns in processing order per `executionInstrumentKey`.
- **Determinism**: Same event stream → same execution_sequence assignment. Enables future "rebuild ledger from replay" or incident-pack audit to produce bit-for-bit identical PnL.

### 10.4 Current Gaps

- Untracked/unknown order fills: no EXECUTION_FILLED; fail-closed before canonical path
- Unmapped broker flatten: EXECUTION_FILL_UNMAPPED; no fill recorded
- INTENT_EXIT_FILL has no trading_date (coordinator scope)

---

## 11. Gap Summary Table

| Category | Current Behavior | Impact | Severity | Replay Impact | Ledger Impact |
|----------|------------------|--------|----------|--------------|--------------|
| **Broker flatten early-return** | Fixed: ProcessBrokerFlattenFill emits EXECUTION_FILLED | Was: PnL gap. Now: canonical for mapped | Was P0 | Replay needs execution events for flatten | Now correct for mapped |
| **Missing execution_instrument_key** | Fixed: P1 enrichment adds to all EXECUTION_FILLED | Was: schema gap | Was P1 | N/A | Invariant requires for exit |
| **Missing side** | Fixed: side (BUY/SELL) in payload | Was: schema gap | Was P1 | N/A | Invariant requires for exit |
| **Missing account** | Fixed: account in payload | Was: schema gap | Was P1 | N/A | Attribution | 
| **Aggregated entry fills** | Fixed: per-intent EXECUTION_FILLED | Was: single event with wrong qty | P1 | N/A | Journal correct; feed now correct |
| **trading_date null cases** | Fail-closed: EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL | No EXECUTION_FILLED for that fill | P1 | N/A | Ledger rejects null |
| **Unmapped fills** | EXECUTION_FILL_UNMAPPED emitted; no EXECUTION_FILLED | PnL gap for unmapped flatten | P0 | State inconsistent | No exit record |
| **Event filtering issues** | Only LIVE_CRITICAL in feed | Non-critical events dropped | Low | N/A | EXECUTION_* fills in LIVE_CRITICAL |
| **Untracked fill** | EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL; flatten; no EXECUTION_FILLED | No intent_id; cannot emit fill | P0 | N/A | Cannot reconcile |
| **Unknown order fill** | EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL; flatten; no EXECUTION_FILLED | Fail-closed before path | P0 | N/A | Cannot reconcile |
| **Untracked/Unknown not in feed** | EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL, EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL not in LIVE_CRITICAL | Stay in robot_*.jsonl only | Low | N/A | N/A |

---

## 12. Key File Reference

| Area | File | Key Methods / Types |
|------|------|---------------------|
| Fill handling | `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` | `HandleExecutionUpdateReal`, `ProcessExecutionUpdateContinuation`, `ProcessBrokerFlattenFill`, `AllocateFlattenFillToExposures`, `DeriveSessionClass`, `EmitEntryFill` |
| Execution journal | `RobotCore_For_NinjaTrader/Execution/ExecutionJournal.cs` | `RecordEntryFill`, `RecordExitFill`, `GetJournalPath` |
| Event emission | `RobotCore_For_NinjaTrader/RobotLogger.cs` | `RobotEvents.ExecutionBase`, `ConvertToRobotLogEvent` |
| Log routing | `RobotCore_For_NinjaTrader/RobotLoggingService.cs` | `Log`, `FlushBatch`, `WriteBatchToFile` |
| Feed generation | `modules/watchdog/event_feed.py` | `EventFeedGenerator.process_new_events`, `_process_event`, `_is_live_critical_event` |
| Config | `modules/watchdog/config.py` | `LIVE_CRITICAL_EVENT_TYPES`, `FRONTEND_FEED_FILE`, `EXECUTION_JOURNALS_DIR` |
| Ledger | `modules/watchdog/pnl/ledger_builder.py` | `_load_execution_fills`, `_validate_fill_invariants`, `_build_ledger_row` |
| Schema | `modules/watchdog/pnl/schema.py` | `normalize_execution_filled` |
| Coordinator | `RobotCore_For_NinjaTrader/Execution/InstrumentIntentCoordinator.cs` | `OnExitFill` (emits INTENT_EXIT_FILL) |
| Strategy | `RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs` | `OnExecutionUpdate` |
| IEA | `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` | `EnqueueExecutionUpdate`, `ProcessExecutionUpdate` |
| Replay | `RobotCore_For_NinjaTrader/Execution/ReplayDriver.cs` | `ProcessEvent` (ExecutionUpdate → ProcessExecutionUpdateCore) |

---

## 13. Non-Goals (Document Scope)

- No proposed fixes
- No refactoring
- No code changes
- Purely explanatory and architectural
