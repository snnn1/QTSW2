# P2.10 — Unified breakout validity (implementation summary)

**Date:** 2026-03-21  
**Scope:** `StreamStateMachine.cs` only (RobotCore, `modules/robot/core`, `NT_ADDONS`). IEA, RiskGate, order submission mechanics unchanged.

## Files modified

| File | Change |
|------|--------|
| `RobotCore_For_NinjaTrader/StreamStateMachine.cs` | New helpers; TryLockRange Phase B; `HandleRangeLockedState`; `ExecutePendingRecoveryAction`; late hydration SIM + DRYRUN |
| `modules/robot/core/StreamStateMachine.cs` | Same |
| `NT_ADDONS/StreamStateMachine.cs` | Same (+ recovery block uses unified gate) |
| `RobotCore_For_NinjaTrader/Models.ParitySpec.cs` | `initial_submission_price_sanity_ticks` documented deprecated |
| `modules/robot/core/Models.ParitySpec.cs` | Same |
| `configs/analyzer_robot_parity.json` | Removed `initial_submission_price_sanity_ticks`; governance note |
| `docs/robot/EXECUTION_LIFECYCLE.md` | P2.10 gates |
| `modules/robot/core/Tests/MixedStopMarketEntryTests.cs` | Comment updates |

## Before / after (logic)

| Path | Before | After |
|------|--------|--------|
| **First lock** | `initial_submission_price_sanity_ticks` (default 10), fail-open on missing quotes | `breakout_validity_tolerance_ticks` (default 2 per instrument), **same** `IsBreakoutStillValidForEntry`, fail-open on missing quotes |
| **Restart / recovery** | `IsBreakoutValidForResubmit` (2 ticks, fail-closed missing quotes) | `LogAndEvaluateUnifiedBreakoutEntryValidity(..., failClosed: true)` — **same math**, logs `BREAKOUT_*_UNIFIED` |
| **Late hydration** | Bar-only `CheckMissedBreakout` | Unchanged bar scan; if passed and late start, **extra** unified check vs computed `brk` using top-of-book or **last bar close** as synthetic bid/ask |

## New commit reasons

- `NO_TRADE_UNIFIED_BREAKOUT_INVALID` — first lock blocked by unified rule  
- `NO_TRADE_LATE_START_UNIFIED_BREAKOUT_INVALID` — late hydration passed bar scan but failed unified check  

Restart/recovery still use `NO_TRADE_RESTART_RETRY_BREAKOUT_ALREADY_TRIGGERED` / `NO_TRADE_RECOVERY_BREAKOUT_ALREADY_TRIGGERED`.

## Logging

- `BREAKOUT_VALIDATED_UNIFIED` — payload includes `path` (`FIRST_LOCK` | `RESTART` | `RECOVERY` | `LATE_HYDRATION`), bid/ask, brk levels, `tolerance_ticks`  
- `BREAKOUT_INVALIDATED_UNIFIED` — same + `long_invalid`, `short_invalid`, `missing_quotes`, `fail_closed_on_missing_quotes`, `reason`  

## Invariants

- **Same quotes + same brk + same tolerance** → **same boolean** from `IsBreakoutStillValidForEntry` for all paths.  
- **Intentional** difference: `failClosedOnMissingQuotes` is **false** for `FIRST_LOCK` and `LATE_HYDRATION`, **true** for `RESTART` / `RECOVERY` (per spec).  

## Edge cases

1. **LATE_HYDRATION** with no adapter and no bars: no bid/ask → fail-open → ARMED (cannot unify; bars empty).  
2. **Synthetic bid=ask=last close** may differ from live top-of-book by up to spread; may rarely disagree with a later first-lock quote.  
3. **NT_ADDONS** recovery path differs structurally from RobotCore but uses the same unified evaluator before resubmit.

## Build

Run: `dotnet build` on `RobotCore_For_NinjaTrader/Robot.Core.csproj` and `modules/robot/core/Robot.Core.csproj`.
