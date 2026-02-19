# Break-Even Detection — Full Assessment (2026-02-18)

**Date:** 2026-02-18  
**Scope:** All fills with BE monitoring today; root cause analysis and fixes

---

## Executive Summary

| Intent | Stream | Execution | Entry Fill | BE Expected? | BE Triggered? | Outcome |
|--------|--------|-----------|------------|--------------|---------------|---------|
| 39bfeecfa090610c | YM1 | MYM | 15:01:33 | Yes | Logged 15:16:28 | **Did not work** — logs show BE_TRIGGER_REACHED but user reports stop was not moved |
| 759dbb3cb6a36e14 | GC2 | MGC | 16:48:02 | Maybe | No | Target hit 17:34 (BE not needed) |
| 3b9984b811335222 | NQ2 | MNQ | 17:06:58 | Yes | **No** | Execution instrument mismatch (fixed) |
| 2098784027b5cd64 | CL2 | MCL | 17:11:42 | Yes | **No** | Execution instrument mismatch (fixed) |

**Fixes applied:** Execution instrument mismatch (NQ chart / MNQ trade), RobotEventTypes registry.

---

## 1. YM1 (MYM) — ✗ BE Did Not Work (per user report)

| Field | Value |
|-------|-------|
| Intent ID | 39bfeecfa090610c |
| Entry fill | 15:01:33 UTC |
| Direction | Long |
| Breakout level | 49875 |
| BE trigger | 49940 |
| BE stop | 49874 |

**Timeline:**
- 15:00:11 — STREAM_FROZEN_NO_STAND_DOWN (IEA_ENQUEUE_AND_WAIT_TIMEOUT for YM1/MYM)
- 15:01:33 — INTENT_EXPOSURE_REGISTERED (entry filled)
- 15:02:34 — BE_PATH_ACTIVE, active_intent_count=1, tick=49878
- 15:03–15:15 — BE_PATH_ACTIVE, tick 49837→49916 (approaching trigger)
- **15:16:28** — **BE_TRIGGER_REACHED** logged (ModifyStopToBreakEven returned Success)
- 15:16:38 — BE_PATH_ACTIVE, active_intent_count=0 (intent marked BE-modified in journal)
- 18:00:06 — IEA_ENQUEUE_REJECTED_INSTRUMENT_BLOCKED (MYM instrument blocked)

**Outcome:** Logs show BE_TRIGGER_REACHED and modify returned Success, but **user reports the stop was not actually moved to break-even**. Root cause unknown — requires investigation of ModifyStopToBreakEven path (order lookup, broker modify, aggregation).

---

## 2. GC2 (MGC) — Target Hit First

| Field | Value |
|-------|-------|
| Intent ID | 759dbb3cb6a36e14 |
| Entry fill | 16:48:02 UTC |
| Direction | Long |
| BE_PATH_ACTIVE | 16:48–17:06, tick 5030→5019 (declining) |

**Timeline:**
- 16:48:02 — INTENT_EXPOSURE_REGISTERED
- 16:48–17:06 — BE_PATH_ACTIVE, active_intent_count=1
- **17:34:34** — INTENT_EXIT_FILL (target hit)

**Outcome:** Target was hit before BE. Price moved down from 5030; for a Long, BE trigger is above entry. Either price never reached BE trigger, or target was hit first. No fix needed — bracket closed at target.

---

## 3. NQ2 (MNQ) — ✗ BE Did Not Trigger (Root Cause Fixed)

| Field | Value |
|-------|-------|
| Intent ID | 3b9984b811335222 |
| Entry fill | 17:06:58 UTC |
| Direction | Long |
| BE_PATH_ACTIVE | 17:06–17:08, tick 25119→25121→25109, active_intent_count=1 |

**Root cause:** When using an **NQ chart** that trades **MNQ** via execution policy:
- `GetExecutionInstrumentForBE()` returned chart instrument `"NQ"`
- Intents have `ExecutionInstrument = "MNQ"`
- `GetActiveIntentsForBEMonitoring("NQ")` filtered out the MNQ intent → 0 intents
- BE never evaluated even when price crossed trigger

