# Stream Architecture Decisions & Answers

## Overview

This document provides authoritative answers to five critical architectural questions about stream lifecycle, terminal states, intent identity, P&L aggregation, and fail-closed principles.

---

## 1. Stream Lifecycle Boundary: Single-Trade Contract

### Question
**What is the formal lifecycle boundary of a stream? Is a stream strictly single-trade by definition, or can it represent multiple retries, re-entries after stop, or multiple intents sequentially in the same slot?**

### Answer: **HARD CONTRACT - Single-Trade Only**

**Formal Definition**: A stream represents **exactly one trading opportunity** for `(tradingDate, session, slotTime, canonicalInstrument)`. Once that opportunity is resolved (trade completed OR no trade), the stream is **permanently committed** and cannot be re-armed.

### Evidence from Code

**1. Entry Detection Flag** (`StreamStateMachine.cs:193, 5019-5040`):
```csharp
private bool _entryDetected;

private void RecordIntendedEntry(...)
{
    if (_entryDetected)
    {
        // Log and return - entry already exists
        _log.Write(..., "BREAKOUT_DETECTED_ALREADY_ENTERED", ...);
        return; // Already detected
    }
    _entryDetected = true;
    // ... record entry
}
```

**Enforcement**: Once `_entryDetected = true`, subsequent breakouts are **ignored**. This prevents multiple entries in the same stream.

**2. Commit Mechanism** (`StreamStateMachine.cs:4858-4920`):
```csharp
private void Commit(DateTimeOffset utcNow, string commitReason, string eventType)
{
    _journal.Committed = true;
    _journal.CommitReason = commitReason;
    // ... persist journal
    State = StreamState.DONE;
}
```

**Enforcement**: Once `Committed = true`, stream transitions to `DONE` and cannot re-enter `ARMED`.

**3. Blueprint Documentation** (`NinjaTrader Robot Blueprint (Execution Layer).txt:384-386`):
```
Intra-stream re-arming prohibition (Hard rule)

Once a stream enters DONE for a trading_date, it must never re-enter ARMED 
for that trading_date under any circumstances.
```

**4. Timetable Update Guard** (`StreamStateMachine.cs:567-580`):
```csharp
public void ApplyDirectiveUpdate(string newSlotTimeChicago, DateOnly tradingDate, DateTimeOffset utcNow)
{
    // Allowed only if not committed
    if (_journal.Committed) {
        // Block updates after commit
        return;
    }
    // ... apply update
}
```

**Enforcement**: Timetable updates are blocked after commit.

### Implications

✅ **Stream-level sufficiency is permanently locked**: Stream = one opportunity = one trade (or no trade)

✅ **Prevents "just add a re-entry" drift**: Cannot add re-entry logic without violating contract

✅ **Deterministic P&L attribution**: One stream → one trade → one P&L (or zero)

✅ **Idempotency guarantee**: Same intent fields → same IntentId → same trade (or blocked as duplicate)

### What This Means

- **Multiple retries**: ❌ Not allowed. If entry order fails, stream commits as "NO_TRADE" (or error state)
- **Re-entries after stop**: ❌ Not allowed. Stream is committed after trade completion
- **Multiple intents sequentially**: ❌ Not allowed. `_entryDetected` flag prevents second intent

**This is a HARD CONTRACT, not accidental.** The system is designed around this constraint.

---

## 2. Stream Terminal States: Formal Classification

### Question
**Is a stream allowed to end without TradeCompleted? Do you want a formal terminal classification for every stream?**

### Answer: **YES - Streams Can End Without TradeCompleted**

**Current Terminal Outcomes**:
1. ✅ **TradeCompleted** (STOP or TARGET) → `TradeCompleted = true` in ExecutionJournalEntry
2. ✅ **NO_TRADE** (no breakout) → `CommitReason = "NO_TRADE_MARKET_CLOSE"` or `"NO_TRADE_LATE_START_MISSED_BREAKOUT"`
3. ✅ **SUSPENDED_DATA_INSUFFICIENT** → `State = SUSPENDED_DATA_INSUFFICIENT`
4. ✅ **SKIPPED** (at timetable parse) → Never created (not a stream instance)

