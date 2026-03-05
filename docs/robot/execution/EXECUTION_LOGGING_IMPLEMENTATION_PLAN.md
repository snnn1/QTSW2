# Execution Logging — Implementation Plan

**Date**: 2026-03-03  
**Spec**: [EXECUTION_LOGGING_CANONICAL_SPEC.md](./EXECUTION_LOGGING_CANONICAL_SPEC.md)

**Phase 1 Status**: ✓ Complete (Steps 1.1–1.8)

---

## Overview

This plan sequences implementation by dependency. Each step is testable before moving on.

---

## Phase 1: Adapter Infrastructure + Unmapped Fills

### Step 1.1 — Add execution_sequence and fill_group_id infrastructure

**Where**: `NinjaTraderSimAdapter` (base + NT partial)

**What**:
1. Add per-adapter state:
   - `_executionSequenceByInstrumentKey: Dictionary<string, int>` — monotonic counter per `execution_instrument_key`
   - Thread-safe increment (lock or Interlocked) when emitting any fill
2. Add helper:
   ```csharp
   string ComputeFillGroupId(string orderId, string brokerOrderId, string timestampUtc, decimal fillPrice, int fillQty)
   // SHA256(orderId + "|" + brokerOrderId + "|" + timestampUtc + "|" + fillPrice + "|" + fillQty).Substring(0, 16)
   ```
3. For NT: get broker execution id from `execution.ExecutionId` if available; use as fill_group_id when present, else use deterministic hash

**Files**: `NinjaTraderSimAdapter.cs`, `NinjaTraderSimAdapter.NT.cs`

**Test**: Unit test `ComputeFillGroupId` is deterministic for same inputs.

---

### Step 1.2 — Extend EXECUTION_FILLED payload (mapped fills)

**Where**: All `_log.Write(RobotEvents.ExecutionBase(..., "EXECUTION_FILLED", ...))` call sites

**What**:
1. Before each emission, get next `execution_sequence` for this `execution_instrument_key`
2. Compute `fill_group_id` (broker exec id or hash)
3. Add to payload: `execution_sequence`, `fill_group_id`, `order_id` (internal), `broker_order_id` (NT OrderId)
4. Add `position_effect`: "OPEN" for entry, "CLOSE" for exit (STOP/TARGET/FLATTEN)
5. Add `mapped: true` explicitly

**Call sites** (from architecture doc):
- Entry: `ProcessExecutionUpdateContinuation` → `EmitEntryFill` (~2385)
- Exit STOP/TARGET: `ProcessExecutionUpdateContinuation` (~2545)
- Broker flatten: `ProcessBrokerFlattenFill` (~2768)

**Files**: `NinjaTraderSimAdapter.NT.cs`

**Test**: Grep logs for EXECUTION_FILLED; verify new fields present.

---

### Step 1.3 — Add EmitUnmappedFill helper

**Where**: `NinjaTraderSimAdapter.NT.cs`

**What**:
```csharp
private void EmitUnmappedFill(
    string instrument,
    string unmappedReason,  // enum: NO_ACTIVE_EXPOSURES, ZERO_REMAINING_EXPOSURE, UNTrackED_TAG, UNKNOWN_ORDER_AFTER_GRACE, INTENT_NOT_FOUND, OTHER
    decimal fillPrice,
    int fillQty,
    DateTimeOffset utcNow,
    object orderId,
    Order order,
    string? ntOrderName = null,
    string? tag = null,
    string? ocoId = null)
{
    var execInstKey = _iea?.ExecutionInstrumentKey ?? ExecutionUpdateRouter.GetExecutionInstrumentKeyFromOrder(order?.Instrument);
    var accountName = _iea?.AccountName ?? ExecutionUpdateRouter.GetAccountNameFromOrder(order);
    var brokerOrderId = orderId?.ToString() ?? "";
    var orderIdInternal = brokerOrderId; // or derive from order if NT has internal id
    var seq = GetNextExecutionSequence(execInstKey);
    var fillGroupId = ComputeFillGroupId(orderIdInternal, brokerOrderId, utcNow.ToString("o"), fillPrice, fillQty);
    var side = "UNKNOWN"; // infer from order if possible, else UNKNOWN

    _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "EXECUTION_FILLED",
        new {
            execution_sequence = seq,
            fill_group_id = fillGroupId,
            order_id = orderIdInternal,
            broker_order_id = brokerOrderId,
            execution_instrument_key = execInstKey,
            side = side,
            order_type = "UNMAPPED",
            fill_price = fillPrice,
            fill_quantity = fillQty,
            trading_date = "", // unmapped: may not have it
            account = accountName,
            source = "robot",
            mapped = false,
            unmapped_reason = unmappedReason,
            nt_order_name = ntOrderName,
            tag = tag,
            oco_id = ocoId
        }));
}
```

**Files**: `NinjaTraderSimAdapter.NT.cs`

---