**Fix applied:** Use `_engine.GetExecutionInstrument()` instead of chart-derived value. Engine returns `"MNQ"` when NQ chart trades MNQ, so the intent filter now matches.

**Note:** Logs show MNQ strategy with active_intent_count=1, so an MNQ chart was present. If the user had **only** an NQ chart, that chart would have passed `"NQ"` and gotten 0 intents. The fix ensures NQ chart also uses `"MNQ"` and finds the intent.

---

## 4. CL2 (MCL) — ✗ BE Did Not Trigger (Root Cause Fixed)

| Field | Value |
|-------|-------|
| Intent ID | 2098784027b5cd64 |
| Entry fill | 17:11:42 UTC |
| Direction | Long |

**Root cause:** Same as NQ2/MNQ. When using a **CL chart** that trades **MCL** via execution policy:
- `GetExecutionInstrument()` returned chart instrument `"CL"` (engine was constructed with `instrument: "CL"`)
- Intents have `ExecutionInstrument = "MCL"` (from streams)
- `GetActiveIntentsForBEMonitoring("CL")` filtered out the MCL intent → 0 intents
- BE never evaluated even when price crossed trigger

**Fix applied:** `RobotEngine.GetExecutionInstrument()` now resolves via execution policy: `GetEnabledExecutionInstrument(chartInstrument)` returns `"MCL"` for `"CL"`. BE filter now matches the intent.

---

## Fixes Applied

### 1. RobotEngine.cs — Resolve Chart to Execution Instrument

**Before:** `GetExecutionInstrument()` returned the chart instrument passed at construction (e.g. `"CL"`, `"NQ"`). Intents have `ExecutionInstrument` from streams (e.g. `"MCL"`, `"MNQ"`). BE filter mismatched → 0 intents.

**After:** `GetExecutionInstrument()` resolves via `_executionPolicy.GetEnabledExecutionInstrument(chartInstrument)`. For CL chart → returns `"MCL"`; for NQ chart → returns `"MNQ"`.

**Impact:** NQ/ES/YM/CL/GC/RTY charts that trade micros (MNQ/MES/MYM/MCL/MGC/M2K) now pass the correct execution instrument to BE monitoring. RobotSimStrategy already used `_engine.GetExecutionInstrument()`; the fix was in the engine's resolution.

### 2. RobotEventTypes.cs — BE Event Registry

Added `BE_TRIGGER_REACHED`, `BE_TRIGGER_RETRY_NEEDED`, `BE_TRIGGER_FAILED`, `BE_TRIGGER_TIMEOUT_ERROR` to registry. Eliminates UNREGISTERED_EVENT_TYPE warning.

### 3. GetExposureState() — Same Fix

`GetExposureState()` now also uses `_engine.GetExecutionInstrument()` for consistency with BE path.

### 4. BE Modify Confirmation (Tightened Patch)

**Problem:** YM1 incident showed BE_TRIGGER_REACHED logged and ModifyStopToBreakEven returned Success, but the stop was not actually moved. Root cause: NinjaTrader's `Account.Change()` returns void; code assigned its result via `dynamic`, causing `RuntimeBinderException` and a fallback path that reported success without broker confirmation.

**Fix:** BE modify confirmation patch (see `BE_MODIFY_CONFIRMATION_PATCH_PLAN.md`):
- Register pending BE request **before** calling `Change()` (race hardening)
- Confirm via OrderUpdate (or post-read at timeout), not call return value
- Emit `STOP_MODIFY_REQUESTED`, `STOP_MODIFY_CONFIRMED`, `STOP_MODIFY_TIMEOUT`, `STOP_MODIFY_FAILED`, `STOP_MODIFY_REJECTED`
- Timeout with retry and post-read; terminal `STOP_MODIFY_FAILED` after max retries
- Journal gating: `RecordBEModification` only on `STOP_MODIFY_CONFIRMED`

