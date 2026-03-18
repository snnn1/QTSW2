# Quant-Level Execution Audit — Breakout System (Post-Fixes)

**Date:** 2026-03-12  
**Scope:** Execution correctness, temporal consistency, price validation, marketable stop behavior, order state integrity, state machine robustness, edge cases, statistical impact, failure modes.

---

## Section 1 — Execution Correctness

### Invariant: "A trade is only taken if the breakout is still valid at the moment of execution."

**Answer: NO — the invariant is NOT enforced uniformly across all entry paths.**

### Path-by-path analysis

| Path | Gate applied? | Code reference |
|------|----------------|-----------------|
| **Initial submission (TryLockRange)** | **NO** | `StreamStateMachine.cs` 5064–5067: `SubmitStopEntryBracketsAtLock(utcNow)` called directly when `utcNow < MarketCloseUtc && !_breakoutLevelsMissing && _brkLongRounded.HasValue && _brkShortRounded.HasValue`. No `IsBreakoutValidForResubmit` call. |
| **Restart retry (HandleRangeLockedState)** | **YES** | `StreamStateMachine.cs` 2522–2539: `if (IsBreakoutValidForResubmit(utcNow)) { SubmitStopEntryBracketsAtLock(utcNow); } else { Commit(..., "NO_TRADE_RESTART_RETRY_BREAKOUT_ALREADY_TRIGGERED"); }` |
| **Recovery resubmit (ExecutePendingRecoveryAction)** | **YES** | `StreamStateMachine.cs` 2905–2910: `if (!IsBreakoutValidForResubmit(utcNow)) { ClearRecoveryAction; Commit(..., "NO_TRADE_RECOVERY_BREAKOUT_ALREADY_TRIGGERED"); return false; }` before any resubmit. |

### Remaining paths where orders can be submitted after breakout is stale without validation

1. **Initial slot-time submission (TryLockRange → SubmitStopEntryBracketsAtLock)**  
   - **Condition:** `utcNow >= SlotTimeUtc`, range locked, breakout levels present.  
   - **Behavior:** Orders submitted immediately. No price check.  
   - **Design intent:** Slot-time submission is treated as “at signal time”; marketable stops are allowed.  
   - **Quant impact:** If slot-time is delayed (e.g. bar arrives late, clock skew), submission can occur after breakout has already moved. The system assumes slot-time ≈ signal time.

2. **No other unguarded submission paths** in `StreamStateMachine.cs` for entry orders.

### Exact code paths

- **Initial (unguarded):**  
  `TryLockRange` (≈4990) → `Transition(RANGE_LOCKED)` (5034) → try block (5047) → `SubmitStopEntryBracketsAtLock(utcNow)` (5066).  
  Gate: `utcNow < MarketCloseUtc`, `!_breakoutLevelsMissing`, `_brkLongRounded.HasValue`, `_brkShortRounded.HasValue`. No `IsBreakoutValidForResubmit`.

- **Restart retry (guarded):**  
  `HandleRangeLockedState` (2479) → `!alreadySubmitted` (2521) → `IsBreakoutValidForResubmit(utcNow)` (2522) → `SubmitStopEntryBracketsAtLock` (2534) or `Commit` (2538).

- **Recovery resubmit (guarded):**  
  `ExecutePendingRecoveryAction` (2875) → `IsBreakoutValidForResubmit(utcNow)` (2905) → `SubmitStopEntryBracketsAtLock` (2936) or `Commit` (2908).

---

## Section 2 — Temporal Consistency

### Time bounds

| Bound | Where used | Code reference |
|-------|------------|----------------|
| **MarketCloseUtc** | Entry cutoff | `utcNow >= MarketCloseUtc` in `ExecutePendingRecoveryAction` (2879), `HandleRangeLockedState` (2544), `TryLockRange` (5064), `HandleRangeBuildingState` (2276). |
| **SlotTimeUtc** | Lock gate | `utcNow >= SlotTimeUtc` for `TryLockRange` (2259, 2417). |
| **SlotTimeUtc + 1 min** | Invariant check | `utcNow >= SlotTimeUtc.AddMinutes(1) && !_rangeLocked` → log `SLOT_TIME_PASSED_WITHOUT_RESOLUTION` (2427). |

### Can entry occur significantly after SlotTimeUtc?

**Yes.** There is no max delay from slot time for initial submission. The only hard cutoff is `MarketCloseUtc`. Entry can occur minutes or hours after slot time if:

- Range lock is delayed (e.g. bars arrive late).
- Restart retry runs long after slot time (but this path is gated by `IsBreakoutValidForResubmit`).

### Signal expiration / breakout freshness

- **No explicit concept** of max delay from slot time.
- **No explicit** signal expiration or breakout freshness window.
- Breakout validity is only checked on **delayed resubmit** paths (recovery, restart retry), not on initial submission.

