# Forced Flatten Fix — Root Cause & Implementation Summary

## Root Cause

**Session close cache was never populated** because `ResolveAndSetSessionCloseIfNeeded()` returned early before calling `SessionCloseResolver.Resolve()` and `SetSessionCloseResolved()`. Early return guards include:

- `State != Realtime` — resolution only runs in Realtime; if strategy stayed in Historical during flatten window, resolution never ran
- `Bars == null || Bars.Count == 0` — no bars to iterate
- `tradingDay` empty — engine had no trading date
- `tradingDay == _lastSessionCloseResolvedTradingDay` — already resolved (expected path)
- `spec == null` — parity spec not loaded

With no cache, `TryGetSessionCloseResult()` always returned false, so forced flatten never triggered.

## Fail-Safe Decision Tree

```
GetSessionCloseResultOrFallback(tradingDay, sessionClass, utcNow)
│
├─ TryGetSessionCloseResult() → cache hit?
│  └─ YES → return cached (LIVE_RESOLVER)
│
├─ Path 1: spec != null && market_close_time present
│  └─ ComputeFallbackFromTime(tradingDate, market_close_time)
│     └─ ConstructChicagoTime + ConvertChicagoToUtc (America/Chicago, DST-aware)
│     └─ return fallback (source=SPEC)
│
├─ Path 2: spec == null OR market_close_time empty
│  └─ SESSION_CLOSE_FALLBACK_FAILED (CRITICAL) — once per (tradingDay, sessionClass)
│  └─ ComputeFallbackFromTime(tradingDate, "16:00")  // EMERGENCY_MARKET_CLOSE_DEFAULT
│     └─ return emergency fallback (source=EMERGENCY)
│
└─ Path 3: _time == null (engine not initialized)
   └─ return null → SESSION_CLOSE_CACHE_MISSING after 5 min
```

**When spec and TradingHours are both missing:** Engine uses hard default `16:00` CT (CME equity index close). Emits `SESSION_CLOSE_FALLBACK_FAILED` (CRITICAL) and still flattens at 15:55 CT. No silent skip.

## Timezone Contract (Non-Negotiable)

- `spec.entry_cutoff.market_close_time` is **America/Chicago local time** (DST-aware).
- `TimeService.ConstructChicagoTime` uses `GetUtcOffset(localDateTime)` for correct DST.
- `ConvertChicagoToUtc` produces UTC for comparison with `utcNow`.
- **Never** treat Chicago local as UTC.

Unit test: `SessionCloseFallbackTimeZoneTests.RunDstBoundaryTests()` covers March and November DST weeks.

**Run DST test:**
```bash
dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test DST
# or
.\scripts\run_session_close_dst_test.ps1
```

**Run forced flatten validation (simulated session through close):**
```bash
dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --validate-forced-flatten
# then
python scripts/check_forced_flatten_today.py 2026-03-04
```

## Call Chain (Strategy Tick → Forced Flatten)

```
OnBarUpdate (Realtime)
  → ResolveAndSetSessionCloseIfNeeded()     [strategy]
  → SessionCloseResolver.Resolve(Bars, spec, sessionClass, tradingDay)
  → _engine.SetSessionCloseResolved(tradingDay, sessionClass, result)
  → SESSION_CLOSE_RESOLVED (INFO) when cache set

_engine.Tick(utcNow)
  → GetSessionCloseResultOrFallback(tradingDateStr, sessionClass, utcNow, out usedFallback)
      → Try cache → Path 1 (spec) → Path 2 (emergency 16:00 CT)
  → if closeResult != null && utcNow >= FlattenTriggerUtc
      → HandleForcedFlatten(utcNow) per stream
```

## Modified Files

| File | Changes |
|------|---------|
| `RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs` | SESSION_CLOSE_RESOLVE_SKIPPED (reason, state, bars_count, trading_day, instrument, instrument_key); SESSION_CLOSE_RESOLVER_FAILED (ERROR) with instrument_key |
| `RobotCore_For_NinjaTrader/RobotEngine.cs` | GetSessionCloseResultOrFallback; ComputeFallbackFromTime; emergency path when spec null; one-shot latches; SESSION_CLOSE_RESOLVED (instrument, resolution_source) |
| `RobotCore_For_NinjaTrader/RobotEventTypes.cs` | SESSION_CLOSE_RESOLVE_SKIPPED, SESSION_CLOSE_FALLBACK_USED, SESSION_CLOSE_CACHE_MISSING, SESSION_CLOSE_FALLBACK_FAILED, SESSION_CLOSE_RESOLVER_FAILED |
| `RobotCore_For_NinjaTrader/Tests/SessionCloseFallbackTimeZoneTests.cs` | DST boundary unit tests (March + November) |

## New Logging Events

| Event | Level | When | One-Shot |
|-------|-------|------|----------|
| `SESSION_CLOSE_RESOLVED` | INFO | Cache set successfully (instrument, close_utc, resolution_source) | per (tradingDay, sessionClass) |
| `SESSION_CLOSE_RESOLVE_SKIPPED` | WARN | Early return (reason, state, bars_count, trading_day, instrument, instrument_key) | rate-limited 60s |
| `SESSION_CLOSE_RESOLVER_FAILED` | ERROR | Resolve throws or invalid result | per exception |
| `SESSION_CLOSE_FALLBACK_USED` | WARN | Cache empty; using spec or emergency fallback | once per (tradingDay, sessionClass) |
| `SESSION_CLOSE_FALLBACK_FAILED` | CRITICAL | Spec null; using emergency 16:00 CT | once per (tradingDay, sessionClass) |
| `SESSION_CLOSE_CACHE_MISSING` | ERROR | Cache empty 5+ min, no fallback available | once per (tradingDay, sessionClass) |

## _lastSessionCloseResolvedTradingDay

- **Initialization:** `null` (never set before first successful resolution).
- **Updated:** Only in `ResolveAndSetSessionCloseIfNeeded` after the `try` block completes (both S1 and S2 resolved).
- **Not updated** on catch — failed resolution does not set it, so next tick will retry.

## Acceptance Criteria (Scenario Tests)

| Scenario | Expected |
|---------|----------|
| Normal day | Cache resolves → SESSION_CLOSE_RESOLVED logged → no fallback used |
| Resolver skipped (forced) | Cache empty → fallback used once → flatten triggers at close−5m |
| Spec null | SESSION_CLOSE_FALLBACK_FAILED (CRITICAL) → emergency 16:00 CT → flatten still triggers |
| DST week | Fallback conversion produces correct UTC trigger (RunDstBoundaryTests) |
| Early close day | Spec static 16:00 CT does not encode early close; consider spec enhancement for early-close dates |

## Validation Checklist

- [x] SESSION_CLOSE_RESOLVE_SKIPPED emitted for early returns (reason, instrument_key)
- [x] SESSION_CLOSE_RESOLVED when cache set (instrument, resolution_source)
- [x] SESSION_CLOSE_FALLBACK_USED once per (tradingDay, sessionClass)
- [x] SESSION_CLOSE_CACHE_MISSING once per (tradingDay, sessionClass)
- [x] SESSION_CLOSE_FALLBACK_FAILED when spec null; emergency 16:00 CT used
- [x] Timezone: market_close_time in America/Chicago, DST-aware
- [x] DST unit test (SessionCloseFallbackTimeZoneTests)
- [x] Run simulated session to confirm FORCED_FLATTEN_TRIGGERED, _forced_flatten_markers.json, ForcedFlattenTimestamp in journals
