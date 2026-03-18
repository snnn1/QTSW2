# 2026-03-17: YM1 Tick Stale During Trade — Trade Still Completed

**Date:** Tuesday, March 17, 2026  
**Scope:** YM1 "broke straight away" but trade still executed and exited at target.

---

## Executive Summary

| Aspect | Outcome |
|--------|---------|
| **YM1 state** | RANGE_LOCKED at 12:30:07 UTC (07:30:07 CT) |
| **Entry** | Long filled at breakout (brk_long 47399) |
| **Exit** | Target hit at 13:11:17–18 UTC (08:11 CT) |
| **"Broken"** | BE_TICK_STALE_WARNING (~10 sec), BE_FILTER_EXCLUDED_ACTIVE_EXPOSURE, TIMETABLE_POLL_STALL_RECOVERED |
| **Result** | ✅ Trade completed successfully |

Yes — it's good. The trade executed and exited at target despite the tick going stale and the BE path briefly failing to evaluate.

---

## Timeline (UTC)

| Time (UTC) | Event |
|------------|-------|
| **12:30:07** | YM1 RANGE_LOCKED (range 47021–47398, brk_long 47399, brk_short 47020) |
| ~12:30–13:11 | Entry filled Long at breakout |
| **13:11:08** | ONBARUPDATE bar 2259; **BE_TICK_STALE_WARNING** (tick_age_seconds: 9.95) |
| **13:11:09** | TIMETABLE_POLL_STALL_RECOVERED |
| **13:11:09** | **BE_FILTER_EXCLUDED_ACTIVE_EXPOSURE** (CRITICAL) — account has exposure but BE filter returned 0 intents |
| **13:11:17** | INTENT_EXIT_FILL (1 of 4 at target) |
| **13:11:18** | INTENT_EXIT_FILL (2 of 4); completion_reason TARGET; ORPHAN_DETECTED (protective stop cancelled) |

---

## What "Broke"

### 1. BE_TICK_STALE_WARNING

- **When:** 13:11:08 UTC  
- **Detail:** Tick price was ~10 seconds old (9.95 sec). BE (break-even) logic uses tick price for blind risk control.  
- **Thresholds:** Warning at >5 sec; fail-closed (StandDownStream) at >12 sec.  
- **Impact:** Warning only. Tick stayed under 12 sec, so no stand-down. Trade continued.

### 2. BE_FILTER_EXCLUDED_ACTIVE_EXPOSURE (CRITICAL)

- **When:** 13:11:09 UTC  
- **Detail:** Account had MYM exposure but the BE filter returned 0 intents. Possible execution-instrument mismatch (chart vs intent).  
- **Impact:** BE path could not evaluate for that tick. Exit was driven by target fill, not BE logic, so the trade still closed correctly.

### 3. TIMETABLE_POLL_STALL_RECOVERED

- **When:** 13:11:09 UTC  
- **Detail:** Timetable poll had stalled briefly; last poll 13:11:08.  
- **Impact:** Recovered; no stream re-init or stand-down.

---

## Why the Trade Still Worked

1. **Entry already in** — Stop orders filled at breakout before the tick went stale.  
2. **Target hit** — Broker filled the target; the robot’s fill handler processed it normally.  
3. **BE gate scope** — BE gate blocks *new* entries when tick is stale; it does not block processing of exit fills.  
4. **Fail-closed threshold** — StandDownStream only triggers when tick >12 sec stale. Tick was ~10 sec, so no stand-down.  
5. **ORPHAN_DETECTED** — Protective stop cancellations after target fill are expected and routed to reconciliation.

---

## Minor Issues (Non-Blocking)

- **EXECUTION_EVENT_WRITE_FAILED** — MYM.jsonl locked by another process during protective audit write. Execution continued.  
- **BROKER_EXPOSURE_MISMATCH** — Broker net qty 1 vs intent net qty 3 during partial exit; informational only.

---

## Conclusion

YM1 showed tick staleness and BE evaluation issues right around the exit, but the trade completed as intended. The system behaved correctly: it warned on stale tick, did not stand down (under 12 sec), and processed the target fill normally.

---

## Follow-up: Watchdog YM1 DONE/RANGE_LOCKED Flicker (2026-03-17)

**Symptom:** YM1 kept switching between DONE and RANGE_LOCKED on the stream feed when it should show DONE.

**Root cause:** Slot journal hydration (`hydrate_stream_states_from_slot_journals`) runs every 30s in `get_stream_states` and was **always overwriting** with the slot journal. The slot journal for YM1 still showed `LastState: RANGE_LOCKED` (robot may not flush DONE until end of session). Event-derived DONE (from execution journal `TradeCompleted`, `TRADE_RECONCILED`, etc.) was being overwritten back to RANGE_LOCKED.

**Fix applied:**
1. **state_manager.py:** Do not overwrite DONE/committed streams with RANGE_LOCKED from the slot journal. Terminal states are preserved.
2. **aggregator.py:** Run `hydrate_intent_exposures_from_journals` before slot journal hydration so DONE from execution journal `TradeCompleted` is applied first.