### Quant assessment

- **Assumption:** Signals are treated as time-invariant at slot time; any submission at slot time is considered valid.
- **Validity:** Questionable. If slot-time is delayed (data latency, clock skew, bar batching), the “signal” may no longer be valid. The system does not model this.
- **Gap:** No `max_delay_from_slot` or `signal_expiration_minutes`; only `MarketCloseUtc` bounds the day.

---

## Section 3 — Price Validation Model

### Breakout validity gate (`IsBreakoutValidForResubmit`)

**Location:** `StreamStateMachine.cs` 2808–2869.

### Tolerance

- **Fixed:** 2 ticks.  
  `const int BREAKOUT_VALIDITY_TOLERANCE_TICKS = 2` (2810).  
  `tolerance = BREAKOUT_VALIDITY_TOLERANCE_TICKS * _tickSize` (2816).
- **Not dynamic** (no volatility, spread, or instrument-specific adjustment).

### Price source

- **Method:** `_executionAdapter.GetCurrentMarketPrice(ExecutionInstrument, utcNow)` (2814).
- **Returns:** `(decimal? Bid, decimal? Ask)`.
- **Implementations:**
  - `NullExecutionAdapter`: `(null, null)` → gate fail-open (2811).
  - `NinjaTraderSimAdapter`: `GetCurrentMarketPriceReal` → `marketData.GetBid(0)`, `marketData.GetAsk(0)`; returns `(null, null)` if instrument/marketData null or NaN (`NinjaTraderSimAdapter.NT.cs` 118–148).
  - `NinjaTraderLiveAdapter`: `(null, null)` (stub) (`NinjaTraderLiveAdapter.cs` 220–221).

### Null / latency handling

- **Null bid/ask:** `longInvalid = ask.HasValue && ask.Value >= ...` (2817); `shortInvalid = bid.HasValue && bid.Value <= ...` (2818). If either is null, that side is not invalidated → gate returns `true` (fail-open).
- **Latency:** No explicit handling. Uses snapshot at call time.
- **LIVE adapter:** Always `(null, null)` → gate always fail-open in LIVE.

### Failure behavior

| Context | Behavior |
|---------|----------|
| Adapter null | Fail-open (return true) |
| Breakout levels missing | Fail-open (return true) |
| Bid/ask null | Fail-open (return true) |
| Price beyond tolerance | Fail-closed (return false, log, block) |

### Quant risk

- **Bias:** Fail-open under missing data favors resubmission. Stale breakouts can be resubmitted when price feed is unavailable.
- **Scenarios:** DRYRUN, LIVE stub, or NinjaTrader returning null bid/ask → gate bypassed.
- **2-tick tolerance:** May be too tight for wide-spread instruments or too loose for tight-spread; no instrument-specific tuning.

---

## Section 4 — Marketable Stop Behavior

### Assumption: "Stop orders beyond price fill immediately and are acceptable."

**Code reference:** `StreamStateMachine.cs` 5055–5060:

```csharp
// If price already beyond breakout → stop fills immediately (marketable stop behavior)
// If price not at breakout → stop waits for breakout
```

### Slippage / gap / fill price deviation

- **Slippage:** Recorded in `ExecutionJournal` (347–370) and `ExecutionSummary.RecordExecutionCost` (103–117) for analysis, but **not used** to gate or block entry.
- **Gap size:** No explicit check. Marketable stop fills at market; no cap on how far price has moved.
- **Fill price deviation:** No pre-trade limit on deviation from breakout level.

### Protection against extreme conditions

- **Extreme gap entries:** None. If price has moved 10+ ticks past breakout, stop fills at market with no cap.
- **Illiquid conditions:** None. No spread check, no minimum liquidity gate.

### Quant classification

- **Uncontrolled variance:** Yes. Fill price can differ significantly from breakout level in fast moves or thin markets.
- **Backtest vs live:** Backtests (SIM) use NinjaTrader fill simulation; live fills depend on real liquidity. No explicit modeling of:
  - Slippage distribution
  - Gap size vs fill quality
  - Illiquidity impact

---

## Section 5 — Order State Integrity

### `HasValidEntryOrdersOnBroker`

**Location:** `StreamStateMachine.cs` 2579–2627.

### Guarantees

| Check | Implemented? | Code |
|------|--------------|------|
| Correct intent IDs | Yes | `RobotOrderIds.DecodeIntentId(tag)` matched to `longIntentId`/`shortIntentId` (2601–2603). |
| Correct prices | Partial | `Math.Abs(longOrder.StopPrice.Value - brkLong) > 0.0001m` (2612); **skipped if `StopPrice` is null** (2611: `longOrder.StopPrice.HasValue`). |
| No duplicates | Yes | `longCount != 1 \|\| shortCount != 1` → false (2608). |
| Correct OCO linkage | Yes | `ocoLong != ocoShort` → false (2622). |

