# Full Summary: Issues and Fixes (2026-03-12 Session)

**Date:** Thursday, March 12, 2026  
**Scope:** All issues identified today (incidents, audits, session work) and whether the implemented fixes address them.

---

## Part 1 — Issues Identified

### A. From 2026-03-12 Daily Summary

| # | Issue | Severity | Description |
|---|-------|----------|-------------|
| A1 | **ENGINE_TICK_STALL false positives** | Medium | ~100 false stall events; low-volume instruments had 3+ min gaps → per-instrument stall detection flagged as dead |
| A2 | **Connection instability → strategy shutdown** | Critical | Connection lost 4+ times in 5 min → NinjaTrader disable; sustained disconnect ~7 min → ENGINE_STOP |
| A3 | **CONNECTION_LOST notification failed** | Medium | Pushover error at 20:40 UTC; sustained disconnect alert not delivered |

### B. From 2026-03-16 YM1-Only-Trade Investigation

| # | Issue | Severity | Description |
|---|-------|----------|-------------|
| B1 | **Timetable hash change → mid-session restart** | High | `as_of` timestamp in timetable changed → hash changed → all streams re-initialized; CL2, RTY2, NG2, NG1 lost RANGE_BUILDING progress |
| B2 | **NG1 never recovered post-restart** | High | NG1 was RANGE_BUILDING at 14:18; restart wiped to ARMED; never transitioned back (bar_count=0, insufficient bars) |
| B3 | **RTY2 order rejected by broker** | Medium | "Price outside limits" — M2K stop order rejected; broker-side, not robot logic |
| B4 | **CL2, NG2 no breakout** | Low | Orders placed; price never hit breakout levels; market behavior, not bug |

### C. From Order Cancellation Root Cause Audit

| # | Issue | Severity | Description |
|---|-------|----------|-------------|
| C1 | **modules/NT_ADDONS cancel orders on recovery** | High | RunRecovery calls `CancelRobotOwnedWorkingOrders` → cancels ALL robot orders; RobotCore does NOT (preserves) |
| C2 | **Ambiguity: who cancels?** | Medium | ORDER_CANCELLED event cannot distinguish robot vs platform; need correlation with ROBOT_ORDERS_CANCELLED |

### D. From Live Breakout Validity Audit

| # | Issue | Severity | Description |
|---|-------|----------|-------------|
| D1 | **LIVE mode blocked** | High | ExecutionAdapterFactory threw for LIVE; production could not use live adapter |
| D2 | **NinjaTraderLiveAdapter returned (null, null)** | High | GetCurrentMarketPrice stub → IsBreakoutValidForResubmit fail-open; no price validation on delayed resubmit |
| D3 | **No freshness window on initial submission** | Medium | Delayed first submissions (beyond slot time) could proceed; no block for materially delayed entries |
| D4 | **No price sanity check** | Medium | Within freshness, clearly stale entries (price far from breakout) could submit |
| D5 | **Fail-open when price unavailable (recovery)** | Medium | Recovery/restart retry when bid/ask null → allowed resubmit; should fail-closed |
| D6 | **Fixed 2-tick tolerance** | Low | ES/NG/CL have different volatility; one size doesn't fit all |
| D7 | **No measurement** | Low | No visibility into % blocked by freshness, validity gate, delay distribution |

### E. From Forced Flatten + Entry Logic Audit

| # | Issue | Severity | Description |
|---|-------|----------|-------------|
| E1 | **ExecutionInterruptedByClose not explicitly checked** | Medium | ReconcileEntryOrders, HandleRangeLockedState, restore logic relied on _entryDetected; "safe by side effect" not "safe by design" |
| E2 | **RestoreRangeLockedFromHydrationLog overwrites when ExecutionInterruptedByClose** | Low | On restart with forced-flatten state, restore could set _stopBracketsSubmittedAtLock=false; protected by _entryDetected but fragile |