### Current State: **Implicit, Not Formal**

**Current Classification** (derivable but not explicit):
- **CommitReason** in `StreamJournal` (string): `"NO_TRADE_MARKET_CLOSE"`, `"STREAM_STAND_DOWN"`, etc.
- **TradeCompleted** in `ExecutionJournalEntry` (bool): Only set if trade completed
- **State** in `StreamStateMachine` (enum): `DONE`, `SUSPENDED_DATA_INSUFFICIENT`

**Gap**: No formal enum/classification that combines all terminal states.

### Recommendation: **Add Formal Terminal Classification**

**Proposed Enum**:
```csharp
public enum StreamTerminalState
{
    NO_TRADE,              // No breakout occurred
    TRADE_COMPLETED,       // Trade completed (STOP or TARGET)
    SKIPPED_CONFIG,        // Skipped at timetable parse (canonical mismatch, disabled)
    FAILED_RUNTIME,        // Runtime failure (stand down, corruption, etc.)
    SUSPENDED_DATA         // Suspended due to insufficient data
}
```

**Benefits**:
- ✅ Explicit terminal state per stream
- ✅ Enables stream-level performance stats including "no-trade days"
- ✅ Clear distinction between "no trade" and "trade completed"
- ✅ Queryable: "All streams that ended in NO_TRADE on 2025-02-01"

**Current Workaround**: Derive from `CommitReason` + `TradeCompleted` + `State`

### Design Decision Required

**Option A: Keep Journal Execution-Centric**
- Journal only tracks executed trades
- No-trade streams are tracked separately (StreamJournal)
- Aggregation must combine both sources

**Option B: Unified Terminal Classification**
- Add `TerminalState` enum to `StreamJournal`
- Set on commit: `TerminalState = TradeCompleted ? TRADE_COMPLETED : NO_TRADE`
- Enables single-source stream-level stats

**Recommendation**: **Option B** - Unified terminal classification enables cleaner stream-level reporting.

---

## 3. IntentId Reuse: Hard Invariant

### Question
**Is IntentId allowed to be reused across restarts by design? Is changing any of the 15 fields a hard invariant that must imply a new trade?**

### Answer: **YES - IntentId Reuse is Intentional and Permanent**

**Design Choice**: **Deterministic Identity Over Execution Lineage**

### Evidence

**IntentId Computation** (`ExecutionJournal.cs:52-72`):
```csharp
public static string ComputeIntentId(
    string tradingDate,
    string stream,
    string instrument,
    string session,
    string slotTimeChicago,
    string? direction,
    decimal? entryPrice,
    decimal? stopPrice,
    decimal? targetPrice,
    decimal? beTrigger)
{
    var canonical = $"{tradingDate}|{stream}|{instrument}|...";
    // Hash → 16-char hex string
    return hexString.Substring(0, 16);
}
```

**Key Property**: Same 15 fields → Same hash → Same IntentId

**Idempotency Check** (`ExecutionJournal.cs:77-130`):
```csharp
public bool IsIntentSubmitted(string intentId, string tradingDate, string stream)
{
    // Check cache and disk for existing entry
    if (entry.EntrySubmitted || entry.EntryFilled) {
        return true; // Already submitted - block duplicate
    }
    return false;
}
```

**Enforcement**: Same IntentId → Same trade → Blocked as duplicate (idempotent)

### Implications

✅ **Restart Safety**: System restart → Same intent fields → Same IntentId → Prevents duplicate submission

✅ **Deterministic Identity**: IntentId is **not** a random UUID - it's a deterministic hash

✅ **Hard Invariant**: Changing **any** of the 15 fields → New hash → New IntentId → New trade

### Design Decision: **Intentional and Permanent**

**This is NOT a bug - it's a feature.** The system chooses:
- ✅ **Deterministic identity** (same fields = same ID)
- ❌ **NOT execution lineage** (each attempt = new ID)

**Why This Matters**:
- Prevents duplicate submissions on restart
- Enables idempotent order placement
- Makes intent identity **testable** (can compute IntentId from fields)

