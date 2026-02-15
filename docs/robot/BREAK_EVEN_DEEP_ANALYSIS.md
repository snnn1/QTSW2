# Break-Even Detection — Deep Analysis

**Date:** 2026-02-13  
**Scope:** Answers to determinism, latency, performance, and design questions

---

## 1. Determinism & State Integrity

### Q1 — Can BE ever trigger twice for the same intent?

**No.** Idempotency is enforced at multiple layers:

| Layer | Mechanism |
|-------|-----------|
| **GetActiveIntentsForBEMonitoring** | Skips intents where `_executionJournal.IsBEModified(intentId, tradingDate, stream)` returns true |
| **ModifyStopToBreakEven** | Checks `IsBEModified` before calling `ModifyStopToBreakEvenReal`; returns early with "BE modification already attempted" |
| **ExecutionJournal** | `BEModified` flag is persisted to disk; survives restart |

**Intent-level vs inferred:** BE state is stored explicitly in `ExecutionJournal` (`BEModified`, `BEStopPrice`, `BEModifiedAt`). It is not inferred from stop price. `RecordBEModification` is called only after `Change()` succeeds.

---

### Q2 — What happens if stop modification fails mid-flight?

**BE is only considered active after broker confirmation.**

Flow in `ModifyStopToBreakEvenReal`:
1. Call `account.Change(new[] { stopOrder })`
2. Check `result` — if Rejected or null, return `FailureResult` (no journal update)
3. **Only on success:** Call `_executionJournal.RecordBEModification(...)`

If `Change()` throws or returns failure:
- `RecordBEModification` is never called
- `IsBEModified` remains false
- Next BE check (within 1s) will retry

**Partial state:** There is no partial state. Either we record BE (success) or we don't (failure). The stop order's in-memory `StopPrice` may be updated before `Change()` is called, but NinjaTrader's `Change()` is synchronous — we get the result before returning.

---

### Q3 — Is BE state stored in ExecutionJournal or only inferred?

**Stored in ExecutionJournal.** Persisted to `{tradingDate}_{stream}_{intentId}.json` with:
- `BEModified = true`
- `BEModifiedAt` (timestamp)
- `BEStopPrice`

**Restart behavior:** On NT restart, `IsBEModified` reads from disk (or cache). If `BEModified` was true, the intent is excluded from `GetActiveIntentsForBEMonitoring`. BE will not re-trigger.

---

## 2. Latency & Missed Trigger Edge Cases

### Q4 — Price crosses BE trigger and reverses within one 1-second bar

**Simulation:**
- Entry = 100, BE trigger = 106.5
- Bar trades 106.6 then closes 105.9
- Price source: `Closes[1][0]` = **105.9**
- Check: `105.9 >= 106.5` → **false**
- **BE does NOT trigger**

**Frequency:** Depends on volatility. For instruments like NQ/ES with fast reversals, this can occur multiple times per day. For slower instruments (GC, CL), less often. No historical quantification exists in the codebase — would require log analysis or backtest.

---

### Q5 — Throttle: last scan or last modify?

**They are independent:**

| Throttle | Measured from | Purpose |
|----------|---------------|---------|
| **BE_SCAN_THROTTLE_MS (200ms)** | Last scan (`_lastBeCheckUtc`) | Min interval between full intent scans |
| **BE_MODIFY_ATTEMPT_INTERVAL_MS (200ms)** | Last modify *attempt* per intent (`_lastBeModifyAttemptUtcByIntent[intentId]`) | Min interval between modify attempts for same intent |

**Current flow:** `RunBreakEvenCheck` is called every 1 second (OnBarUpdate throttle). So we enter `CheckBreakEvenTriggersTickBased` at most once per second. The 200ms scan throttle would only matter if we moved to tick-based (OnMarketData) — then we'd skip scans until 200ms elapsed.

**Modify throttle:** If we attempt modify at T=0 and it fails, we won't retry the same intent until T=200ms. The next scan can run at T=1000ms (next RunBreakEvenCheck). So we'd retry on that scan.

---

