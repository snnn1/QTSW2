# Execution Logging — Canonical Fill Spec & Refactor Roadmap

**Date**: 2026-03-03  
**Status**: Specification (target state)  
**Prerequisite**: [EXECUTION_LOGGING_ARCHITECTURE_FULL.md](./EXECUTION_LOGGING_ARCHITECTURE_FULL.md)

---

## 1. End-State: Non-Negotiable Invariants

### Invariant A — Canonical Fill Record

Every broker fill produces **one or more** canonical records: **one per allocated intent**. All records from the same broker fill share a common `fill_group_id` tying them back to the original fill. (Single-intent fills have one record; aggregated fills have one per intent.)

| Field | Required | Notes |
|-------|----------|-------|
| `execution_sequence` | ✓ | Monotonic integer per `execution_instrument_key`. **Scope**: per execution_instrument_key only — NOT per strategy instance, NOT per stream, NOT global across instruments. Multi-account: key = account\|execution_instrument_key. Replay must reproduce same sequence. |
| `fill_group_id` | ✓ | Deterministic: `hash(order_id + broker_order_id + timestamp_utc + fill_price + fill_qty)` or broker execution id if available. Must be reproducible under replay (no random UUID). |
| `order_id` | ✓ | Internal order identifier (stable within robot logs; used for correlation) |
| `broker_order_id` | ✓ or "" | External/broker/exchange identifier if available |
| `intent_id` | ✓ or explicit unmapped | Null when `mapped=false` |
| `instrument` | ✓ | |
| `execution_instrument_key` | ✓ | |
| `side` | ✓ | BUY/SELL |
| `order_type` | ✓ | ENTRY, STOP, TARGET, FLATTEN, or UNMAPPED |
| `fill_price` | ✓ | |
| `fill_qty` | ✓ | |
| `timestamp_utc` | ✓ | |
| `trading_date` | ✓ | Non-null |
| `account` | ✓ | |
| `stream_key` | ✓ or "" | |
| `session_class` | ✓ or "" | S1, S2, etc. |
| `position_effect` | Optional | OPEN/CLOSE; recommended for reversals, partial flatten+re-enter |

**fill_group_id rule**: Must be **reproducible under replay**. Do not use random UUID — replay would not match live logs and bit-for-bit determinism would break. Use deterministic hash of `order_id + broker_order_id + timestamp_utc + fill_price + fill_qty`, or broker execution id if the platform provides one.

### Invariant B — No Silent Loss

If a fill **cannot be attributed** to an intent:

- Still write a canonical record with `mapped=false`
- Include full broker identifiers: `order_id`, `broker_order_id`, `instrument`, `account`, `execution_instrument_key`, `fill_price`, `fill_qty`, `timestamp_utc`
- **`unmapped_reason`** must be an enum (see §7); include any identifiers available: NT order name, tag, OCO id
- Trigger fail-closed + flatten + trading halt as today
- **No accounting hole**: ledger sees the fill even if unmapped; unmapped fills become actionable incidents

### Invariant C — Single Source of Truth

PnL reconstruction can be done from:

- `robot_<instrument>.jsonl` (or a dedicated `fills.jsonl` stream)

**Without requiring journals.**

Journals become **redundancy**, not required truth.

---

## 2. Fix P0 Truth Gaps

### 2.A Unmapped Fills Must Be Ledger-Visible

**Today**: `EXECUTION_FILL_UNMAPPED` → no `EXECUTION_FILLED` → accounting hole.

**Target**:

1. Emit `EXECUTION_FILLED` with:
   - `mapped=false`
   - `intent_id=null` (or absent)
   - `order_type=UNMAPPED` (or `UNKNOWN`)
2. Include: `order_id`, `broker_order_id`, `instrument`, `account`, `execution_instrument_key`, `fill_qty`, `fill_price`, `timestamp_utc`
3. **`unmapped_reason`** must be an enum (e.g. `NO_ACTIVE_EXPOSURES`, `UNTrackED_TAG`, `UNKNOWN_ORDER_AFTER_GRACE`)
4. Include any identifiers available: NT order name, tag, OCO id (turns unmapped fills into actionable incidents)
5. Keep fail-closed + flatten + trading halt behavior

**Effect**: Removes accounting hole while preserving fail-closed safety.

### 2.B No Early-Return Without Canonical Emission

**Audit rule**: Every `return`/`throw` in fill processing must satisfy:

> "Before returning, either emit canonical fill OR emit canonical unmapped fill."

**Paths to audit** (from `EXECUTION_LOGGING_ARCHITECTURE_FULL.md`):

- `HandleExecutionUpdateReal` → untracked fill (no tag) → flatten path
- `HandleExecutionUpdateReal` → broker flatten recognition → `ProcessBrokerFlattenFill`
- `ProcessBrokerFlattenFill` → no exposures → `EXECUTION_FILL_UNMAPPED` (today: return; target: emit unmapped fill first)
- `ProcessExecutionUpdateContinuation` → `ResolveIntentContextOrFailClosed` fails → orphan path
- Unknown order after grace period → `TriggerUnknownOrderFlatten`
- Duplicate execution skip