### Step 1.4 — Replace EXECUTION_FILL_UNMAPPED returns with EmitUnmappedFill + keep behavior

**Where**: `ProcessBrokerFlattenFill` (two early-returns)

**Current**:
- No exposures → log EXECUTION_FILL_UNMAPPED, return
- Zero remaining → log EXECUTION_FILL_UNMAPPED, return

**Target**:
- No exposures → **EmitUnmappedFill**(instrument, "NO_ACTIVE_EXPOSURES", ...), then log EXECUTION_FILL_UNMAPPED (or keep as diagnostic), return
- Zero remaining → **EmitUnmappedFill**(instrument, "ZERO_REMAINING_EXPOSURE", ...), then return

**Files**: `NinjaTraderSimAdapter.NT.cs`

---

### Step 1.5 — Untracked fill path (no tag)

**Where**: `HandleExecutionUpdateReal` — when `string.IsNullOrEmpty(intentId)` and NOT broker flatten

**Current**: Log EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL, flatten, return

**Target**: **EmitUnmappedFill**(instrument, "UNTrackED_TAG", ..., tag: encodedTag), then existing flatten + return

**Files**: `NinjaTraderSimAdapter.NT.cs`

---

### Step 1.6 — Unknown order path (grace expired)

**Where**: `TriggerUnknownOrderFlatten` / grace-expired path

**Current**: Log EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL, flatten, return

**Target**: **EmitUnmappedFill**(instrument, "UNKNOWN_ORDER_AFTER_GRACE", ..., tag: encodedTag) with order info we have, then flatten

**Files**: `NinjaTraderSimAdapter.NT.cs`

---

### Step 1.7 — trading_date enforcement

**Where**: All fill emission paths

**What**:
- Before emitting mapped fill: if `trading_date` is null/empty → emit EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL, do NOT emit EXECUTION_FILLED, fail-closed (flatten if applicable)
- Already partially done in ProcessBrokerFlattenFill (continue skips)
- Ensure ResolveIntentContextOrFailClosed fails closed with trading_date evidence when null

**Files**: `NinjaTraderSimAdapter.NT.cs`

---

### Step 1.8 — Add unmapped_reason to RobotEventTypes + config

