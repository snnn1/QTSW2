# IEA Implementation Gaps and Decisions

This document captures structural inconsistencies, gaps, and required decisions identified during the Phase 1–3 implementation. It serves as the spec for hardening and completing the Instrument Execution Authority (IEA) design.

---

## 1. Serialization Plan Drift (CRITICAL)

**Current state:** Phase 3 uses `lock (_beLock)` around BE evaluation only.

**Problem:** Locking BE alone does NOT protect from concurrent mutation between:
- Execution updates (fills / partials)
- Bracket resize
- Cancel/replace transitions
- BE evaluation

"Lock BE but not fills" = race-driven nondeterminism.

**Decision required:** Choose one:

| Option | Mechanism | Robustness |
|--------|-----------|------------|
| **A (preferred)** | Single per-instrument **queue** that serializes ALL broker-state mutations (fills, bracket, BE, flatten, cancels) | Cleanest for audit/replay; deterministic ordering |
| **B** | Single per-instrument **execution authority lock** that wraps ALL broker-state mutations | Simpler; must be one lock, not BE-specific |

**Rule:** If lock is chosen, it must be a single "execution authority lock", not a BE-specific lock.

**Action:** Implement queue (Option A) or consolidate to one `_executionLock` used by HandleExecutionUpdate, HandleEntryFill, bracket resize, BE evaluation, Flatten, Cancel.

---

## 2. Deterministic OCO Naming — Identity and Time Basis

**Current formula:** `QTSW2:{ExecutionInstrumentKey}_{tradingDate}_{intentId}_PROTECTIVE_A{attempt}`

**Gaps:**

### 2a. Trading date definition
- **Must be explicit:** timezone and rollover rule.
- **Rule:** Use America/Chicago (same as Translator world).
- **Problem:** CME futures trading day ≠ UTC day. UTC midnight causes collisions/flips during evening session.
- **Action:** Define `ResolveTradingDate(utcNow, instrument, sessionClass)` using Chicago rollover; use consistently for OCO seed.

### 2b. IntentId uniqueness scope
- **If intentId is globally unique (GUID-ish):** OK.
- **If intentId is per-strategy-instance or per-stream only:** Must include stream identity (or globally unique intent key) in OCO seed.

**Hard rule:** OCO group name must be deterministic and collision-free across:
- Multiple charts
- Restarts
- Rehydration
- Forced flatten reentry logic

**Recommendation:** If `SlotInstanceKey` or intent lifecycle identity exists in journals, use that as seed instead of `intentId` when `intentId` can be regenerated.

---

## 3. Canonical Ledger — Precise Definition

**Current state:** "IEA becomes canonical ledger" is stated but not defined. Risk: accidental queries to `Position.MarketPosition`, per-order tags, per-chart order collections → split truth.

**IEA-owned truth must include (minimum):**

| Field | Description |
|-------|-------------|
| Net filled qty by execution instrument | (and avg price if needed) |
| Working stop order id(s) and price(s) | |
| Working target order id(s) and price(s) | |
| **Bracket state machine** | NONE / SUBMITTING / WORKING / RESIZING / CANCEL_REPLACE_PENDING / FAILED_CLOSED |
| **BE state** | NOT_ARMED / ARMED / MOVED / BLOCKED_TRANSITIONAL |
| **Transitional barriers** | "No BE while bracket transitional" — enforced by ledger |

**Action:** Define `IEA.GetPositionState()` and `IEA.GetBracketState()` returning these; gate Strategy `Position.MarketPosition` and stream stop-tag access when IEA enabled.

---

## 4. Idempotency Contract for Protective Submission

**Risk:** Double-submission on multiple execution callbacks, partial fills, disconnect/reconnect, restart/hydration.

**IEA must track per intent (or slot instance):**
- Entry submitted? (id)
- Entry filled qty so far
- Stop submitted? (id)
- Target submitted? (id)
- Last known working prices

**Rule:** Refuse to resubmit unless in a defined recovery path.

**Migration bridge — "shadow detect and adopt":**
- If protectives exist and match expected bracket policy → IEA adopts them.
- If they exist but mismatch → IEA emits CRITICAL mismatch event and fail-closes (or blocks trading for that instrument).

**Action:** Add adoption logic and idempotency checks before any protective submission.

---

## 5. Aggregation Policy — TIGHTEST Safety Envelope

**Current:** TIGHTEST stop-side validation (long stop < entry, short stop > entry) exists.

**Additional guardrails:**