**Action**: For each early-return, ensure canonical (or unmapped) fill is emitted before return.

---

## 3. Unify Entry and Exit Accounting (P1)

### Current State

- **Entry**: Journals (canonical)
- **Exit**: `EXECUTION_FILLED` (canonical)

### Target State

- **EXECUTION_FILLED** is canonical for **both** entry and exit
- Journals remain for redundancy + idempotency

### Actions

1. **Entry emission** (already done per UNIFY_FILL_EVENTS):
   - One `EXECUTION_FILLED` per intent allocation (no aggregation misattribution)
   - Always include: `side`, `account`, `execution_instrument_key`, `trading_date`, `stream_key`, `session_class`

2. **Ledger builder** — explicit fallback rule (no silent "best effort" merges):

   **Entry**:
   - If entry `EXECUTION_FILLED` exists → use it
   - Else if journal entry exists → use journal
   - Else → mark trade as incomplete, emit CRITICAL

   **Exit**:
   - If exit `EXECUTION_FILLED` exists → use it
   - Else if journal has exit data → use journal
   - Else → mark trade as incomplete, emit CRITICAL

---

## 4. Provably Correct (P1/P2)

### 4.A Invariants at Two Layers

**At emission time (robot)**:

| Invariant | Action on violation |
|-----------|----------------------|
| `fill_price` present, finite, and > 0 for supported instruments | Fail-closed, emit CRITICAL |
| `fill_qty > 0` | Fail-closed, emit CRITICAL |
| `trading_date` not null | Fail-closed, emit CRITICAL |
| `execution_instrument_key` not null | Fail-closed, emit CRITICAL |
| `order_type` recognized OR `mapped=false` | Fail-closed, emit CRITICAL |

**At ledger build time**:

| Invariant | Action on violation |
|-----------|----------------------|
| Per intent: `sum(exit_qty) ≤ sum(entry_qty)` | Emit CRITICAL, stop ledger build |
| Trade closes iff `exit_qty == entry_qty` | — |
| No negative qty | Emit CRITICAL, stop ledger build |
| Timestamps non-decreasing per intent | Emit CRITICAL, stop ledger build |
| **Entry side defines direction; exit side must be opposite of entry side** | Emit CRITICAL, stop ledger build (catches silent reversals and mis-attributions) |
| **`execution_sequence` strictly increasing per `execution_instrument_key`** | Emit CRITICAL, stop ledger build (prevents race-condition nondeterminism when timestamps collide) |

**On violation**: Emit `CRITICAL` invariant event; stop ledger build; optionally block trading if live integrity break.

### 4.B Determinism Pack

For each incident day:

1. Replay fills
2. Rebuild ledger
3. Produce **same realized PnL** bit-for-bit

This is the **audit switch**.

---

## 5. Reduce Operational Fragility (P2)

### 5.A Stop Depending on frontend_feed.jsonl for Accounting

**Today**: Ledger reads `frontend_feed.jsonl` (filtered, can lag).

**Target** (explicit; avoid drift back to feed-based accounting):

| Source | Role |
|--------|------|
| **Ledger input** | Canonical `EXECUTION_FILLED` events from **raw robot logs** (`robot_<instrument>.jsonl`) or a dedicated `fills.jsonl` stream |
| **Journals** | Redundancy only (fallback per §3) |
| **Feed** (`frontend_feed.jsonl`) | UI only — dashboards, alerts. Never used for accounting. |

### 5.B Make Journaling Path Explicit and Validated

**Startup self-check**:

1. "Is `execution_journals` root correct?"
2. "Can we write and read a test journal file?"

**Fail closed** if not.

---

## 6. Implementation Roadmap

### Phase 1 (1–2 sessions)

| Task | Description |
|------|--------------|
| 1.1 | Canonicalize unmapped fills → `EXECUTION_FILLED(mapped=false)` |
| 1.2 | Remove any early-return that skips canonical emission |
| 1.3 | Guarantee `trading_date` always present or fail-closed with evidence |

**Files**: `NinjaTraderSimAdapter.NT.cs`, `RobotEventTypes.cs`, `config.py` (LIVE_CRITICAL)

### Phase 2 (2–4 sessions)

| Task | Description |
|------|-------------|
| 2.1 | Ledger builder consumes `EXECUTION_FILLED` for entry + exit |
| 2.2 | Journals become fallback only |
| 2.3 | Add invariants + CRITICAL violations at emission and ledger build |

**Files**: `ledger_builder.py`, `schema.py`, adapter emission paths

### Phase 3 (Ongoing hardening)

| Task | Description |
|------|-------------|
| 3.1 | Ledger reads raw logs, not feed |
| 3.2 | Incident-pack replay: "rebuild ledger from logs" tool |
| 3.3 | Monitoring: fill coverage rate, unmapped rate, null trading_date rate (must be 0) |

