# Architectural Decisions: Analysis & Recommendations

## Overview

This document provides detailed analysis and recommendations for the seven architectural questions, based on codebase patterns and system design principles.

---

## 1. Hydration Authority: Recommendation

### Current State Analysis

**Fallback Chain** (`StreamStateMachine.cs:4498-4524`):
1. Hydration log (`hydration_{tradingDay}.jsonl`) → Primary ✅
2. Ranges log (`ranges_{tradingDay}.jsonl`) → Fallback ⚠️
3. Journal (`LastState == "RANGE_LOCKED"`) → Last resort ⚠️

**Code Evidence**:
```csharp
// Try hydration log first
var hydrationFile = Path.Combine(_projectRoot, "logs", "robot", $"hydration_{tradingDay}.jsonl");
if (!File.Exists(hydrationFile))
{
    // Fallback to ranges file
    hydrationFile = Path.Combine(_projectRoot, "logs", "robot", $"ranges_{tradingDay}.jsonl");
    // ...
    if (!File.Exists(hydrationFile))
    {
        // Fallback: Journal indicates locked but hydration/ranges log missing
        // Will proceed without restoration - range will be recomputed
    }
}
```

### Recommendation: **YES - Declare Hydration Logs Canonical**

**Rationale**:
1. **Clear Ownership**: Single source eliminates ambiguity
2. **Explicit Failure**: Missing log → recompute (explicit), not guess (hidden fallback)
3. **Simplified Reasoning**: One source, not three
4. **Audit Trail**: Hydration logs are append-only, journal can be modified

**Implementation**:
- ✅ Keep hydration log as canonical
- ✅ Keep ranges log fallback (temporary, for backward compatibility)
- ❌ **Remove journal fallback** (blurs ownership)

**Code Change**:
```csharp
// REMOVE journal fallback (lines 440-456)
// If hydration/ranges log missing → recompute range (explicit failure)
// Do NOT fall back to journal LastState check
```

**Doctrine**: **"Hydration logs are canonical. Missing log → recompute. Journal is advisory only."**

---

## 2. Hard Timeout Semantics: Recommendation

### Current State Analysis

**Hard Timeout** (`StreamStateMachine.cs:1295-1342`):
```csharp
// HARD TIMEOUT: Liveness guarantee - PRE_HYDRATION must exit no later than RangeStartChicagoTime + 1 minute
var hardTimeoutChicago = RangeStartChicagoTime.AddMinutes(1.0);
var shouldForceTransition = nowChicago >= hardTimeoutChicago;
```

**Behavior**: Forces transition even with 0 bars.

**Zero-Bar Handling** (`StreamStateMachine.cs:1432-1446`):
```csharp
// Log timeout if transitioning without bars (but not forced)
else if (barCount == 0 && nowChicago >= RangeStartChicagoTime)
{
    _log.Write(..., "PRE_HYDRATION_TIMEOUT_NO_BARS", ...);
}
```

### Recommendation: **SAFETY FUSE - Always Allow Progression**

**Rationale**:
1. **Engine Liveness**: Prevents deadlock (critical for unattended operation)
2. **Explicit Failures**: Zero-bar streams fail at range lock (visible), not hydration (hidden)
3. **Predictable**: Timeout behavior is deterministic

**Future Policy** (if needed):
- **Keep**: Hard timeout as safety fuse (always allow progression)
- **Add**: Separate data-quality gate if needed (e.g., `SUSPENDED_DATA_INSUFFICIENT`)
- **Separate**: Keep timeout (liveness) separate from data quality (policy)

**Doctrine**: **"Hard timeout is a safety fuse. It prevents deadlock, not data quality issues. Zero-bar streams proceed and fail explicitly at range lock."**

---

## 3. Zero-Bar Streams: Recommendation

### Current State Analysis

**Zero-Bar Scenarios**:
1. **Missing CSV** (`StreamStateMachine.cs:3599-3615`): Logs `PRE_HYDRATION_ZERO_BARS`, marks complete
2. **BarsRequest Timeout**: Hard timeout forces transition with 0 bars
3. **Result**: Stream proceeds, fails at range lock (implicit)

**Current Terminal States**:
- `NO_TRADE` (generic)
- `TRADE_COMPLETED`
- `SKIPPED_CONFIG`
- `FAILED_RUNTIME`
- `SUSPENDED_DATA`

**Gap**: No explicit `ZERO_BAR_HYDRATION` state.

### Recommendation: **YES - Add Explicit Terminal State**

**Rationale**:
1. **Stats Accuracy**: Need to distinguish "no trade" (market) from "no data" (system)
2. **Post-Mortem Clarity**: Clear distinction in logs and statistics
3. **Monitoring**: Track data availability separately from trading performance