### 5a. TIGHTEST = best protection, not closest price
- **Long:** Highest stop price *below* entry is tightest (closest to entry but valid).
- **Short:** Lowest stop price *above* entry is tightest.

### 5b. Bracket tolerance scope
- Tolerance must apply to **both** stop AND target.
- Define: Does 1-tick tolerance apply independently to stop and target?
- Define: Does it apply to entry price? (Important for stop-side validation on aggregated orders.)

**Action:** Document in config schema; enforce in `TryAggregateWithExistingOrders`.

---

## 6. Break-Even Trigger Policy — FIRST_TO_TRIGGER Under Aggregation

**Ambiguity:** With multiple intents aggregated into one position, "first to trigger" can mean:

| Option | Semantics |
|--------|-----------|
| A | First intent reaches its trigger threshold → move stop for whole net position |
| B | Any tick satisfies trigger for at least one intent → move stop for whole net position (≈ A if triggers differ) |
| C | First time net position's R-multiple reaches threshold based on chosen reference entry |

**Key question:** What is the trigger keyed to?
- Per-intent entry price? (but net position has blended fills)
- Per-fill weighted average entry?
- Primary intent only?

**Institutional approach:** Trigger off the **net position's effective entry price (WAP)** in the ledger. Keep per-intent attribution in journals; execution actions happen on the net position.

**Rule:** Per-intent triggers with a net stop → inconsistent semantics. Avoid.

**Action:** Decide and implement; document in BE policy.

---

## 7. Rollback Path — Explicit Contract

**Current claim:** "Set use_instrument_execution_authority: false to revert."

**Reality check:** Rollback works only if:
- All call sites still support legacy adapter-owned maps ✓
- Strategy BE logic still exists OR adapter has equivalent path ✓ (adapter has `EvaluateBreakEvenNonIEA`)
- No new required fields only produced by IEA path

**Clarification:** Strategy BE logic (`CheckBreakEvenTriggersTickBased`) was **removed**, but adapter retains `EvaluateBreakEvenNonIEA` for the non-IEA path. When `use_instrument_execution_authority: false`, adapter runs its own BE loop. So rollback **does** restore legacy BE.

**Explicit contract:**
- **Full rollback:** IEA disabled → adapter uses own maps, adapter runs BE via `EvaluateBreakEvenNonIEA`. Strategy only calls `adapter.EvaluateBreakEven`; adapter branches.
- **Phase 1-only rollback:** If adapter's legacy BE were removed, rollback would NOT restore BE. We did NOT do that.

**Action:** Document rollback contract in migration guide; ensure no "dormant but deleted" code paths.

---

## 8. Expected Effects — Add Recovery / Ops

**Add to summary:**

### Recovery
- **Restart:** IEA can rehydrate state from execution journals and/or open orders and continue idempotently.
- **Disconnect:** IEA blocks mutation until it can reconcile broker open orders vs ledger.
- **Multi-chart:** IEA prevents duplicate submission and duplicate BE moves by construction.

### Observability
- **Authority heartbeat:** Single per-instrument event for watchdog:
  - Active IEA instance id
  - Last exec update processed
  - Last mutation processed
  - Queue depth (if queue model)

---

## 9. Phase 2 Spec Deliverable — Five Items to Lock

Before further implementation, lock these five items:

| # | Item | Status |
|---|------|--------|
| 1 | **Serialization model** | Queue (strongly preferred) or single execution lock |
| 2 | **IEA ledger schema** | PositionState + BracketState + BEState + transitional flags |
| 3 | **Idempotency rules** | Per intent/slot identity; adoption vs fail-closed when broker orders detected |
| 4 | **Deterministic OCO seed** | trading_date definition (timezone + rollover); intent/slot identity scope |
| 5 | **Adoption vs fail-closed** | Behavior when existing broker orders are detected at startup/reconnect |

Once these are defined, Phase 2 implementation becomes mechanical rather than fragile.

---

## Implementation Checklist (Next Steps)

- [ ] Choose serialization model (queue vs single lock); implement
- [ ] Define and implement IEA ledger schema (GetPositionState, GetBracketState)
- [ ] Add idempotency checks and adoption logic for protectives
- [ ] Fix OCO seed: trading_date (Chicago), slot/intent identity
- [ ] Document TIGHTEST and bracket tolerance semantics
- [ ] Decide FIRST_TO_TRIGGER semantics under aggregation; implement
- [ ] Add authority heartbeat event
- [ ] Document rollback contract explicitly