**What**:
- Ensure EXECUTION_FILLED with mapped=false is in LIVE_CRITICAL (it's the same event type)
- Add EXECUTION_FILL_UNMAPPED to LIVE_CRITICAL if not already (for diagnostic visibility)

**Files**: `RobotEventTypes.cs`, `modules/watchdog/config.py`

---

## Phase 2: Ledger Builder

### Step 2.1 — Schema: normalize_execution_filled

**Where**: `modules/watchdog/pnl/schema.py`

**What**:
- Extract `execution_sequence`, `fill_group_id`, `order_id`, `broker_order_id`, `position_effect`, `mapped`, `unmapped_reason`
- Handle both mapped and unmapped formats
- Lenient for backfill (old events without new fields)

---

### Step 2.2 — Ledger: use EXECUTION_FILLED for entry

**Where**: `ledger_builder.py`

**What**:
- `_load_execution_fills` already loads EXECUTION_FILLED; extend to include **entry** fills (order_type=ENTRY), not just exit
- Group by intent_id: entry fills vs exit fills
- Implement fallback rule:
  - Entry: use EXECUTION_FILLED if present, else journal, else CRITICAL + incomplete
  - Exit: use EXECUTION_FILLED if present, else journal, else CRITICAL + incomplete

---

### Step 2.3 — Ledger invariants

**Where**: `ledger_builder.py` — `_validate_fill_invariants` or new `_validate_ledger_invariants`

**What**:
- Per intent: `sum(exit_qty) ≤ sum(entry_qty)`
- Entry side defines direction; exit side must be opposite
- `execution_sequence` strictly increasing per `execution_instrument_key`
- On violation: emit CRITICAL, raise LedgerInvariantViolation, stop build

---

## Phase 3: Raw Logs + Hardening

### Step 3.1 — Ledger reads raw logs

**Where**: `ledger_builder.py`

**What**:
- Change `_load_execution_fills` to read from `robot_<instrument>.jsonl` (or glob `robot_*.jsonl`) instead of `frontend_feed.jsonl`
- Filter for EXECUTION_FILLED / EXECUTION_PARTIAL_FILL
- Filter by trading_date, stream as today

---

### Step 3.2 — Journal startup self-check

**Where**: `ExecutionJournal` constructor or engine startup

**What**:
- Verify `_journalDir` exists and is writable
- Write and read a test file `_journal_dir/.startup_check`
- Fail closed (stand down) if not

---

### Step 3.3 — Replay: execution_sequence ✓

**Where**: Replay driver / IEA replay

**What**:
- When replaying execution events, assign `execution_sequence` in same order as live
- Replay must reproduce same sequence for determinism

**Implemented**:
- `ReplayExecutionUpdate.ExecutionSequence` (int?) added; preserved when present from source
- `ReplayDriver.AssignExecutionSequenceIfMissing`: assigns monotonic per `executionInstrumentKey` when missing

---

## Order of Operations (Recommended)

| Order | Step | Depends on |
|-------|------|------------|
| 1 | 1.1 Infrastructure (seq, fill_group_id) | — |
| 2 | 1.2 Extend mapped payload | 1.1 |
| 3 | 1.3 EmitUnmappedFill helper | 1.1 |
| 4 | 1.4 ProcessBrokerFlattenFill unmapped | 1.3 |
| 5 | 1.5 Untracked fill | 1.3 |
| 6 | 1.6 Unknown order | 1.3 |
| 7 | 1.7 trading_date enforcement | — |
| 8 | 1.8 Config | — |
| 9 | 2.1 Schema | — |
| 10 | 2.2 Ledger entry from EXECUTION_FILLED | 2.1 |
| 11 | 2.3 Ledger invariants | 2.2 |
| 12 | 3.1 Raw logs | 2.2 |
| 13 | 3.2 Journal check | — |
| 14 | 3.3 Replay sequence | 1.1 |

---

## Phase 4: Audit + Determinism + Monitoring (Correct Order)

**Order matters.** Structural guarantee first, then deterministic proof, then runtime hygiene.

### Step 4.1 — Early-Return Audit (Structural Guarantee) ✓

**Goal**: Zero execution path exits without canonical emission.

**Deliverable**: Formal checklist — "All fill-handling code paths produce canonical or unmapped fill."

**Scope**: No behavior change. Pure integrity hardening.

**Reference**: EXECUTION_LOGGING_CANONICAL_SPEC §8

**Implemented**:
- `ProcessExecutionUpdateContinuation` trading_date null: `EmitUnmappedFill("TRADING_DATE_NULL")` before return
- `ProcessBrokerFlattenFill` Intent not in IntentMap: `EmitUnmappedFill("INTENT_NOT_FOUND")` before continue
- `ProcessBrokerFlattenFill` trading_date null: `EmitUnmappedFill("TRADING_DATE_NULL")` before continue
- Added `TRADING_DATE_NULL` to unmapped_reason enum

---

### Step 4.2 — Incident-Pack Replay Tool (Deterministic Proof) ✓

**Goal**: Given robot logs → rebuild ledger → identical PnL.

**Deliverable**: Command-line tool:
```
python scripts/rebuild_ledger_from_logs.py --date 2026-03-03
python scripts/rebuild_ledger_from_logs.py --date 2026-03-03 --out snapshot.json
python scripts/rebuild_ledger_from_logs.py --date 2026-03-03 --compare snapshot.json
```
Outputs: Ledger rows (in snapshot), hash of PnL, comparison vs snapshot. Audit switch: run twice → same hash.

**Implemented**: `scripts/rebuild_ledger_from_logs.py`

---

### Step 4.3 — Monitoring Metrics (Operational Confidence) ✓

**Goal**: Live observability.

**Track daily**:
- `fill_coverage_rate` (must be 100%)
- `unmapped_rate` (target 0)
- `null_trading_date_rate` (target 0)
- `invariant_violation_count` (target 0)

Runtime hygiene.

**Implemented**:
- `modules/watchdog/pnl/fill_metrics.py` — `compute_fill_metrics(trading_date, stream)`
- `scripts/fill_metrics_daily.py` — standalone metrics (--date, --json)
- `scripts/rebuild_ledger_from_logs.py --metrics` — metrics + rebuild

---

## Sync Note

Per `.cursor/rules/robotcore-sync.mdc`: edits in `RobotCore_For_NinjaTrader/` should be synced to `modules/robot/core/` (and vice versa). Apply changes in the canonical location and sync.

---

## Testing Strategy

1. **Unit**: `ComputeFillGroupId` deterministic; `GetNextExecutionSequence` monotonic
2. **Integration**: Run strategy, trigger entry fill → verify new fields in logs
3. **Unmapped**: Simulate untracked fill (order with no tag) → verify EXECUTION_FILLED(mapped=false) in logs
4. **Ledger**: Rebuild ledger for a test day → verify no invariant violations
5. **Replay**: Replay same day twice → same PnL bit-for-bit

### Automated Test Script

```powershell
.\scripts\test_execution_logging.ps1 -Date 2026-03-03
```

Runs: rebuild ledger, determinism check, fill metrics, rebuild with metrics. Exit 0 = pass.

### Hash Stability (PnL Determinism)

The rebuild_ledger_from_logs PnL hash must be deterministic across runs. Ensured by:
- **Sorted rows** by (stream, intent_id)
- **Sorted keys** in JSON (`sort_keys=True`)
- **Fixed float precision** (round to 2 decimals)
- **Integer fields as int** (no 1.0 vs 1 in JSON)
- **No timestamps** in hash (avoids formatting variance)
- **ensure_ascii=True** (no locale-dependent Unicode)
