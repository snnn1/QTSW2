# What We Don't Have vs. "Latest Broken Version" (Pre-Rollback)

**Context:** Rollback to 65c8d13 (Feb 8) was done to fix chart lag. The "broken" version had more features but caused lag. This summarizes what's missing now and how useful each is.

---

## 1. Entry-Fill Cache for HasEntryFillForStream

**What:** O(1) lookup instead of disk I/O per call. `_entryFillByStream` dict + `WarmCacheForTradingDate` so `HasEntryFillForStream` doesn't scan journal directory on every call.

**Current state:** **ALREADY IMPLEMENTED.**  
- `_entryFillByStream` dict in ExecutionJournal; `HasEntryFillForStream` checks cache first, falls back to disk on miss.  
- `WarmCacheForTradingDate` populates it; `RecordEntryFill` updates it on live fills.  
- `WarmExecutionJournalCacheForTradingDate` called from RobotEngine on Realtime transition.

**Usefulness:** **High** — Reduces I/O when forced flatten checks post-entry (near session close).

**Lag risk:** **Low** — Pure optimization.

---

## 2. Session-Aware Forced Flatten (c3fe8f8)

**What:** Replace fixed 16:00 forced flatten with NinjaTrader `TradingHours` / `SessionIterator` to get actual session close. Handles early closes, holidays (skips forced flatten on holidays). New events: `SESSION_CLOSE_RESOLVED`, `FORCED_FLATTEN_TRIGGERED`, etc.

**Current state:** **NOT in codebase.**  
- We use spec-based `market_close_time = "16:00"` from parity config.  
- No `SessionIterator` / `TradingHours` for forced flatten.

**Usefulness:** **Medium** — Correct behavior on early closes and holidays; avoids flattening at wrong time.

**Lag risk:** **Medium** — `SessionIterator` / `TradingHours` can block if used in hot path. Original impl deferred to Realtime, but post-entry check calls `HasEntryFillForStream` (disk I/O) per stream per bar near close. Add entry-fill cache first.

---

## 3. RobotRuntimeConfig (robot.json) — e301b95

**What:** `configs/robot/robot.json` for runtime config. Sim adapter, connection status handling. Large refactor; also **removed** health/notification code.

**Current state:** **NOT in codebase.** No `robot.json`; no RobotRuntimeConfig.

**Usefulness:** **Low–Medium** — Depends on whether you need runtime config and connection status. The commit removed health/notification code — we still have that.

**Lag risk:** **Unknown** — Large refactor; would need incremental review.

---

## 4. enable_diagnostic_logs = false (Production)

**What:** Disable diagnostic logging in production to reduce log volume and I/O.

**Current state:** **DONE.** `configs/robot/logging.json` now has `enable_diagnostic_logs: false`.

**Usefulness:** **Low** — Reduces log noise and some I/O in production.

**Lag risk:** **Low** — Config change only.

---

## 5. LogBarProfileIfSlow Gated on Config

**What:** Only log `BAR_PROFILE_SLOW` when `diagnostic_slow_logs` (or similar) is true.

**Current state:** **Already in place.** Gated on `IsDiagnosticSlowLogsEnabled(_engine)` which reads config.

**Usefulness:** N/A — Already done.

---

## 6. Timetable Poll 5s

**What:** Slow timetable polling from 2s to 5s.

**Current state:** **Already 5s** in RobotSimStrategy.

**Usefulness:** N/A — Already done.

---

## Summary Table

| Item | In Codebase? | Usefulness | Lag Risk | Recommendation |
|------|--------------|------------|----------|----------------|
| Entry-fill cache | **Yes** | High | Low | Done |
| Session-aware forced flatten | No | **Medium** | Medium | Add next; entry-fill cache already in place |
| RobotRuntimeConfig | No | Low–Medium | Unknown | Defer; review incrementally |
| enable_diagnostic_logs=false | Config only | Low | Low | Set for production — done |
| LogBarProfileIfSlow gating | Yes | — | — | Done |
| Timetable poll 5s | Yes | — | — | Done |

---

## Suggested Order to Add Back

1. ~~**Entry-fill cache**~~ — Done.
2. ~~**Config:** `enable_diagnostic_logs: false`~~ — Done.
3. **Session-aware forced flatten** (c3fe8f8) — Add next; validate on single chart first.
4. **e301b95 (RobotRuntimeConfig)** — Only if needed; review file-by-file.