### Gap: null `StopPrice`

- If broker snapshot has `StopPrice == null`, price check is skipped and orders can be treated as valid.
- `ClassifyBrokerState` (2746–2749) has the same pattern: `longOrder.StopPrice.HasValue && Math.Abs(...)`.

### System thinks orders exist but they don’t

- Snapshot is from `GetAccountSnapshot(utcNow)`; it reflects broker state at that moment.
- Race: between snapshot and action, orders can be filled/cancelled. No re-check before acting.
- `ExecutePendingRecoveryAction` does re-check `HasValidEntryOrdersOnBroker` (2895) before resubmit; if valid set appears, it clears recovery. So a late fill can prevent duplicate resubmit.

### System resubmits incorrectly

- **Idempotency:** `_executionJournal.IsIntentSubmitted` (2517–2518) used in restart retry to avoid duplicate submission.
- **Recovery:** `ResubmitClean` does not re-check intent journal before `SubmitStopEntryBracketsAtLock`. If recovery runs while orders are in flight, duplicate submission is possible.

### Determinism / idempotency / race safety

- **Deterministic:** Same snapshot + state → same classification. No randomness in logic.
- **Idempotent:** `SubmitStopEntryBracketsAtLock` uses `_stopBracketsSubmittedAtLock` and `CanSubmitStopBrackets` (3688) to block repeat submission.
- **Race conditions:** Snapshot can be stale. Cancel-and-rebuild can race with fills. No locking around snapshot + action.

---

## Section 6 — State Machine Robustness

### Transitions

| From | To | Trigger |
|------|----|---------|
| PRE_HYDRATION | ARMED | Pre-hydration complete (1929, 2147) |
| ARMED | RANGE_BUILDING | Range build start (2243) |
| RANGE_BUILDING | RANGE_LOCKED | TryLockRange success (5034) |
| RANGE_LOCKED | DONE | Commit (2954, 5791, 5870) |
| * | DONE | Market close, breakout invalidated, etc. |
| * | SUSPENDED_DATA_INSUFFICIENT | Data insufficient (543) |

### Determinism across restart / disconnect / recovery

- **Restart:** `RANGE_LOCKED` restored from hydration/ranges log (5465–5538). `_stopBracketsSubmittedAtLock = false` (5497) to force resubmit. Recovery/restart retry then run with breakout validity gate.
- **Disconnect:** No explicit disconnect handling. State persists via journals; on reconnect, restoration logic runs.
- **Recovery:** `AuditAndClassifyEntryOrders` → `SetRecoveryAction` → `ExecutePendingRecoveryAction` on next tick. Gate applied before resubmit.

### Stuck states

- **ARMED forever:** Possible if `RangeStartUtc` never reached or bars never arrive. `RANGE_BUILDING_STUCK_PAST_SLOT_TIME` alert at 10 minutes (1017, 1042).
- **RANGE_LOCKED forever:** Possible if no fill and no market close. Market close check (2544) eventually commits.
- **RANGE_BUILDING:** Stuck detection at 10 minutes (1042); no automatic transition out.

### RANGE_BUILDING persistence ↔ RANGE_LOCKED restore

- `PersistRangeBuildingSnapshot` (5731) writes bars/range when `State == RANGE_BUILDING && !_rangeLocked` (5733).
- On restore: if `LastState == "RANGE_BUILDING"` and `!_rangeLocked`, `RestoreRangeBuildingFromSnapshot` (556–558) runs.
- If RANGE_LOCKED events exist, range lock is restored first (5440–5538); RANGE_BUILDING restore is skipped.
- Interaction is ordered: RANGE_LOCKED restore takes precedence.

---

## Section 7 — Edge Cases (Critical)

### 1. Breakout occurs exactly at slot time (race)

- **Scenario:** Bar closes at slot time with breakout; lock and submission happen in same tick.
- **Behavior:** `TryLockRange` runs at slot time; `SubmitStopEntryBracketsAtLock` submits immediately. No gate. Stops are marketable if price already past breakout.
- **Correctness:** By design. Slot-time submission is treated as valid; marketable fill is accepted.

### 2. Breakout occurs during restart window

- **Scenario:** Restart during RANGE_LOCKED; price has crossed breakout before restart retry.
- **Behavior:** `HandleRangeLockedState` restart retry (2506–2541) calls `IsBreakoutValidForResubmit`. If invalid, `Commit(..., "NO_TRADE_RESTART_RETRY_BREAKOUT_ALREADY_TRIGGERED")`.
- **Correctness:** Correct. Late resubmit is blocked.

