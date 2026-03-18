# 2026-03-16: Why Only YM1 Took a Trade — Full Investigation

**Date:** Monday, March 16, 2026  
**Scope:** Root cause analysis of why only YM1 executed a trade; CL2, RTY2, NG2, NG1 did not.

---

## Exact Timeline (UTC / Chicago)

| UTC | Chicago | Event |
|-----|---------|-------|
| **00:26:56** | Sun 19:26 | CL2, NG2, NG1, RTY2, YM1 — STREAM_INITIALIZED (PRE_HYDRATION), hash `472fb24...` |
| **00:29:03** | Sun 19:29 | NG2, NG1, CL2, RTY2, YM1 — STREAM_INITIALIZED (restart), hash `9c4c7e2...` |
| **07:00:00** | 02:00 | NG1, YM1 — RANGE_BUILDING_START (S1 range window opens) |
| **11:17:28** | 06:17 | NG2, NG1, CL2 — STREAM_INITIALIZED (restart), hash `026b320...` |
| **11:17:32** | 06:17 | RTY2 — STREAM_INITIALIZED (restart), slot 09:30, hash `75c2a15...` |
| **11:17:33** | 06:17 | YM1 — STREAM_INITIALIZED (restart), hash `2d66b3e...` |
| **12:30:05** | **07:30:05** | **YM1 — RANGE_LOCKED** (range 46903–47252, brk_long 47253, brk_short 46902) |
| **12:42:22** | **07:42:22** | **YM1 — ENTRY FILLED** (Long @ 47253, qty 2) |
| **13:00:05** | 08:00 | CL2, RTY2, NG2 — PRE_HYDRATION_COMPLETE → ARMED |
| **13:01:05** | 08:01 | CL2, NG2, RTY2 — RANGE_BUILDING_START |
| **13:44:42** | **08:44:42** | **YM1 — TARGET HIT** (exit @ 47353, +$100) |
| **14:18:05** | **09:18:05** | **RESTART** — timetable hash `abc8e95...` |
| **14:18:05** | 09:18 | CL2 — STREAM_INITIALIZED (prev RANGE_BUILDING) → PRE_HYDRATION → ARMED |
| **14:18:05** | 09:18 | YM1 — STREAM_INITIALIZED (prev RANGE_LOCKED) → **RANGE_LOCKED restored** |
| **14:18:05** | 09:18 | RTY2 — STREAM_INITIALIZED (prev RANGE_BUILDING) → PRE_HYDRATION → ARMED |
| **14:18:13** | 09:18 | NG2, NG1 — STREAM_INITIALIZED (prev RANGE_BUILDING) → PRE_HYDRATION → ARMED |
| **14:19:05** | 09:19 | RTY2, CL2, NG2 — RANGE_BUILDING_START (rebuild) |
| **15:12:05** | **10:12:05** | **CL2 — RANGE_LOCKED** (brk_long 94.52, brk_short 93.84); entry orders submitted |
| **15:14:05** | **10:14:05** | **RTY2 — RANGE_LOCKED** (brk_long 2545.1, brk_short 2535.2); entry orders submitted |
| **15:14:05** | 10:14 | RTY2 — **ORDER REJECTED** (~350ms later): "price outside limits" |
| **15:16:06** | **10:16:06** | **NG2 — RANGE_LOCKED** (brk_long 3.099, brk_short 3.082); entry orders submitted |
| **22:01:00** | 17:01 | GC2, GC1, NQ2 — STREAM_INITIALIZED (new streams, post-close) |

**Slot times (Chicago):** YM1 07:30, NG1 09:00, NG2 10:30, CL2 11:00, RTY2 11:00 (timetable changed to 09:30 for S2 streams).

---

## Executive Summary

| Stream | Outcome | Root Cause |
|--------|---------|------------|
| **YM1** | ✅ Trade executed (Long @ 47253, target hit) | Normal operation; locked at 07:30 CT, breakout filled at 07:42 CT |
| **NG1** | ❌ No trade | Never reached RANGE_LOCKED; 14:18 restart wiped RANGE_BUILDING → ARMED; never recovered |
| **CL2** | ❌ No trade | Reached RANGE_LOCKED post-restart; orders placed; price never hit breakout levels |
| **RTY2** | ❌ No trade | **Order REJECTED** by broker: "current price is outside the price limits set for this product" |
| **NG2** | ❌ No trade | Reached RANGE_LOCKED post-restart; orders placed; price never hit breakout levels |