---

## BE_GATE_BLOCKED (Expected)

When position is in one instrument (e.g. MYM), strategies on other charts (M2K, MES, MNQ, MGC, MCL, MNG) correctly emit `BE_GATE_BLOCKED (INSTRUMENT_MISMATCH)`. This prevents comparing wrong tick prices (e.g. M2K price vs MYM position). **Correct behavior.**

---

## Recommendations

1. **Verify fix in production** — Run with NQ chart only on next MNQ fill; confirm BE triggers.
2. **Reduce BE_GATE_BLOCKED noise** — Consider raising rate limit for INSTRUMENT_MISMATCH (e.g. 5 min).
3. **Add BE trigger logging** — When price crosses trigger but modify fails, log `BE_TRIGGER_FAILED` with reason.

---

## Root Cause: Why Was the Instrument Blocked?

**Log evidence (robot_ENGINE.jsonl, robot_MYM.jsonl):**

| Time (UTC) | Event | Detail |
|------------|-------|--------|
| 15:00:05.677 | ENTRY_SUBMIT_PRECHECK | YM1 strategy submits stop brackets (long + short) via EnqueueAndWait |
| 15:00:05.677 | Work enqueued | enqueue_sequence=1, last_processed_sequence=0 (worker had never completed any work) |
| 15:00:11.085 | **IEA_ENQUEUE_AND_WAIT_TIMEOUT** | 5s timeout; instrument blocked |
| 15:00:11.741 | ORDER_SUBMIT_SUCCESS | Long entry order actually submitted (~656ms *after* timeout) |
| 15:00:12.790 | ORDER_ACKNOWLEDGED | Broker acknowledged long order |

**Conclusion:** The IEA worker **did** pick up the stop-brackets work and ran it. The worker was blocked inside **NinjaTrader's `CreateOrder` or `Submit`** for ~6 seconds. The first (long) order took from 15:00:05 to 15:00:11.741 — longer than the 5s `EnqueueAndWait` timeout. The caller timed out and blocked the instrument before the worker finished. The worker eventually completed the long submission, but by then the instrument was already blocked.

**Root cause:** NinjaTrader's order submission API blocked the IEA worker thread for >5 seconds. Possible contributors: NT under load (many strategies/charts), Sim connection latency, or internal NT locks.

---

## Prevention: How to Avoid This Happening Again

### Root Cause Summary

The YM1 incident had two failure modes:

1. **IEA worker blocked by slow NT API** — `EnqueueAndWait` timed out at 15:00:11 because NinjaTrader's `CreateOrder`/`Submit` blocked the worker for ~6s. The instrument was marked blocked; all new work (including protective submission) was rejected.
2. **Fill after block** — Entry filled at 15:01:33. The execution update was `Enqueue`'d (fire-and-forget) but the worker was blocked, so `HandleEntryFill` never ran. Result: position with no stop or limit.
3. **BE logged but no stop to modify** — BE path ran (price crossed trigger) and logged Success, but there were no working protectives to modify.

### Prevention Measures (in priority order)

#### 1. **Flatten on block (CRITICAL)**

When `blockInstrumentCallback` fires (IEA timeout/overflow), immediately flatten any open positions for that instrument. Today we only stand down streams; we do not flatten. Once blocked, `EnqueueAndWait` rejects all work, so normal flatten (which goes through the queue) will fail.

**Implementation:** Add `FlattenEmergency(instrument, utcNow)` to the adapter that bypasses the IEA queue and calls `Account.Flatten()` directly. In the block callback, before or after standing down, call `FlattenEmergency` for the blocked instrument. This ensures we never leave a position unprotected when the instrument is blocked.

#### 2. **Unprotected position check in reconciliation**

Extend `ReconciliationRunner` to detect: account has position for instrument X, journal has `EntryFilled` for X, but there are no QTSW2 protective working orders for X. That indicates an unprotected position.