### 3. Orders partially exist (one side missing)

- **Behavior:** `GetMatchingEntryOrderCounts` → `longCount != 1 || shortCount != 1` → `BrokerStateClassification.BrokenSet` or `MissingSet`. `AuditAndClassifyEntryOrders` sets `ResubmitClean` or `CancelAndRebuild`.
- **Correctness:** Correct. Partial set triggers recovery.

### 4. Duplicate orders exist

- **Behavior:** `longCount > 1 || shortCount > 1` → `BrokerStateClassification.BrokenSet` → `CancelAndRebuild`. Cancel sent, then `ResubmitClean` on next cycle.
- **Correctness:** Correct. Duplicates trigger cancel-and-rebuild.

### 5. Price feed unavailable during recovery

- **Behavior:** `GetCurrentMarketPrice` returns `(null, null)`. `IsBreakoutValidForResubmit` fail-opens (2817–2818: `ask.HasValue` false → `longInvalid` false).
- **Correctness:** Incorrect for strict validity. Resubmit proceeds when price is unknown; may create late entry.

### 6. Large gap across breakout (10+ ticks)

- **Initial submission:** No gate. Stops submitted; marketable fill at market.
- **Recovery/restart:** Gate uses 2-tick tolerance. If ask >= brkLong + 2 ticks or bid <= brkShort - 2 ticks, blocked. Otherwise allowed.
- **Correctness:** 2-tick tolerance may allow resubmit when price is 1–2 ticks past breakout. Large gaps (10+ ticks) are blocked.

---

## Section 8 — Statistical / Quant Impact

### Bias

| Bias type | Present? | Mechanism |
|----------|----------|-----------|
| **Selection bias** | Possible | Fail-open when price unavailable → more resubmits when data is bad. |
| **Execution bias** | Yes | Initial submission unguarded; delayed paths guarded. Asymmetric treatment by path. |
| **Survivorship bias** | Possible | Blocked resubmits (breakout invalidated) not traded; only “valid” resubmits fill. |

### Before vs after fixes

- **Before:** Delayed resubmit and restart retry could submit after breakout was stale.
- **After:** Both paths use `IsBreakoutValidForResubmit`; stale resubmits are blocked.
- **Unchanged:** Initial slot-time submission remains unguarded.

### Expected effect

| Metric | Direction |
|--------|-----------|
| Win rate | Slight increase (fewer late, bad entries) |
| Average R | Slight improvement (fewer poor fills on stale breakouts) |
| Drawdown | Slight reduction (fewer bad entries) |
| Variance | Reduced (fewer outlier fills from late entries) |

---

## Section 9 — Failure Modes

| Failure mode | Risk | Description |
|--------------|------|-------------|
| Initial submission after delayed slot-time | **HIGH** | No gate; late slot-time can submit after breakout. |
| Price feed null during recovery | **MEDIUM** | Gate fail-open; resubmit when validity unknown. |
| LIVE adapter always (null, null) | **HIGH** | Gate always bypassed in LIVE. |
| Orders with null StopPrice accepted as valid | **MEDIUM** | `HasValidEntryOrdersOnBroker` skips price check. |
| Duplicate resubmit during cancel-and-rebuild race | **LOW** | Cancel in flight; resubmit before confirm. |
| Stuck ARMED (no bars) | **LOW** | No automatic recovery; manual intervention. |
| Stuck RANGE_BUILDING > 10 min | **LOW** | Alert only; no auto-transition. |
| Marketable stop in illiquid market | **MEDIUM** | No liquidity/spread check; poor fills possible. |
| 2-tick tolerance too tight/loose | **LOW** | Instrument-dependent; fixed value. |

---

## Section 10 — Final Quant Rating

| Dimension | Score | Rationale |
|-----------|-------|-----------|
| **Execution correctness** | **6/10** | Delayed paths gated; initial path unguarded. Fail-open when price unavailable. |
| **Robustness** | **7/10** | Restoration, reconciliation, and recovery are structured. Some race and edge cases remain. |
| **Bias control** | **5/10** | Asymmetric gating, fail-open, and no signal expiration. |
| **Production readiness** | **6/10** | Suitable for SIM/DRYRUN with monitoring. LIVE has gate bypass (null price). |

### Single biggest remaining weakness

**Initial slot-time submission has no breakout validity check.** The system assumes slot-time submission is always valid. If slot-time is delayed (data latency, bar batching, clock skew), orders can be submitted after the breakout has already occurred, with no price-based validation. Combined with marketable stop behavior, this can produce late entries with poor fills. The delayed paths (recovery, restart retry) are now protected, but the primary submission path is not.

---

*Audit complete. Code references point to `modules/robot/core/StreamStateMachine.cs` unless otherwise noted.*