### F. From Timetable Hash Audit

| # | Issue | Severity | Description |
|---|-------|----------|-------------|
| F1 | **Python vs C# hash mismatch** | Low | Python includes `metadata`; C# excludes; same file can produce different hashes |
| F2 | **Content hash excludes as_of** | Fixed | Content-only hash implemented; metadata-only writes no longer trigger restarts |

---

## Part 2 — Fixes Implemented (This Session)

### Breakout Validity / LIVE Path

| Fix | Files | Status |
|-----|-------|--------|
| **Enable LIVE adapter** | ExecutionAdapterFactory, RobotEngine | ✅ Done |
| **NinjaTraderLiveAdapter.GetCurrentMarketPrice** | NinjaTraderLiveAdapter, SetNTContext, Strategy wiring | ✅ Done (prior session) |
| **Initial submission freshness window** | StreamStateMachine.TryLockRange, BreakoutSpec.initial_submission_freshness_minutes | ✅ Done |
| **Price sanity check (within freshness)** | StreamStateMachine.TryLockRange, BreakoutSpec.initial_submission_price_sanity_ticks | ✅ Done |
| **Fail-closed when price unavailable (recovery)** | IsBreakoutValidForResubmit | ✅ Done |
| **Instrument-level tolerance** | ParityInstrument.breakout_validity_tolerance_ticks, analyzer_robot_parity.json (NG:7, CL:4) | ✅ Done |
| **Measurement** | EXECUTION_METRIC_INITIAL_SUBMISSION_ALLOWED, metric_type on block events | ✅ Done |

### ExecutionInterruptedByClose Hardening

| Fix | Files | Status |
|-----|-------|--------|
| **ReconcileEntryOrders** | StreamStateMachine (RobotCore, NT_ADDONS, modules) | ✅ Done |
| **HandleRangeLockedState** | StreamStateMachine | ✅ Done |
| **ExecutePendingRecoveryAction** (modules) | StreamStateMachine | ✅ Done |
| **Restore logic** | StreamStateMachine constructor | ✅ Done |

### Build / Deploy

| Fix | Files | Status |
|-----|-------|--------|
| **Models.ParitySpec missing properties** | RobotCore_For_NinjaTrader/Models.ParitySpec.cs | ✅ Done (initial_submission_freshness_minutes, initial_submission_price_sanity_ticks) |
| **Build and deploy** | deploy_to_ninjatrader.ps1 | ✅ Done |

### Prior Sessions (Referenced)

| Fix | Status |
|-----|--------|
| Content-only timetable hash (exclude as_of) | ✅ Done (2026-03-16 incident doc) |
| Shared engine tick state (stall false positives) | ✅ Done (2026-03-12 daily summary) |
| RobotCore recovery preserves orders | ✅ Already correct (no CancelRobotOwnedWorkingOrders) |

---

## Part 3 — Do the Fixes Address the Issues?

### A. 2026-03-12 Daily Summary

| Issue | Fix | Addressed? |
|-------|-----|------------|
| A1 ENGINE_TICK_STALL false positives | Shared engine tick state | ✅ Yes (prior session) |
| A2 Connection instability | None (infrastructure) | ❌ No — broker/network; robot cannot fix |
| A3 CONNECTION_LOST notification failed | None | ❌ No — Pushover delivery; needs separate investigation |

### B. 2026-03-16 YM1-Only-Trade

| Issue | Fix | Addressed? |
|-------|-----|------------|
| B1 Timetable hash restart | Content-only hash | ✅ Yes (prior session) |
| B2 NG1 never recovered | None | ❌ No — RANGE_BUILDING restore not implemented; restore only handles RANGE_LOCKED |
| B3 RTY2 broker rejection | None | ❌ No — broker-side; investigate M2K price limits |
| B4 CL2, NG2 no breakout | N/A | — Market behavior |

### C. Order Cancellation Audit