**Implementation:** In `RunInternal`, after the qty mismatch check, add a loop: for each instrument with `accountQty > 0`, check if we have QTSW2-tagged working orders for that instrument. If we have position + open journal with `EntryFilled` but no protective working orders → log `RECONCILIATION_UNPROTECTED_POSITION` and invoke `onUnprotectedPosition(instrument, intentIds, utcNow)`. The engine callback flattens each intent and stands down.

#### 3. **Flatten on qty mismatch**

Today `onQuantityMismatch` only stands down; it does not flatten. If account has position but journal disagrees (e.g. fill never processed), we should flatten to resolve the mismatch.

**Implementation:** In the `onQuantityMismatch` callback, after `StandDownStreamsForInstrument`, add logic to flatten open intents for that instrument (from journal). This closes the position and aligns broker with journal.

#### 4. **Increase EnqueueAndWait timeout for entry submission (confirmed fix)**

Logs show NinjaTrader's `CreateOrder`/`Submit` blocked for ~6s. The 5s timeout is too short. Increase timeout for entry submission (e.g. 10–15s) or make it configurable. Flatten already uses 10s.

#### 5. **Investigate worker stuck root cause**

The 5s `EnqueueAndWait` timeout suggests the worker was blocked for >5s. **Confirmed:** NinjaTrader API blocked for ~6s. Likely causes: NT under load, Sim connection latency, or internal NT locks.

**Actions:**
- Add per-work-item timeout or watchdog: if the worker hasn’t completed the current item in N seconds, log CRITICAL with context.
- Profile which operations run on the worker and can block (e.g. `SubmitStopEntryOrder`, `SubmitProtectiveStop`, `ProcessExecutionUpdate`).
- Consider increasing timeout for flatten (already 10s) and making it configurable.
- Add `IEA_WORKER_STUCK` heartbeat: if `LastMutationUtc` is stale for >30s, log and consider recovery.

#### 6. **Protective submission watchdog (optional)**

After a fill, we expect `ProtectionSubmitted` within ~30s. If the journal has `EntryFilled` but `ProtectionSubmitted` stays false for >N seconds, treat as failure and flatten.

**Implementation:** Reconciliation or a separate timer checks: for each open journal with `EntryFilled && !ProtectionSubmitted` and fill time >30s ago → flatten and alert. This catches cases where `HandleEntryFill` never ran or protectives failed silently.

#### 7. **Monitoring and alerts**

- Alert on `IEA_ENQUEUE_AND_WAIT_TIMEOUT` and `IEA_ENQUEUE_REJECTED_INSTRUMENT_BLOCKED` (already CRITICAL/WARN).
- Alert when `ProtectionSubmitted` is false for >60s after a fill (from journal).
- Dashboard: show “unprotected position” when account has position but no protective orders for that instrument.

### Implementation Order

| Priority | Item | Effort | Impact |
|----------|------|--------|--------|
| 1 | Flatten on block | Medium | Prevents unprotected position when IEA blocks |
| 2 | Unprotected position in reconciliation | Medium | Catches any position without protectives |
| 3 | Flatten on qty mismatch | Low | Resolves broker/journal desync |
| 4 | Increase EnqueueAndWait timeout for entry | Low | Reduces timeout when NT is slow |
| 5 | Worker stuck investigation | Medium | Reduces likelihood of block |
| 6 | Protective submission watchdog | Low | Extra safety net |
| 7 | Monitoring/alerts | Low | Visibility |

---

## Related

- [2026-02-18 Break-Even Detection Investigation](2026-02-18_BREAK_EVEN_DETECTION_INVESTIGATION.md)
- [BREAK_EVEN_DETECTION_SUMMARY](../BREAK_EVEN_DETECTION_SUMMARY.md)
- [2026-02-17 NQ1 Fill No Protectives](2026-02-17_NQ1_FILL_NO_PROTECTIVES_INVESTIGATION.md)