**Would You Ever Want "Logical Intent" vs "Execution Attempt"?**

**Answer**: **NO** - The current design is correct. If you need execution lineage (multiple attempts with same fields), that would require:
- Adding an "attempt number" to IntentId computation (breaks idempotency)
- Or tracking execution attempts separately (adds complexity)

**Recommendation**: **Keep current design** - Deterministic identity is the right choice for idempotency.

---

## 4. Stream-External P&L Consumers: Design Goal

### Question
**Do you ever want stream-external P&L consumers? Is it a design goal that all higher-level reporting must be derived from stream journals, or do you want a secondary derived ledger?**

### Answer: **Current Design: Stream Journals Are Authoritative**

**Current Architecture**:
- **Authoritative Source**: `ExecutionJournalEntry` per `(tradingDate, stream, intentId)`
- **Aggregation**: External/manual (scan journal files, aggregate)
- **Portfolio Views**: Derived, not persisted

### Current State

**Stream-Level P&L Query** (manual):
```csharp
// Pattern: {tradingDate}_{stream}_*.json
var files = Directory.GetFiles(journalDir, $"{tradingDate}_{stream}_*.json");
var streamPnL = 0m;
foreach (var file in files) {
    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(File.ReadAllText(file));
    if (entry.TradeCompleted) {
        streamPnL += entry.RealizedPnLNet;
    }
}
```

**No Built-In Aggregation**: No `GetStreamPnLSummary()` or `GetDayPnLSummary()` methods

### Design Decision Required

**Option A: Stream Journals Are Authoritative (Current)**
- ✅ Single source of truth
- ✅ No synchronization issues
- ❌ Aggregation requires scanning files
- ❌ Performance: O(n) scans for portfolio views

**Option B: Secondary Derived Ledger (Read-Only)**
- ✅ Fast portfolio queries (pre-aggregated)
- ✅ Convenience for reporting
- ❌ Must keep in sync with journals
- ❌ Adds complexity (what if ledger diverges?)

### Recommendation: **Hybrid Approach**

**Keep Journals Authoritative, Add Read-Only Aggregation Layer**:

```csharp
// Read-only aggregation (derived from journals)
public class StreamPnLAggregator
{
    // Fast queries (cached, rebuilt on demand)
    public StreamPnLSummary GetStreamSummary(string tradingDate, string stream);
    public DayPnLSummary GetDaySummary(string tradingDate);
    public PortfolioPnLSummary GetPortfolioSummary(DateOnly startDate, DateOnly endDate);
    
    // Rebuild cache from journals (on demand or scheduled)
    public void RebuildCache(string tradingDate);
}
```

**Key Properties**:
- ✅ Journals remain authoritative
- ✅ Aggregation is **derived** (read-only)
- ✅ Cache can be rebuilt from journals (verification)
- ✅ Performance: Fast queries without scanning files

**Design Goal**: **Journals are truth, aggregation is convenience**

---

## 5. The "Never" Question: Fail-Closed Principles

### Question
**What is the one thing you absolutely never want this system to do?**

### Answer: **Multiple Fail-Closed Principles (Ranked)**

Based on code analysis, here are the fail-closed principles already encoded:

### 1. **Never Trade Without Stream Attribution** (HIGHEST PRIORITY)

**Encoded In**: `ResolveIntentContextOrFailClosed()`, `RecordEntryFill()` validation

**Principle**: "I would rather the system stop trading entirely than ever **record a fill without a valid stream identity**."

**Evidence**:
```csharp
// RecordEntryFill validation
if (string.IsNullOrWhiteSpace(tradingDate)) {
    // Fail-closed: do not persist
    return;
}
if (string.IsNullOrWhiteSpace(stream)) {
    // Fail-closed: do not persist
    return;
}
```

**Impact**: Orphan fills are logged but **not recorded** in journal. Execution is blocked.

### 2. **Never Hold an Unprotected Position**

**Encoded In**: `HandleEntryFill()` - Intent completeness check

**Principle**: "I would rather the system stop trading entirely than ever **hold a position without protective orders**."

