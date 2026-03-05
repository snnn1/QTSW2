# Forced Flatten Investigation — 2026-03-04

## Summary

**Forced flatten did NOT run on 2026-03-04.** Evidence:

| Check | Result |
|-------|--------|
| `_forced_flatten_markers.json` | File does not exist — never marked as triggered |
| Execution journals `ForcedFlattenTimestamp` | None — `HandleForcedFlatten` was never called |
| `FORCED_FLATTEN_TRIGGERED` in logs | Not found |
| `FORCED_FLATTEN_SKIP_NO_CACHE` in logs | **Found** at 15:51 UTC (9:51 AM CT) for S2 |

## Root Cause

**Session close was never resolved/cached.** `TryGetSessionCloseResult()` returned false, so the forced flatten block skipped every tick with `FORCED_FLATTEN_SKIP_NO_CACHE` (rate-limited to every 5 min).

`ResolveAndSetSessionCloseIfNeeded()` either:
1. Never ran (e.g. robot stayed in Historical, or conditions not met)
2. Ran but `SessionCloseResolver.Resolve()` failed (Bars, TradingHours, SessionIterator, etc.)
3. Ran but threw before calling `SetSessionCloseResolved`

## Prerequisites for Session Close Resolution

`ResolveAndSetSessionCloseIfNeeded()` in `RobotSimStrategy.cs` requires:

- `State == Realtime` (deferred to Realtime only)
- `Bars != null && Bars.Count > 0`
- `tradingDay` from engine (non-empty)
- `spec` (parity spec) not null
- Runs on: Realtime transition, and each `OnBarUpdate` when `tradingDay != _lastSessionCloseResolvedTradingDay`

`SessionCloseResolver.Resolve()` can fail with:

- `NO_BARS`, `EMPTY_BARS`, `TRADING_HOURS_MISSING`
- `SESSION_ITERATOR_ERROR`, `SESSION_CALCULATION_ERROR`
- `HOLIDAY`, `NO_ELIGIBLE_SEGMENTS`
- `TIMEZONE_ERROR`

## Recommendations

1. **Add explicit logging when resolution fails**  
   Log `SESSION_CLOSE_RESOLVER_FAILED` with `FailureReason` and `ExceptionMessage` so we can see why resolution failed.

2. **Log when resolution succeeds**  
   Emit `SESSION_CLOSE_RESOLVED` with `FlattenTriggerUtc` when `SetSessionCloseResolved` is called, so we can confirm the cache was populated.

3. **Run diagnostic script**  
   `python scripts/check_forced_flatten_today.py 2026-03-04` to re-check after fixes.

4. **Verify Realtime transition**  
   Ensure the strategy transitions to Realtime before 15:55 CT so `ResolveAndSetSessionCloseIfNeeded` has a chance to run. If the robot was in Historical during the flatten window, no ticks would run with wall-clock time.

## References

- `docs/robot/FORCED_FLATTEN_RUNDOWN.md` — End-to-end flow
- `RobotEngine.cs` ~1464–1510 — Forced flatten block
- `RobotSimStrategy.cs` ~1771–1802 — `ResolveAndSetSessionCloseIfNeeded`
- `SessionCloseResolver.cs` — Resolution logic