**Correct order for 3.2/3.3 and audit** (see Implementation Plan Phase 4):
1. **Early-Return Audit** — Structural guarantee first (no behavior change)
2. **Incident-Pack Replay Tool** — Deterministic proof (audit switch)
3. **Monitoring Metrics** — Runtime hygiene (operational confidence)

---

## 7. Event Schema: EXECUTION_FILLED (Canonical)

### Mapped Fill

```json
{
  "event_type": "EXECUTION_FILLED",
  "ts_utc": "...",
  "intent_id": "...",
  "instrument": "...",
  "data": {
    "execution_sequence": 42,
    "fill_group_id": "deterministic-hash-or-broker-exec-id",
    "order_id": "internal-stable-id",
    "broker_order_id": "external-broker-id-if-available",
    "execution_instrument_key": "...",
    "side": "BUY|SELL",
    "order_type": "ENTRY|STOP|TARGET|FLATTEN",
    "position_effect": "OPEN|CLOSE",
    "fill_price": 123.45,
    "fill_quantity": 1,
    "filled_total": 1,
    "remaining_qty": 0,
    "trading_date": "2026-03-03",
    "account": "...",
    "stream_key": "...",
    "session_class": "S1",
    "source": "robot",
    "mapped": true
  }
}
```

### Unmapped Fill

```json
{
  "event_type": "EXECUTION_FILLED",
  "ts_utc": "...",
  "intent_id": null,
  "instrument": "...",
  "data": {
    "execution_sequence": 42,
    "fill_group_id": "deterministic-hash-or-broker-exec-id",
    "order_id": "internal-stable-id",
    "broker_order_id": "external-broker-id-if-available",
    "execution_instrument_key": "...",
    "side": "BUY|SELL",
    "order_type": "UNMAPPED",
    "fill_price": 123.45,
    "fill_quantity": 1,
    "trading_date": "2026-03-03",
    "account": "...",
    "source": "robot",
    "mapped": false,
    "unmapped_reason": "NO_ACTIVE_EXPOSURES",
    "nt_order_name": "...",
    "tag": "...",
    "oco_id": "..."
  }
}
```

### unmapped_reason Enum

| Value | Meaning |
|-------|---------|
| `NO_ACTIVE_EXPOSURES` | Broker flatten recognized but no exposures to map |
| `ZERO_REMAINING_EXPOSURE` | Exposures exist but zero remaining |
| `UNTrackED_TAG` | Fill has missing/invalid tag; cannot decode intent |
| `UNKNOWN_ORDER_AFTER_GRACE` | Order not in OrderMap after grace period |
| `INTENT_NOT_FOUND` | Intent resolved but not in IntentMap |
| `TRADING_DATE_NULL` | trading_date null/empty; cannot emit mapped fill |
| `OTHER` | Fallback (include note) |

---

## 8. Checklist: Early-Return Audit ✓

**Invariant**: All fill-handling code paths produce canonical or unmapped fill before return.

| Location | Path | Status |
|----------|------|--------|
| `HandleExecutionUpdateReal` | Duplicate skip | ✓ OK — no fill (duplicate already processed) |
| `HandleExecutionUpdateReal` | Broker flatten recognized | ✓ OK — `ProcessBrokerFlattenFill` emits |
| `HandleExecutionUpdateReal` | Untracked (no tag) | ✓ OK — `EmitUnmappedFill` then flatten |
| `HandleExecutionUpdateReal` | Unknown order (grace expired) | ✓ OK — `TriggerUnknownOrderFlatten` → `EmitUnmappedFill` |
| `HandleExecutionUpdateReal` | Wrong instance skip | ✓ OK — delegated to correct instance |
| `ProcessBrokerFlattenFill` | No exposures | ✓ OK — `EmitUnmappedFill` first |
| `ProcessBrokerFlattenFill` | Zero remaining | ✓ OK — `EmitUnmappedFill` first |
| `ProcessBrokerFlattenFill` | Intent not in IntentMap | ✓ OK — `EmitUnmappedFill` (Phase 4.1 fix) |
| `ProcessBrokerFlattenFill` | trading_date null | ✓ OK — `EmitUnmappedFill` (Phase 4.1 fix) |
| `ResolveIntentContextOrFailClosed` | Fails | ✓ OK — `EmitUnmappedFill` first |
| `ProcessExecutionUpdateContinuation` | Intent not found after context | ✓ OK — `EmitUnmappedFill` first |
| `ProcessExecutionUpdateContinuation` | trading_date null | ✓ OK — `EmitUnmappedFill` (Phase 4.1 fix) |

---

## 9. References

- [EXECUTION_LOGGING_ARCHITECTURE_FULL.md](./EXECUTION_LOGGING_ARCHITECTURE_FULL.md) — Current architecture
- [UNIFY_FILL_EVENTS_SUMMARY_FOR_AGENT.md](./UNIFY_FILL_EVENTS_SUMMARY_FOR_AGENT.md) — Prior refactor
- [EXECUTION_LOGGING_GAPS_ASSESSMENT.md](./EXECUTION_LOGGING_GAPS_ASSESSMENT.md) — Gap analysis