**Evidence**:
```csharp
// HandleEntryFill
if (intent.Direction == null || intent.StopPrice == null || intent.TargetPrice == null) {
    // Flatten immediately (fail-closed)
    FlattenWithRetry(intentId, intent.Instrument, utcNow);
    StandDownStream(intent.Stream);
    return;
}
```

**Impact**: Entry fill → Missing protective fields → Position flattened immediately → Stream stood down

### 3. **Never Double-Count a Fill**

**Encoded In**: Delta quantities only, cumulative tracking internal

**Principle**: "I would rather the system stop trading entirely than ever **double-count a fill quantity**."

**Evidence**:
```csharp
// RecordEntryFill accepts delta only
public void RecordEntryFill(..., int fillQuantity, ...)  // Delta, NOT cumulative
{
    entry.EntryFilledQuantityTotal += fillQuantity;  // Cumulative updated internally
}
```

**Impact**: Method signature enforces delta-only (compiler prevents cumulative passing)

### 4. **Never Calculate P&L on Incomplete Trade**

**Encoded In**: `RecordExitFill()` - Completion gating

**Principle**: "I would rather the system stop trading entirely than ever **calculate P&L before trade completion**."

**Evidence**:
```csharp
// RecordExitFill
if (entry.ExitFilledQuantityTotal == entry.EntryFilledQuantityTotal) {
    // Only calculate P&L when complete
    ComputePnL(entry);
    entry.TradeCompleted = true;
}
// If partial exit: P&L NOT calculated
```

**Impact**: P&L only calculated when `ExitFilledQuantityTotal == EntryFilledQuantityTotal`

### 5. **Never Trade Wrong Market**

**Encoded In**: MasterInstrument matching, canonical mismatch check

**Principle**: "I would rather the system stop trading entirely than ever **trade a contract whose MasterInstrument doesn't match the timetable canonical instrument**."

**Evidence**:
```csharp
// RobotEngine.ApplyTimetable
if (_masterInstrumentName != timetableCanonical) {
    // Skip directive - log STREAM_SKIPPED with CANONICAL_MISMATCH
    LogEvent(..., "STREAM_SKIPPED", reason: "CANONICAL_MISMATCH", ...);
    continue; // Skip to next directive
}
```

**Impact**: Canonical mismatch → Directive skipped → No trade → Logged loudly

### 6. **Never Hide a Failure**

**Encoded In**: Comprehensive logging, orphan fill logging, incident persistence

**Principle**: "I would rather the system stop trading entirely than ever **fail silently or hide a critical error**."

**Evidence**:
- Orphan fills logged to `data/execution_incidents/orphan_fills_YYYY-MM-DD.jsonl`
- Journal corruption triggers `EXECUTION_JOURNAL_CORRUPTION` event
- Protective failures persist incident records
- All failures trigger CRITICAL log events

**Impact**: Every failure is logged, persisted, and auditable

---

## Summary: Design Decisions

### 1. Stream Lifecycle: **HARD CONTRACT - Single-Trade Only**
- ✅ One stream = one opportunity = one trade (or no trade)
- ✅ Prevents re-entry, multiple intents, retries
- ✅ Permanently locks stream-level sufficiency

### 2. Terminal States: **Implicit, Should Be Formal**
- ✅ Current: Derivable from `CommitReason` + `TradeCompleted` + `State`
- ⚠️ Recommendation: Add `StreamTerminalState` enum for explicit classification

### 3. IntentId Reuse: **Intentional and Permanent**
- ✅ Deterministic identity (same fields = same ID)
- ✅ Hard invariant: Change any field → New ID → New trade
- ✅ Enables idempotency (prevents duplicates on restart)

### 4. P&L Aggregation: **Journals Authoritative, Aggregation Convenience**
- ✅ Current: Stream journals are authoritative
- ⚠️ Recommendation: Add read-only aggregation layer (derived from journals)

### 5. Fail-Closed Principles: **Multiple, Ranked**
1. **Never trade without stream attribution** (highest priority)
2. **Never hold unprotected position**
3. **Never double-count fill**
4. **Never calculate P&L on incomplete trade**
5. **Never trade wrong market**
6. **Never hide failure**

---

**End of Architecture Decisions Document**