**Implementation**:
```csharp
// Add to StreamTerminalState enum
ZERO_BAR_HYDRATION  // Zero bars loaded during hydration

// Update DetermineTerminalState()
if (barCount == 0 && (commitReason.Contains("PRE_HYDRATION") || commitReason.Contains("TIMEOUT")))
{
    return StreamTerminalState.ZERO_BAR_HYDRATION;
}
```

**Doctrine**: **"Zero-bar hydration is a distinct failure mode. It represents data unavailability, not market conditions. Statistics must distinguish 'no trade' from 'no data'."**

---

## 4. Late-Start Philosophy: Recommendation

### Current State Analysis

**Missed Breakout Detection** (`StreamStateMachine.cs:903-976`):
- Strict inequalities (`bar.High > rangeHigh`, `bar.Low < rangeLow`)
- Immediate commit if missed breakout detected
- No back-fill logic

**Code Evidence**:
```csharp
// CRITICAL: Use STRICT inequalities for breakout detection
// bar.High > rangeHigh (not >=) - price must exceed range high
// bar.Low < rangeLow (not <=) - price must exceed range low
// Price equals range boundary is NOT a breakout
if (bar.High > rangeHigh)
{
    return (true, bar.TimestampUtc, barChicagoTime, bar.High, "LONG");
}
```

**Late-Start Response** (`StreamStateMachine.cs:1594-1612`):
```csharp
if (missedBreakout)
{
    LogHealth("INFO", "LATE_START_MISSED_BREAKOUT", ...);
    Commit(utcNow, "NO_TRADE_LATE_START_MISSED_BREAKOUT", "NO_TRADE_LATE_START_MISSED_BREAKOUT");
    return;
}
```

### Recommendation: **YES - This Is Core Doctrine**

**Rationale**:
1. **Determinism**: System behavior is predictable (no "what if" scenarios)
2. **Risk Management**: Prevents trading on stale information
3. **Audit Trail**: Clear record of why trades were not taken
4. **Future-Proofing**: Prevents feature creep ("just add back-fill logic")

**Doctrine**: **"Real-time observation is a hard requirement. If a breakout occurred while the system was offline, that opportunity is permanently lost. No back-fills, no discretionary fills, no exceptions. This rule is core doctrine and must never be softened."**

**Enforcement**:
- ✅ Already encoded in code (strict detection, immediate commit)
- ✅ Document as core doctrine
- ✅ Never add back-fill logic

---

## 5. Gap Tracking: Recommendation

### Current State Analysis

**Gap Tolerance** (`StreamStateMachine.cs:2633-2664`):
```csharp
// TEMPORARILY DISABLED: DATA_FEED_FAILURE gap invalidation
// Previously, DATA_FEED_FAILURE gaps would invalidate ranges, but this is now disabled
// All gaps (both DATA_FEED_FAILURE and LOW_LIQUIDITY) are tolerated and logged for monitoring
```

**Gap Classification** (`StreamStateMachine.cs:2613-2631`):
- **DATA_FEED_FAILURE**: PRE_HYDRATION gaps, BARSREQUEST gaps, very low bar count
- **LOW_LIQUIDITY**: RANGE_BUILDING gaps from LIVE feed

**Current Behavior**: All gaps tolerated, logged, never block execution.

### Recommendation: **OBSERVATIONAL FOREVER - No Enforcement**

**Rationale**:
1. **Market Reality**: Gaps are part of trading (low liquidity, data feed issues)
2. **System Resilience**: Better to trade with gaps than not trade at all
3. **Monitoring Value**: Gap tracking provides valuable diagnostics without blocking

**Future Considerations**:
- **Advisory Alerts**: Could add alerts for extreme gap patterns (not blocking)
- **Never Block**: Should never block execution based on gaps
- **Monitoring**: Use gaps for data feed health monitoring

**Doctrine**: **"Gap tracking is observational only. Gaps are logged, classified, and monitored, but never block execution. The system operates with imperfect data, not perfect data."**

---

## 6. Stream Granularity: Recommendation

### Current State Analysis

**Stream Identity** (`StreamStateMachine.cs:277`):
```csharp
Stream = directive.stream;  // e.g., "ES1", "GC2"
```

**Single-Trade Contract** (`STREAM_ARCHITECTURE_DECISIONS.md:14-91`):
- One stream = one opportunity = one trade (or no trade)
- Enforced by `_entryDetected` flag
- Commit mechanism prevents re-arming

**Current Architecture**: Fixed at `(tradingDate, session, slotTime, canonicalInstrument)` granularity.

### Recommendation: **FIXED FOREVER - One Stream Per Slot**