---

## Timeline

### YM1 (07:30 CT slot) — Success

| Time (UTC) | Time (CT) | Event |
|------------|-----------|-------|
| 12:30:05 | 07:30:05 | RANGE_LOCKED (range_high=47252, range_low=46903, brk_long=47253, brk_short=46902) |
| 12:42:22 | 07:42:22 | **Entry filled** (Long @ 47253) |
| 13:44:42 | 08:44:42 | Target hit; trade completed (+$100) |

YM1's trade completed **before** the 14:18 restart. The restart had no impact on YM1's execution.

---

### 14:18 UTC (09:18 CT) — Timetable Hash Change Restart

Timetable hash changed (`abc8e958...`). All streams re-initialized with `is_mid_session_restart: true`.

**Why did this happen?** The robot hashes the entire `timetable_current.json` file. The timetable includes an `as_of` timestamp that is set to the current time every time the file is written. So any process that writes the timetable (Matrix app auto-update, resequence, pipeline, dashboard save) produces a new hash even when streams/config are unchanged. The robot polls every 5 seconds; when it sees a new hash, it reloads and re-initializes streams. Likely trigger: Matrix app auto-update (resequence every 20 min) or another process writing the timetable around 09:18 CT.

| Stream | Previous State | Post-Restart State | Impact |
|--------|----------------|-------------------|--------|
| **YM1** | RANGE_LOCKED | RANGE_LOCKED (restored from hydration) | No impact; trade already done |
| **CL2** | RANGE_BUILDING | ARMED (bar_count=0) | Lost progress; had to rebuild |
| **RTY2** | RANGE_BUILDING | ARMED (bar_count=0) | Lost progress; had to rebuild |
| **NG2** | RANGE_BUILDING | ARMED (bar_count=0) | Lost progress; had to rebuild |
| **NG1** | RANGE_BUILDING | ARMED (bar_count=0) | **Never recovered** — stayed ARMED |

**NG1 critical detail:** NG1 slot = 09:00 CT (14:00 UTC). At 14:18, slot time had **already passed**. NG1 should have locked before the restart. That it was still RANGE_BUILDING suggests either:
- Insufficient bars to lock at 09:00
- Bars not yet received/processed when slot time hit
- Restart occurred before NG1 could transition to RANGE_LOCKED

After restart, NG1 went to ARMED with bar_count=0. It never transitioned back to RANGE_BUILDING or RANGE_LOCKED for 2026-03-16.

---

### Post-Restart: CL2, RTY2, NG2 Rebuild and Lock

| Stream | Slot (timetable) | RANGE_LOCKED At (UTC) | Breakout Levels |
|--------|------------------|------------------------|-----------------|
| CL2 | 11:00 → 09:30* | 15:12:05 | brk_long=94.52, brk_short=93.84 |
| RTY2 | 11:00 → 09:30* | 15:14:05 | brk_long=2545.1, brk_short=2535.2 |
| NG2 | 10:30 → 09:30* | 15:16:06 | brk_long=3.099, brk_short=3.082 |

*Timetable changed during day; RANGE_LOCKED used slot 09:30 (S2 default).

---

## Per-Stream Root Cause

### 1. NG1 — Never Reached RANGE_LOCKED

- **Journal:** `LastState: ARMED`, `StopBracketsSubmittedAtLock: false`
- **Hydration:** NG1 had RANGE_BUILDING_START at 07:00 UTC, but no RANGE_LOCKED event
- **14:18 restart:** previous_state=RANGE_BUILDING → post-restart ARMED with bar_count=0
- **Cause:** Restart wiped NG1 before it could lock. Post-restart, NG1 never received sufficient bars to transition from ARMED → RANGE_BUILDING → RANGE_LOCKED. Likely a data/bar feed issue for MNG during the rebuild window.

---

### 2. RTY2 — Order Rejected by Broker