## 3. Performance & UI Thread Safety

### Q6 — What runs inside RunBreakEvenCheck()?

```
RunBreakEvenCheck()
  → CheckBreakEvenTriggersTickBased(price, utcNow)
```

**Breakdown:**

| Operation | Cost | Notes |
|-----------|------|-------|
| `_lastTickPriceForBE = tickPrice` | O(1) | Latch |
| `(utcNow - _lastBeCheckUtc).TotalMilliseconds` | O(1) | Throttle check |
| Cache check `_cachedActiveIntentsForBE` | O(1) | Reference + age check |
| `GetActiveIntentsForBEMonitoring(executionInstrument)` | O(n_intents) | When cache stale; see Q7 |
| `foreach` over activeIntents | O(n_active) | Per-intent work |
| Per intent: direction check, price compare, tick size, `ModifyStopToBreakEven` | O(1) each | |
| `LogEngineEvent` (rate-limited) | O(1) | Dict alloc when logging |
| `Stopwatch` | O(1) | |

**Allocations:**
- `GetActiveIntentsForBEMonitoring` returns new `List<>` when cache stale
- `LogEngineEvent` with `new Dictionary<string, object>` (rate-limited: 1/sec for BE_EVALUATION_TICK, 60s for BE_PATH_ACTIVE)
- BE_TRIGGER_REACHED / BE_TRIGGER_FAILED: `new Dictionary` per event (only when trigger reached or failed)

**LINQ:** `GetActiveIntentsForBEMonitoring` uses `foreach` over `_intentMap` — no LINQ. `account.Orders.FirstOrDefault` in `ModifyStopToBreakEvenReal` — one LINQ per modify attempt.

**Snapshot calls:** None in BE path. `GetAccountSnapshot` is not used for BE.

**Verdict:** O(n_active_intents) per scan. Main extra cost: list allocation when cache stale, plus dictionary allocs for logging.

---

### Q7 — Does GetActiveIntentsForBEMonitoring allocate new lists each call?

**Yes.** `var activeIntents = new List<...>();` every call.

**Mitigation:** Result is cached for 200ms (`BE_INTENTS_CACHE_MS`). With 1-second BE check interval, we call it at most ~5 times per second per strategy when in position. 7 strategies × 5 = 35 list allocs/sec. Moderate GC pressure.

**Improvement:** Could use a pooled/reused list, or return `IReadOnlyList` from a pre-allocated buffer. Not critical at current frequency.

---

## 4. Instrument Isolation

### Q8 — Can two streams share the same execution instrument simultaneously?

**Yes.** Example: ES1 and ES2 both on MES. Each stream has its own intent and intentId.

**BE isolation:** Stop orders are tagged by `RobotOrderIds.EncodeStopTag(intentId)` → `QTSW2:{intentId}:STOP`. `ModifyStopToBreakEvenReal` finds the stop by intentId. Each intent has a unique stop order. Modification is per-intent, not per-instrument.

**Verdict:** BE correctly isolates per intent. No ambiguity.

---

## 5. Risk Model Integrity

### Q9 — What if BE stop price is worse than the current stop?

**No guard.** The code does not compare `beStopPrice` to the current stop order's `StopPrice` before modifying. It always sets `stopOrder.StopPrice = (double)beStopPrice`.

**Scenario:** If some other logic (e.g. trailing stop, manual adjustment) moved the stop tighter than BE, we would overwrite it with BE (entry ± 1 tick), which could be worse.

**Current design:** There is no other trailing logic in the robot. The only stop modifications are BE and forced flatten (which cancels). So in practice this may not occur. But if you add trailing stops or external modifications, this becomes a risk.

**Recommendation:** Add guard: for Long, only modify if `beStopPrice > currentStopPrice`; for Short, only if `beStopPrice < currentStopPrice`. "Only tighten" rule.

---

## 6. Race Conditions

### Q10 — BE triggers while fill event is being processed?

**No shared lock** between fill processing and BE check. Both run on the UI thread (NinjaTrader) but not necessarily under a common lock.

