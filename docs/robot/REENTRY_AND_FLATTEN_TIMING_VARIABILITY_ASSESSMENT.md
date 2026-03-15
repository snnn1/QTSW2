# Reentry and Forced Flatten Timing Variability Assessment

**Date:** 2026-03-12  
**Scope:** How forced flatten (5 min before close) and reentry (market open) handle holidays, early closes, and variable schedules.

---

## Executive Summary

| Component | Current Source | Holiday / Early-Close Aware? | Gap |
|-----------|----------------|------------------------------|-----|
| **Forced flatten (primary)** | SessionCloseResolver → NinjaTrader SessionIterator | ✅ Yes | None when cache populated |
| **Forced flatten (fallback)** | Spec `market_close_time` (16:00) + fixed 300s buffer | ❌ No | Wrong on early-close days |
| **Reentry** | Spec `market_reopen_time` (17:00) | ❌ No | Wrong on early-close, delayed-open, holiday |

---

## 1. Forced Flatten (5 Min Before Close)

### Primary Path: SessionCloseResolver (Live)

- **Source:** NinjaTrader `SessionIterator` + `Bars.TradingHours`
- **Logic:** Iterates actual TradingHours segments for the trading day; finds the **structural close** (largest gap between segments). `FlattenTriggerUtc = closeEndUtc - bufferSeconds` (300 = 5 min).
- **Holiday handling:** If no eligible segments → `HasSession=false` → `SESSION_CLOSE_HOLIDAY` → no forced flatten (correct).
- **Early-close handling:** SessionIterator returns actual segment end times. E.g. day-after-Thanksgiving 13:00 close → flatten trigger ~12:55 CT. ✅ Correct when cache is populated.

### Fallback Path (Cache Miss)

- **Source:** `spec.entry_cutoff.market_close_time` (fixed "16:00") + `SESSION_CLOSE_FALLBACK_BUFFER_SECONDS` (300).
- **Trigger:** Used when `ResolveAndSetSessionCloseIfNeeded` never ran or failed (e.g. robot in Historical, Bars.Count==0, TradingHours missing).
- **Gap:** On early-close days, fallback still uses 16:00 CT. Flatten would fire at 15:55 when market actually closed at 13:00 — **positions already closed, or worse: no flatten before actual close.**

---

## 2. Reentry (Market Open)

### Current Implementation

- **Source:** `spec.entry_cutoff.market_reopen_time` (default "17:00")
- **Logic:** `MarketReopenChicagoTime = tradingDate at market_reopen_time`, same calendar day as market close.
- **Holiday / variability:** None. Fixed 17:00 regardless of:
  - Early close (e.g. 13:00 close) → reopen is typically 17:00 same day; 17:00 may be correct, but not derived from TradingHours.
  - Delayed open (e.g. 09:30 open) → we'd allow reentry at 17:00 when market opened at 09:30; **wrong**.
  - Full holiday (no session) → we'd still gate on 17:00; slot should not reenter at all.

---

## 3. CME Schedule Variability (Examples)

| Scenario | Close | Reopen | Current Forced Flatten | Current Reentry |
|----------|-------|--------|------------------------|-----------------|
| Normal day | 16:00 | 17:00 | ✅ 15:55 (resolver) or 15:55 (fallback) | ✅ 17:00 |
| Day after Thanksgiving | 13:00 | 17:00 | ✅ 12:55 (resolver) / ❌ 15:55 (fallback) | ✅ 17:00 (lucky) |
| Christmas Eve | 12:00 | 17:00 | ✅ 11:55 (resolver) / ❌ 15:55 (fallback) | ✅ 17:00 (lucky) |
| Delayed open (e.g. 09:30) | 16:00 | 09:30 next day | N/A | ❌ 17:00 same day (wrong) |
| Full holiday | No session | No session | ✅ No flatten (resolver) | ❌ Would still gate on 17:00 |

---

## 4. Data Sources for Dynamic Times

| Source | Forced Flatten | Reentry | Notes |
|--------|----------------|---------|-------|
| **SessionCloseResolver** | ✅ Used (primary) | ❌ Not used | Has `ResolvedSessionCloseUtc`; does not expose next-session start |
| **NinjaTrader SessionIterator** | Via SessionCloseResolver | ❌ Not used | Can iterate to next segment → next session begin |
| **Spec fixed times** | Fallback only | ✅ Used | `market_close_time`, `market_reopen_time` |
| **Timetable** | Trading date only | ❌ | No close/reopen times |

---

## 5. Recommendations

### Short Term (Documentation / Monitoring)

1. **Log when fallback is used** — Already done (`SESSION_CLOSE_FALLBACK_USED`). Add alert if fallback used on known early-close dates (e.g. CME calendar).
2. **Document CME early-close calendar** — So operators know when resolver must succeed; fallback is unsafe those days.

### Medium Term (Reentry)

1. **Derive reentry time from SessionCloseResolver / SessionIterator** — Store `NextSessionBeginUtc` (or equivalent) when resolving session close. Use that for reentry gate instead of fixed 17:00.
2. **Holiday propagation** — If `SessionCloseResult.HasSession=false`, slot should not attempt reentry (treat as no-trade day). Currently we might still gate on 17:00 and try to reenter when there is no session.

### Longer Term (Fallback Hardening)

1. **Early-close calendar in spec or config** — Fallback could use date-specific close times when resolver fails.
2. **SessionCloseResolver for harness** — HistoricalReplay could call a resolver stub that returns correct early-close times for test dates.

---

## 6. Implementation (Completed 2026-03-12)

**Implemented:** `SessionCloseResult.NextSessionBeginUtc`; `SessionCloseResolver` populates it; `RobotEngine.GetReentryAllowedUtc`; `CheckMarketOpenReentry` uses it.

- **SessionCloseResolver:** When iterating segments, captures first segment of next trading day as `NextSessionBeginUtc`. For multi-segment days, uses `sorted[indexOfLargestGap+1].BeginUtc`.
- **RobotEngine.GetReentryAllowedUtc(tradingDay, sessionClass, utcNow):** Returns `(reentryUtc, hasSession)`. Uses cached `NextSessionBeginUtc` when available; else computes from `spec.market_reopen_time`. Returns `(null, false)` when `HasSession=false`.
- **StreamStateMachine.CheckMarketOpenReentry:** Calls `_engine.GetReentryAllowedUtc`; if `!hasSession` returns (no reentry); gates on `utcNow >= reentryUtc` when available; falls back to `MarketReopenChicagoTime` when engine is null or reentryUtc is null.
- **Fallback chain:** `NextSessionBeginUtc` (from resolver) → `spec.market_reopen_time` (fixed 17:00) → no reentry if `HasSession=false`.

**Tests:** `--test REENTRY_TIMING` covers holiday (HasSession=false), early close (NextSessionBeginUtc from resolver), delayed open, and spec fallback.

---

## 7. References

- `SessionCloseResolver.cs` — Resolve logic, segment iteration
- `RobotEngine.cs` — GetSessionCloseResultOrFallback, ComputeFallbackFromTime
- `StreamStateMachine.cs` — CheckMarketOpenReentry, MarketReopenChicagoTime
- `docs/robot/FORCED_FLATTEN_AND_REENTRY_SUMMARY.md` — Known limitations
- CME Group holiday calendar: https://www.cmegroup.com/tools-information/holiday-calendar.html