**Rationale**:
1. **Determinism**: Stream identity is predictable and stable
2. **Simplicity**: No ambiguity about which stream is which
3. **P&L Attribution**: Clean stream-level P&L (one stream = one trade)
4. **Scalability**: Add variants via timetable (new slots), not stream multiplication

**Future Extension Path** (if needed):
- **Option A**: Add new slots to timetable (e.g., "ES1_VOLATILE", "ES1_CALM")
- **Option B**: Use stream filters (external to robot, in analysis layer)
- **Current Choice**: **Option A** (new slots, not multiple streams)

**Doctrine**: **"Stream identity is fixed at slot-time granularity. One stream = one opportunity = one trade (or no trade). This granularity will never change. Variants must be separate slots in the timetable, not multiple streams per slot."**

---

## 7. Success Definition: Recommendation

### Analysis

**Current System Strengths**:
- ✅ Comprehensive logging (hydration, range, execution)
- ✅ Fail-closed behaviors (missing context → block execution)
- ✅ Deterministic P&L attribution
- ✅ Recovery mechanisms (hydration logs, journal recovery)

**Current System Gaps**:
- ⚠️ Some failures are implicit (zero-bar → fail at range lock)
- ⚠️ Some edge cases not explicitly documented
- ⚠️ Recovery fallbacks blur ownership

### Recommendation: **"Done" = Failures Are Boring and Predictable**

**Criteria**:

**1. Trust Unattended**:
- ✅ System runs without intervention
- ✅ Failures are logged, not hidden
- ✅ Recovery is automatic or clearly documented

**2. Post-Trade Analysis Never Surprises**:
- ✅ Every trade is explainable from logs
- ✅ Every no-trade is explainable from logs
- ✅ P&L attribution is deterministic and verifiable

**3. Failures Are Boring**:
- ✅ Failures follow predictable patterns
- ✅ Root causes are identifiable
- ✅ Fixes are straightforward

**4. Edge Cases Are Documented**:
- ✅ Every edge case has documented behavior
- ✅ Every edge case has a test
- ✅ Every edge case has a recovery path

### Doctrine

**"The system is 'done' when I stop thinking about edge cases because they're all handled predictably. When failures are boring (expected, logged, recoverable) rather than surprising (unexpected, hidden, unrecoverable)."**

**Signs of "Done"**:
- ✅ **Logs Tell the Story**: Every failure has a clear log trail
- ✅ **Recovery Is Automatic**: System recovers without intervention
- ✅ **Statistics Are Trustworthy**: P&L attribution is deterministic
- ✅ **Edge Cases Are Handled**: No "what if" scenarios remain

**When to Stop Engineering**:
- ✅ All edge cases have documented behavior
- ✅ All failures have clear resolution paths
- ✅ System runs unattended without surprises
- ✅ Post-trade analysis never reveals hidden failures

**When to Start Operating**:
- ✅ System is trusted for unattended operation
- ✅ Failures are predictable and recoverable
- ✅ Statistics are reliable for decision-making
- ✅ Edge cases are handled, not ignored

---

## Implementation Priority

### High Priority (Core Doctrine)

1. **Remove Journal Fallback** (Hydration Authority)
   - Impact: Simplifies recovery logic
   - Risk: Low (recompute is explicit failure)

2. **Add ZERO_BAR_HYDRATION Terminal State** (Zero-Bar Streams)
   - Impact: Improves statistics accuracy
   - Risk: Low (additive change)

3. **Document Late-Start Rule** (Late-Start Philosophy)
   - Impact: Prevents future feature creep
   - Risk: None (documentation only)

### Medium Priority (Clarification)

4. **Formalize Gap Policy** (Gap Tracking)
   - Impact: Clarifies monitoring vs enforcement
   - Risk: None (documentation only)

5. **Document Stream Granularity** (Stream Granularity)
   - Impact: Prevents architectural drift
   - Risk: None (documentation only)

### Low Priority (Future)

6. **Migrate Ranges Logs** (Hydration Authority)
   - Impact: Single canonical source
   - Risk: Medium (requires migration script)

7. **Add Gap Advisory Alerts** (Gap Tracking)
   - Impact: Enhanced monitoring
   - Risk: Low (additive feature)

---

## Decision Summary

| Question | Recommendation | Priority | Risk |
|----------|---------------|----------|------|
| 1. Hydration Authority | Remove journal fallback | High | Low |
| 2. Hard Timeout | Safety fuse (keep current) | High | None |
| 3. Zero-Bar Streams | Add explicit terminal state | High | Low |
| 4. Late-Start Philosophy | Core doctrine (already encoded) | High | None |
| 5. Gap Tracking | Observational forever | Medium | None |
| 6. Stream Granularity | Fixed forever | Medium | None |
| 7. Success Definition | Failures are boring | Low | None |

---

**End of Analysis**
