# Architectural Doctrine: Final Decisions

## Overview

This document formalizes the final architectural decisions that define the system's core philosophy and boundaries. These decisions are **permanent** and should not be changed without explicit architectural review.

---

## 1. Hydration Authority: Single Canonical Source

### Question
**Do you want to formally declare hydration logs as the single canonical source of range truth, and remove the journal fallback entirely?**

### Decision: **YES - Hydration Logs Are Canonical**

**Formal Rule**: **Hydration logs (`hydration_{tradingDay}.jsonl`) are the single canonical source of range truth. No fallbacks.**

### Rationale

**Current State**:
- Hydration log → primary ✅
- Ranges log → fallback ⚠️
- Journal → last resort ⚠️

**Problem**: Multiple fallbacks blur ownership and create ambiguity about which source is authoritative.

**Solution**: **Declare hydration logs canonical, remove journal fallback.**

### Implementation

**Remove Journal Fallback** (`StreamStateMachine.cs:440-456`):
```csharp
// REMOVE THIS FALLBACK:
if (!_rangeLocked && existing.LastState == "RANGE_LOCKED")
{
    // Fallback: Journal indicates locked but hydration/ranges log missing
    LogHealth("WARN", "RANGE_LOCKED_RESTORE_FALLBACK", ...);
    // REMOVE: Do not restore from journal
}
```

**Keep Ranges Log Fallback** (temporary compatibility):
- Keep `ranges_{tradingDay}.jsonl` fallback for backward compatibility
- **Future**: Migrate all ranges logs to hydration log format
- **Target**: Single canonical source (hydration log only)

### Doctrine

**"Hydration logs are the single source of truth for range state. If a hydration log is missing, the range must be recomputed. Journal state is advisory only, never authoritative."**

**Benefits**:
- ✅ Clear ownership (hydration log = canonical)
- ✅ Simplified reasoning (one source, not three)
- ✅ Explicit failure mode (missing log → recompute, not guess)

**Trade-offs**:
- ❌ Less survivability (missing log = no recovery)
- ✅ More predictable (recompute is explicit, not hidden fallback)

---

## 2. Hard Timeout Semantics: Safety Fuse

### Question
**Is the hard timeout meant to be a safety fuse (never block the engine) or a data-quality signal (something you may later gate on)?**

### Decision: **SAFETY FUSE - Always Allow Progression**

**Formal Rule**: **Hard timeout (`RangeStartChicagoTime + 1 minute`) is a liveness guarantee, not a data quality gate. Streams must always progress, even with 0 bars.**

### Rationale

**Current Implementation** (`StreamStateMachine.cs:1295-1342`):
```csharp
// HARD TIMEOUT: Liveness guarantee - PRE_HYDRATION must exit no later than RangeStartChicagoTime + 1 minute
var hardTimeoutChicago = RangeStartChicagoTime.AddMinutes(1.0);
var shouldForceTransition = nowChicago >= hardTimeoutChicago;
```

**Behavior**: Forces transition to `ARMED` even with 0 bars.

**Philosophy**: **Engine must never deadlock. Better to fail later (range lock) than block forever.**

### Doctrine

**"Hard timeout is a safety fuse. It prevents engine deadlock, not data quality issues. Zero-bar streams proceed and fail at range lock (explicit failure), not at hydration (hidden deadlock)."**

**Future Policy** (if needed):
- **Option A**: Keep current (safety fuse only)
- **Option B**: Add separate data-quality gate (e.g., `SUSPENDED_DATA_INSUFFICIENT` if 0 bars + timeout)
- **Current Choice**: **Option A** (safety fuse only)

**If Option B Later**:
- Add `SUSPENDED_DATA_INSUFFICIENT` terminal state
- Gate: `if (barCount == 0 && shouldForceTransition) → SUSPENDED_DATA_INSUFFICIENT`
- **But**: Keep hard timeout separate (safety fuse remains)

### Benefits

✅ **Engine Liveness**: Never deadlocks waiting for data  
✅ **Explicit Failures**: Zero-bar streams fail at range lock (visible), not hydration (hidden)  
✅ **Predictable**: Timeout behavior is deterministic  

---

## 3. Zero-Bar Streams: Explicit Terminal State

### Question
**Do you want "ZERO_BAR_HYDRATION" to become a formal terminal reason (distinct from generic NO_TRADE), or is its current implicit handling sufficient?**

### Decision: **YES - Add Explicit Terminal State**

**Formal Rule**: **Zero-bar hydration is a distinct terminal outcome. Add `ZERO_BAR_HYDRATION` as explicit commit reason and terminal state.**

### Rationale

**Current State**: Zero-bar streams commit as generic `NO_TRADE_MARKET_CLOSE` or fail at range lock (implicit).