**Sequence:**
1. Entry fills → `OnExecutionUpdate` → `HandleEntryFill` → journal updated (`EntryFilled = true`), stop/target submitted
2. BE check runs → `GetActiveIntentsForBEMonitoring` → `GetEntry` + `EntryFilled` check

**Race:** If BE runs before journal is updated, we see `EntryFilled = false` and skip the intent. No double-modify. If BE runs after, we see filled and proceed. Journal writes are under `ExecutionJournal._lock`. So we get a consistent read.

**BE before stop submitted?** `HandleEntryFill` submits stop synchronously. By the time we return, stop is in `account.Orders`. Journal `EntryFilled` is set after protective orders are submitted. So when we see `EntryFilled`, the stop should exist. Edge case: if journal is updated before stop is in `account.Orders` (e.g. async submission), we could get "Stop order not found" — which is handled as retry (BE_TRIGGER_RETRY_NEEDED).

**Verdict:** No lock, but journal is source of truth and updates are ordered. Failures result in retry, not corruption.

---

## 7. Statistical Validation (Q11 — Quant Level)

**Not answerable from code.** Requires log analysis:

- Parse execution journals and event logs for last 500 trades
- For each BE_TRIGGER_REACHED, compute `seconds_since_entry = BE_time - entry_fill_time`
- Histogram: within 2s, 5s, 10s, 30s+

**If most BE triggers occur 30+ seconds after entry:** The tick-vs-bar debate has limited impact. A 1-second bar close would likely have captured the trigger. The 5s→1s throttle change matters more than bar vs tick.

**If many triggers occur within 2–5 seconds:** Tick-based detection would help. Bar-based can miss fast reversals.

---

## 8. Philosophical / System Design (Q12)

**Current ownership:**
- **Trigger computation:** StreamStateMachine (engine layer)
- **Detection:** RobotSimStrategy (strategy layer)
- **Execution:** NinjaTraderSimAdapter (adapter layer)

**Three-layer flow.** BE detection runs in `OnBarUpdate` (strategy), which is UI-thread-bound and bar-driven.

**Collapsing into engine tick:**
- **Pros:** Single place for BE logic; engine already has `Tick(utcNow)` with price context; could use engine's price feed; reduces strategy complexity; more deterministic (engine is authoritative).
- **Cons:** Engine doesn't have direct access to `Closes[1][0]` or tick price — it receives bars via `OnBar`. Engine would need a price feed. Currently the engine gets bars, not ticks. Moving BE to engine would require the engine to receive price updates (e.g. from strategy) or the strategy to pass price into `Tick()`. The engine's `Tick` is called with `utcNow` only, not price.

**Reality:** The engine's `Tick` is time-based. Bar/price data flows through `OnBar` → `engine.OnBar` → streams. BE needs price. The strategy has price (from chart). So either:
1. Strategy passes price to engine (e.g. `engine.Tick(utcNow, lastPrice)`) — engine does BE
2. Strategy keeps BE — current design

**Recommendation:** Keeping BE in strategy is reasonable because it has direct chart/price access. Moving to engine would require an API change (price in Tick). The main improvement is tick-based detection (OnMarketData) in the strategy — no need to move to engine for that.

---

## Summary Table

| Question | Answer |
|----------|--------|
| Q1 Duplicate BE | No; journal + adapter guard |
| Q2 Change() failure | No journal update; retry on next check |
| Q3 Restart | BE state in journal; no re-trigger |
| Q4 Cross & reverse | BE can miss; no trigger |
| Q5 Throttles | Independent; scan from last scan, modify from last attempt |
| Q6 RunBreakEvenCheck cost | O(n_active); list + dict allocs |
| Q7 List alloc | Yes; cached 200ms |
| Q8 Two streams same instrument | Isolated per intent |
| Q9 BE worse than current stop | No guard; add "only tighten" |
| Q10 Fill/BE race | Journal ordering; retry on failure |
| Q11 BE timing stats | Requires log analysis |
| Q12 Engine vs strategy | Strategy has price; engine would need API change |
