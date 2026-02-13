# Post-Rollback: What Was Added Since Feb 8 That We Don't Have Now

**Rollback commit:** 65c8d13 (Feb 8)  
**Current state:** At rollback (charts stable, lag resolved)  
**Goal:** Identify what could be added back without re-introducing chart lag

---

## 1. Commits After Feb 8 (Not in Current Codebase)

### Commit c3fe8f8 — Session-Aware Forced Flatten (Feb 9)

**What it does:**
- Replaces **spec-based 16:00** forced flatten with **session-aware timing** (TradingHours/SessionIterator)
- Uses NinjaTrader `TradingHours` / `SessionIterator` to resolve actual session close times
- Handles **early closes and holidays** (skips forced flatten on holidays)
- Adds **post-entry guard** with safe fallback (never throws from Tick())
- New event types: `SESSION_CLOSE_RESOLVED`, `SESSION_CLOSE_FALLBACK`, `SESSION_CLOSE_HOLIDAY`, `FORCED_FLATTEN_TRIGGERED`, `FORCED_FLATTEN_POST_ENTRY_CHECK_FAILED`, etc.

**Files changed:**
- RobotEngine.cs (+283 lines)
- StreamStateMachine.cs (+88 lines)
- NinjaTraderSimAdapter.cs
- RobotSimStrategy.cs (+234 lines)
- RobotEventTypes.cs
- DateOnlyCompat.cs, TimeOnlyCompat.cs

**Chart lag risk:** **Medium**
- Uses `SessionIterator` / `TradingHours` — can block if not careful; c3fe8f8 defers resolution to Realtime
- Post-entry check calls `HasEntryFillForStream()` — **disk I/O per stream per bar** when near session close (no cache in current code)
- Recommendation: Add **entry-fill cache** first if bringing this back

---

### Commit e301b95 — Robot Runtime Config, Connection Status (Feb 11)

**What it does:**
- `RobotRuntimeConfig` (configs/robot/robot.json)
- Sim adapter, connection status handling
- **Removes** health/notification code
- StreamStateMachine, RobotEngine, bar provider refactors
- Configs and docs
- Ignore logs, data, build artifacts

**Chart lag risk:** **Unknown** — large refactor; would need incremental review

---

## 2. Lag Mitigations (From Conversation Summary — May Have Been Uncommitted)

These were discussed before rollback as ways to reduce lag. Some may already be in the Feb 8 codebase.

### A. Entry-Fill Cache (ExecutionJournal)

**What:** `_entryFillByStream` dict + `WarmEntryFillCacheForTradingDate()` so `HasEntryFillForStream` is **O(1)** instead of disk I/O per bar.

**Current state:** **NOT in codebase** — `HasEntryFillForStream` scans journal directory and reads JSON files on every call.

**Chart lag risk:** **Low** — reduces I/O; pure win  
**Recommendation:** **Safe to add back** — improves performance

---

### B. Timetable Poll Interval (2s → 5s)

**What:** Slow timetable file polling from every 2 seconds to every 5 seconds.

**Current state:** **Already 5s** — `RobotSimStrategy` line 364: `TimeSpan.FromSeconds(5)`

**Chart lag risk:** N/A  
**Recommendation:** Already applied

---

### C. LogBarProfileIfSlow Gated on Config

**What:** Only log `BAR_PROFILE_SLOW` when `diagnostic_slow_logs` (or similar) is true in robot.json.

**Current state:** **Partially in place** — gated on `IsDiagnosticSlowLogsEnabled(_engine)` which reads config.

**Chart lag risk:** Low  
**Recommendation:** Ensure `enable_diagnostic_logs` or equivalent is `false` in production

---

### D. enable_diagnostic_logs = false

**What:** Disable diagnostic logging in production.

**Current state:** Check `configs/robot/logging.json` — `enable_diagnostic_logs` was set to `true` in last seen config.

**Chart lag risk:** Low  
**Recommendation:** Set to `false` for production

---

## 3. Summary: Safe vs Higher-Risk Additions

| Addition | Chart Lag Risk | Recommendation |
|----------|----------------|----------------|
| **Entry-fill cache** (ExecutionJournal) | Low | ✅ Add first — reduces I/O, enables other features |
| **Timetable poll 5s** | N/A | Already applied |
| **LogBarProfileIfSlow gating** | Low | Already present; verify config |
| **enable_diagnostic_logs = false** | Low | Config change only |
| **Session-aware forced flatten** (c3fe8f8) | Medium | Add **after** entry-fill cache; test on single chart |
| **e301b95 refactor** | Unknown | Defer; review incrementally |

---

## 4. Suggested Order to Add Back

1. **Entry-fill cache** — Low risk, high impact; unblocks session-aware forced flatten
2. **Config**: `enable_diagnostic_logs: false` in logging.json
3. **Session-aware forced flatten** (c3fe8f8) — cherry-pick or manually apply; validate on 1 chart first
4. **e301b95** — Only if needed; review file-by-file

---

## 5. What the Current (Feb 8) Codebase Has

- Spec-based forced flatten timing (market_close_time = "16:00" from analyzer_robot_parity.json)
- `HasEntryFillForStream` does disk I/O on every call (no cache)
- Timetable poll: 5 seconds
- LogBarProfileIfSlow: gated on `IsDiagnosticSlowLogsEnabled`
- No session-aware session close (no TradingHours/SessionIterator for forced flatten)
- No RobotRuntimeConfig from robot.json
- Health/notification code present (e301b95 removed it)