**Problem**: Cannot distinguish "no breakout" from "no data" in statistics.

**Solution**: **Add explicit `ZERO_BAR_HYDRATION` terminal state.**

### Implementation

**Add Terminal State** (`StreamTerminalState` enum):
```csharp
public enum StreamTerminalState
{
    NO_TRADE,              // No breakout occurred
    TRADE_COMPLETED,       // Trade completed
    SKIPPED_CONFIG,        // Skipped at timetable parse
    FAILED_RUNTIME,        // Runtime failure
    SUSPENDED_DATA,        // Suspended due to insufficient data
    ZERO_BAR_HYDRATION     // NEW: Zero bars loaded during hydration
}
```

**Update Commit Logic** (`StreamStateMachine.cs:4858`):
```csharp
// In DetermineTerminalState():
if (barCount == 0 && (commitReason.Contains("PRE_HYDRATION") || commitReason.Contains("TIMEOUT")))
{
    return StreamTerminalState.ZERO_BAR_HYDRATION;
}
```

**Update Commit Reasons**:
- `"NO_TRADE_ZERO_BAR_HYDRATION"` (when CSV missing or BarsRequest fails)
- `"NO_TRADE_MARKET_CLOSE"` (when market closes without breakout)
- `"NO_TRADE_LATE_START_MISSED_BREAKOUT"` (when late start + missed breakout)

### Doctrine

**"Zero-bar hydration is a distinct failure mode. It represents data unavailability, not market conditions. Statistics must distinguish 'no trade' (market) from 'no data' (system)."**

**Benefits**:
- ✅ **Stats Accuracy**: Can filter out zero-bar days from performance stats
- ✅ **Post-Mortem Clarity**: Clear distinction between "no opportunity" and "no data"
- ✅ **Monitoring**: Can track data availability separately from trading performance

---

## 4. Late-Start Philosophy: Core Doctrine

### Question
**Is your formal philosophy: "If the system was not live to observe the breakout in real time, the opportunity does not exist."?**

### Decision: **YES - This Is Core Doctrine**

**Formal Rule**: **"If the system was not live to observe the breakout in real time, the opportunity does not exist. No back-fills, no discretionary fills, no exceptions."**

### Rationale

**Current Implementation** (`StreamStateMachine.cs:903-976`):
- Strict missed-breakout detection
- Immediate `NO_TRADE_LATE_START_MISSED_BREAKOUT` commit if breakout occurred
- No back-fill logic

**Philosophy**: **Real-time observation is required. Historical reconstruction is for analysis, not execution.**

### Doctrine

**"Real-time observation is a hard requirement. If a breakout occurred while the system was offline, that opportunity is permanently lost. No back-fills, no discretionary fills, no exceptions. This rule is core doctrine and must never be softened."**

**Enforcement**:
- ✅ **Strict Detection**: `CheckMissedBreakout()` uses strict inequalities (`>`, `<`)
- ✅ **Immediate Commit**: If missed breakout detected → commit immediately
- ✅ **No Back-Fill**: Never fill orders retroactively

**Why This Matters**:
- **Determinism**: System behavior is predictable (no "what if" scenarios)
- **Risk Management**: Prevents trading on stale information
- **Audit Trail**: Clear record of why trades were not taken

**Future-Proofing**: This rule prevents feature creep ("just add back-fill logic") that would undermine system integrity.

---

## 5. Gap Tracking: Observational Forever

### Question
**Is gap tracking purely observational forever, or do you foresee a future where certain gap patterns become advisory (not blocking) or fatal?**

### Decision: **OBSERVATIONAL FOREVER - No Enforcement**

**Formal Rule**: **Gap tracking is purely observational. Gaps are logged and classified, but never block execution or invalidate ranges.**

### Rationale

**Current State** (`StreamStateMachine.cs:2633-2664`):
```csharp
// TEMPORARILY DISABLED: DATA_FEED_FAILURE gap invalidation
// Previously, DATA_FEED_FAILURE gaps would invalidate ranges, but this is now disabled
// All gaps (both DATA_FEED_FAILURE and LOW_LIQUIDITY) are tolerated and logged for monitoring
```

**Philosophy**: **Gaps are market reality. System must operate with imperfect data, not block on data quality.**

### Doctrine

**"Gap tracking is observational only. Gaps are logged, classified (DATA_FEED_FAILURE vs LOW_LIQUIDITY), and monitored, but never block execution. The system operates with imperfect data, not perfect data."**

**Classification** (for monitoring):
- **DATA_FEED_FAILURE**: Gaps during PRE_HYDRATION, from BARSREQUEST, or very low bar count
- **LOW_LIQUIDITY**: Gaps during RANGE_BUILDING from LIVE feed (legitimate market sparsity)