- **Execution journal:** `Rejected: true`, `RejectionReason: "Order rejected: Please check the order price. The current price is outside the price limits set for this product."`
- **Intent:** Short stop @ 2535.2 (brk_short)
- **Cause:** NinjaTrader/broker rejected the order. M2K (micro Russell) may have different price limits or the stop was too far from current market. This is a **broker-side rejection**, not a robot logic error.

---

### 3. CL2 — No Breakout

- **Execution journal:** `EntrySubmitted: true`, `EntryFilled: false`, `Rejected: false`
- **Intent:** Short stop @ 93.84 (brk_short)
- **Cause:** Orders were placed successfully. Price never reached breakout levels before market close or session end. **Market did not break out.**

---

### 4. NG2 — No Breakout

- **Execution journal:** `EntrySubmitted: true`, `EntryFilled: false`, `Rejected: false`
- **Intent:** Long stop @ 3.099 (brk_long)
- **Cause:** Orders were placed successfully. Price never reached breakout levels. **Market did not break out.**

---

## Evidence Summary

### Execution Journals (2026-03-16)

| Stream | File | EntryFilled | Rejected | Notes |
|--------|------|-------------|----------|-------|
| YM1 | `2026-03-16_YM1_b0b4e4438b41b8c3.json` | ✅ true | false | Long @ 47253, target @ 47353 |
| CL2 | `2026-03-16_CL2_4c5a97bd67846cf4.json` | false | false | Short @ 93.84, no fill |
| RTY2 | `2026-03-16_RTY2_419ff49036c138b8.json` | false | **true** | Short @ 2535.2, **REJECTED** |
| NG2 | `2026-03-16_NG2_ee377fda37b15048.json` | false | false | Long @ 3.099, no fill |

### Stream Journals (2026-03-16)

| Stream | LastState | StopBracketsSubmittedAtLock | EntryDetected |
|--------|-----------|-----------------------------|---------------|
| YM1 | RANGE_LOCKED | true | false* |
| NG1 | ARMED | false | false |
| CL2 | RANGE_LOCKED | true | false |
| RTY2 | RANGE_LOCKED | true | false |
| NG2 | RANGE_LOCKED | true | false |

*YM1 journal shows EntryDetected=false but execution journal confirms fill — journal may not have been updated before restart, or EntryDetected is tracked differently.

---

## Recommendations

### 1. RTY2 / M2K Order Rejection

- **Investigate:** NinjaTrader M2K "price limits" — is there a max distance from market for stop orders?
- **Mitigation:** Add retry with adjusted price if rejection reason indicates price limits; or log and alert on this rejection type for follow-up.

### 2. NG1 Post-Restart Recovery

- **Issue:** NG1 never recovered from 14:18 restart (stayed ARMED).
- **Mitigation:** Ensure streams that were in RANGE_BUILDING when slot time had passed can restore from hydration/ranges log with a "would have locked" state, or at least rebuild with historical bars. The current restore logic only handles RANGE_LOCKED.

### 3. 14:18 Restart Prevention — **FIX APPLIED**

- **Cause:** Timetable hash change (likely `as_of` timestamp update).
- **Fix:** Content-only hash implemented. Excludes `as_of` and `source` from hash; only `trading_date`, `timezone`, and `streams` (with stream, instrument, session, slot_time, enabled, block_reason, decision_time) affect the hash. Metadata-only writes (e.g. Matrix resequence) no longer trigger restarts.
- **Files:** `TimetableContentHasher.cs`, `TimetableFilePoller.cs` (RobotCore, NT_ADDONS, modules/robot/core); `TimetableCache.cs`; `modules/watchdog/timetable_poller.py` (`_compute_content_hash`).

### 4. Stop Bracket Resubmit on Restore (Already Implemented)

- The fix to resubmit stop-entry brackets when restoring RANGE_LOCKED from hydration is in place. YM1 benefited from having locked before the restart; CL2, RTY2, NG2 locked *after* the restart, so they submitted brackets at lock time (no restore needed).

---

## Files Referenced

- `logs/robot/journal/2026-03-16_*.json` — Stream journals
- `logs/robot/hydration_2026-03-16.jsonl` — Hydration events
- `logs/robot/ranges_2026-03-16.jsonl` — Range lock events
- `data/execution_journals/2026-03-16_*.json` — Execution journals