| Issue | Fix | Addressed? |
|-------|-----|------------|
| C1 modules/NT_ADDONS cancel on recovery | RobotCore already preserves | ✅ Yes — production uses RobotCore |
| C2 Ambiguity who cancels | None | ⚠️ Partial — audit documents correlation; no code change |

### D. Live Breakout Validity

| Issue | Fix | Addressed? |
|-------|-----|------------|
| D1 LIVE mode blocked | Enable LIVE in factory | ✅ Yes |
| D2 Live adapter (null,null) | GetCurrentMarketPrice implemented | ✅ Yes (prior session) |
| D3 No freshness window | initial_submission_freshness_minutes | ✅ Yes |
| D4 No price sanity | initial_submission_price_sanity_ticks | ✅ Yes |
| D5 Fail-open recovery | Fail-closed when price unavailable | ✅ Yes |
| D6 Fixed 2-tick tolerance | Instrument-level breakout_validity_tolerance_ticks | ✅ Yes |
| D7 No measurement | EXECUTION_METRIC_*, metric_type | ✅ Yes |

### E. Forced Flatten Audit

| Issue | Fix | Addressed? |
|-------|-----|------------|
| E1 ExecutionInterruptedByClose implicit | Explicit checks in all paths | ✅ Yes |
| E2 Restore overwrites | Skip restore when ExecutionInterruptedByClose | ✅ Yes |

### F. Timetable Hash Audit

| Issue | Fix | Addressed? |
|-------|-----|------------|
| F1 Python vs C# hash mismatch | None | ❌ No — metadata inclusion differs |
| F2 as_of triggers restart | Content-only hash | ✅ Yes |

---

## Part 4 — Summary Matrix

| Category | Issues | Fixed | Not Fixed | External |
|----------|--------|-------|-----------|----------|
| Breakout validity / LIVE | 7 | 7 | 0 | — |
| Forced flatten hardening | 2 | 2 | 0 | — |
| Timetable / restart | 2 | 1 | 1 (Python hash) | — |
| 2026-03-12 daily | 3 | 1 | 0 | 2 (connection, Pushover) |
| 2026-03-16 YM1 incident | 4 | 1 | 2 | 1 (broker rejection) |
| Order cancellation | 2 | 1 | 0 | 1 (audit only) |

---

## Part 5 — Remaining Gaps (Not Addressed)

1. **NG1 post-restart recovery** — RANGE_BUILDING restore; streams that were building when slot passed need a "would have locked" or historical-bar rebuild path.
2. **RTY2/M2K broker rejection** — Investigate NinjaTrader M2K "price limits"; possible retry with adjusted price or alert.
3. **Connection stability** — Broker/data feed; network/VPN; outside robot code.
4. **CONNECTION_LOST notification failure** — Pushover delivery; review error handling.
5. **Python vs C# timetable hash** — Align metadata handling if watchdog/robot need to agree on "changed."

---

## Part 6 — What the Fixes Prevent

| Scenario | Before | After |
|----------|--------|-------|
| Delayed initial submission (>3 min) | Could submit | Blocked (NO_TRADE_MATERIALLY_DELAYED) |
| Stale price at initial submission | Could submit | Blocked (NO_TRADE_INITIAL_SUBMISSION_PRICE_SANITY) |
| Recovery resubmit, price unavailable | Fail-open (allowed) | Fail-closed (blocked) |
| Recovery resubmit, price crossed breakout | Could resubmit (Sim adapter had price) | Blocked (Live adapter now has price; instrument tolerance) |
| Entry resubmit when ExecutionInterruptedByClose | Protected by _entryDetected | Explicit check; safe by design |
| Restore when ExecutionInterruptedByClose | Could overwrite _stopBracketsSubmittedAtLock | Skip restore |
| LIVE mode | Threw | Uses NinjaTraderLiveAdapter |
| Timetable as_of change | Restart | No restart (content hash) |
