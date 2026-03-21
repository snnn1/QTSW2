# Execution Lifecycle — Consolidated Reference

**Date:** 2026-03-12  
**Context:** Execution-hardening cycle (breakout decision, OCO, recovery, forced flatten, re-entry).

---

## 1. Initial Submission (Slot-Time)

**When:** Immediately after `TryLockRange` succeeds and state transitions to `RANGE_LOCKED`.

**Path:** `TryLockRange` → `SubmitStopEntryBracketsAtLock(utcNow)`.

**Gates:**
- Freshness window: `(utcNow - SlotTimeUtc).TotalMinutes <= initial_submission_freshness_minutes` (default **3** when omitted from spec). Set **`breakout.initial_submission_freshness_minutes` to `0`** (or negative) in the parity spec JSON to **disable** the time-based block; **`NO_TRADE_MATERIALLY_DELAYED_INITIAL_SUBMISSION`** is not used for delay-from-slot in that case.
- Price sanity: not "clearly stale" (long: `ask < brkLong + sanityTolerance`; short: `bid > brkShort - sanityTolerance`) — still applies when freshness is disabled.
- Breakout levels present, before market close.

**Decision:** `GetCurrentMarketPrice` → if `ask >= brkLong` → MARKET BUY; if `bid <= brkShort` → MARKET SELL; else STOP.

**Logging:** `ENTRY_ORDER_TYPE_DECISION`, `EXECUTION_METRIC_INITIAL_SUBMISSION_ALLOWED`.

**OCO:** Both long and short share same `ocoGroup`; NinjaTrader cancels opposite side when one fills.

---

## 2. Delayed Initial Submission (Blocked)

**When:** First submission attempt is materially delayed from slot time.

**Block reasons:**
- `INITIAL_SUBMISSION_BLOCKED_MATERIALLY_DELAYED`: `freshness_minutes > 0` and `delay_from_slot_minutes > freshness_minutes`.
- `INITIAL_SUBMISSION_BLOCKED_PRICE_SANITY`: price clearly beyond breakout (sanity_ticks).

**Outcome:** `Commit(utcNow, "NO_TRADE_MATERIALLY_DELAYED_INITIAL_SUBMISSION" | "NO_TRADE_INITIAL_SUBMISSION_PRICE_SANITY", ...)`.

**Note:** Delayed resubmit/restart retry uses separate path (`HandleRangeLockedState` + `IsBreakoutValidForResubmit`).

---

## 3. Recovery Resubmit (Phase B)

**When:** `_entryOrderRecoveryState.Action == ResubmitClean` (e.g. reconciliation detected missing/invalid orders).

**Path:** `HandleRangeLockedState` → `ExecutePendingRecoveryAction(snap, utcNow)` → `SubmitStopEntryBracketsAtLock(utcNow)`.

**Preconditions:**
- Stream in `RANGE_LOCKED`, not committed, before market close.
- Position flat.
- No valid entry orders on broker.
- `IsBreakoutValidForResubmit(utcNow) == true` (price within tolerance of breakout).

**Decision:** Same as initial — MARKET vs STOP based on `GetCurrentMarketPrice` vs breakout levels.

**Logging:** `ENTRY_ORDERS_RESUBMITTED`, `ENTRY_ORDER_TYPE_DECISION`.

---

## 4. Restart Retry

**When:** Stream restarts with `_stopBracketsSubmittedAtLock == false` (e.g. previous placement failed).

**Path:** `HandleRangeLockedState` → RESTART RECOVERY block → `SubmitStopEntryBracketsAtLock(utcNow)`.

**Preconditions:**
- `!_stopBracketsSubmittedAtLock`, `!_entryDetected`, before market close.
- Range and breakout levels available.
- Intents not already submitted (idempotency).
- `IsBreakoutValidForResubmit(utcNow) == true`.

**If breakout crossed beyond tolerance:** `Commit(utcNow, "NO_TRADE_RESTART_RETRY_BREAKOUT_ALREADY_TRIGGERED", ...)`.

---

## 5. Forced Flatten

**When:** Session close (or fallback) triggers engine-level forced flatten.

**Effect:**
- All positions flattened.
- `ExecutionInterruptedByClose = true` on affected streams.
- Entry orders cancelled (broker OCO or explicit cancel).
- Stream remains `RANGE_LOCKED` but marked for re-entry.

**Skip restore:** When `ExecutionInterruptedByClose`, `RestoreRangeLockedFromHydrationLog` skips (avoids overwriting re-entry state).

**Logging:** `FORCED_FLATTEN_TRIGGERED`.

---

## 6. Re-Entry (Post Forced Flatten)

**When:** Market reopens; stream has `ExecutionInterruptedByClose == true` and `!ReentrySubmitted`.

**Path:** `HandleRangeLockedState` / re-entry logic → `SubmitMarketReentryCommand` (or equivalent).

**Effect:** New market order to re-establish position; `ReentrySubmitted = true`.

**Note:** Re-entry is time-based (market open), not bar-based.

---

## 7. Mixed STOP/MARKET Behavior

**Decision logic (in `SubmitStopEntryBracketsAtLock`):**
- `longCrossed = ask.HasValue && ask.Value >= brkLong` → long side uses MARKET.
- `shortCrossed = bid.HasValue && bid.Value <= brkShort` → short side uses MARKET.
- Otherwise STOP.

**OCO:** Both orders share same `ocoGroup`; when one fills, NinjaTrader cancels the other.

**Journal:** `RecordSubmission` receives `ocoGroup` for both MARKET and STOP (modules, RobotCore, NT_ADDONS).

**Protective orders:** Same path for MARKET and STOP entry fills — `HandleEntryFill` → `ExecuteSubmitProtectives`.

**Rejection handling:** If "price outside limits" on STOP, `ENTRY_ORDER_REJECTED_PRICE_INVALID` logged; pre-submit MARKET substitution prevents this when breakout already crossed.

---

## Event Summary

| Event | When | Outcome |
|-------|------|---------|
| Initial submission | Slot-time, within freshness | STOP or MARKET (if crossed) |
| Delayed initial blocked | Beyond freshness / price sanity | NO_TRADE commit |
| Recovery resubmit | ResubmitClean, flat, valid breakout | Resubmit brackets |
| Restart retry | Restart, brackets not submitted | Retry or NO_TRADE if crossed |
| Forced flatten | Session close | Flatten, ExecutionInterruptedByClose |
| Re-entry | Market open after forced flatten | Market re-entry order |
| Mixed STOP/MARKET | Breakout crossed at submission | MARKET + STOP, same OCO |

---

## Related Docs

- `MIXED_STOP_MARKET_ENTRY_AUDIT.md` — OCO, intent tracking, protective orders.
- `ORDER_CANCELLATION_ROOT_CAUSE_AUDIT.md` — Cancellation and OCO sibling handling.
- `TIMETABLE_HASH_AND_RESTART_RECOVERY_AUDIT.md` — Restart and recovery flows.