**Monitoring Use Cases**:
- Track data feed health
- Identify systematic data issues
- Monitor market liquidity patterns
- **Not** for blocking execution

**Future Considerations**:
- **Advisory**: Could add alerts for extreme gap patterns (not blocking)
- **Never**: Should never block execution based on gaps
- **Rationale**: Better to trade with gaps than not trade at all

---

## 6. Stream Granularity: Fixed Forever

### Question
**Do you want stream identity to remain fixed at slot-time granularity forever, or do you anticipate multiple streams per slot in the future?**

### Decision: **FIXED FOREVER - One Stream Per Slot**

**Formal Rule**: **Stream identity is fixed at `(tradingDate, session, slotTime, canonicalInstrument)` granularity. One stream = one opportunity = one trade (or no trade). This will never change.**

### Rationale

**Current Architecture**:
- Stream ID = `{canonicalInstrument}{session}{slotNumber}` (e.g., "ES1", "GC2")
- One stream per slot
- Deterministic identity

**Philosophy**: **Simplicity and determinism over flexibility. Stream identity is a fundamental invariant.**

### Doctrine

**"Stream identity is fixed at slot-time granularity. One stream = one opportunity = one trade (or no trade). This granularity will never change. If multiple variants are needed (e.g., volatility regimes, filters), they must be separate slots in the timetable, not multiple streams per slot."**

**Implications**:
- ✅ **Deterministic**: Stream identity is predictable and stable
- ✅ **Simple**: No ambiguity about which stream is which
- ✅ **Scalable**: Add variants via timetable (new slots), not stream multiplication

**Future Extension Path** (if needed):
- **Option A**: Add new slots to timetable (e.g., "ES1_VOLATILE", "ES1_CALM")
- **Option B**: Use stream filters (external to robot, in analysis layer)
- **Current Choice**: **Option A** (new slots, not multiple streams)

**Why This Matters**:
- Prevents architectural drift ("just add another stream per slot")
- Maintains deterministic identity
- Keeps stream-level P&L attribution clean

---

## 7. Success Definition: Trust Through Predictability

### Question
**How will you personally know this system is "done"?**

### Decision: **"Done" = Failures Are Boring and Predictable**

**Formal Definition**: **The system is "done" when failures are boring, predictable, and fully explained. When every failure has a clear cause, a clear resolution path, and a clear prevention strategy.**

### Criteria

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
- ✅ Fixes are straightforward (not "works on my machine")

**4. Edge Cases Are Documented**:
- ✅ Every edge case has a documented behavior
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

## Summary: Core Doctrine

### 1. Hydration Authority
**"Hydration logs are canonical. No journal fallback. Missing log → recompute."**

### 2. Hard Timeout
**"Hard timeout is a safety fuse. Always allow progression. Zero bars → fail at range lock, not hydration."**

### 3. Zero-Bar Streams
**"Zero-bar hydration is a distinct terminal state. Distinguish 'no data' from 'no trade'."**

### 4. Late-Start Philosophy
**"Real-time observation required. No back-fills, no exceptions. If system was offline, opportunity is lost."**

### 5. Gap Tracking
**"Gaps are observational only. Log, classify, monitor. Never block execution."**

### 6. Stream Granularity
**"One stream per slot, forever. Fixed granularity. Variants via timetable, not stream multiplication."**

### 7. Success Definition
**"Done when failures are boring and predictable. Trust through predictability, not perfection."**

---

## Implementation Checklist

### Immediate Changes

- [ ] **Remove Journal Fallback**: Remove journal-based range restoration fallback
- [ ] **Add ZERO_BAR_HYDRATION**: Add explicit terminal state for zero-bar hydration
- [ ] **Document Late-Start Rule**: Add to architecture docs as core doctrine
- [ ] **Formalize Gap Policy**: Document that gaps are observational only

### Future Considerations

- [ ] **Migrate Ranges Logs**: Convert all ranges logs to hydration log format
- [ ] **Remove Ranges Fallback**: After migration, remove ranges log fallback
- [ ] **Add Gap Advisory Alerts**: Optional alerts for extreme gap patterns (not blocking)

---

## Doctrine Enforcement

**These decisions are architectural doctrine. Changes require:**
1. Explicit architectural review
2. Impact analysis on all dependent systems
3. Documentation updates
4. Test coverage for new behavior

**Doctrine Violations**:
- Adding journal fallback (violates hydration authority)
- Blocking on gaps (violates gap policy)
- Adding back-fill logic (violates late-start philosophy)
- Multiple streams per slot (violates stream granularity)

---

**End of Architectural Doctrine**
