# Stream-Level Tracking Summary

## Overview

Stream-level tracking is the system's ability to **deterministically attribute every fill and calculate P&L at the stream level**. A stream represents a single trading opportunity (e.g., "ES1" for ES session 1 at 07:30 CT). This document explains how the system tracks performance per stream.

---

## 1. Stream Identity

### 1.1 What is a Stream?

**Definition**: A stream is a unique trading opportunity defined by:
- **Stream ID**: Canonical identifier (e.g., "ES1", "GC1")
- **Trading Date**: The trading day (e.g., "2025-02-01")
- **Canonical Instrument**: Base instrument (e.g., "ES", "GC")
- **Execution Instrument**: What is actually traded (e.g., "MES", "MGC")
- **Session**: Trading session (e.g., "S1", "S2")
- **Slot Time**: Time slot (e.g., "07:30" CT)

**Example**:
```
Stream: "ES1"
Trading Date: "2025-02-01"
Canonical Instrument: "ES"
Execution Instrument: "MES"
Session: "S1"
Slot Time: "07:30"
```

### 1.2 Stream Identity Immutability

**Key Property**: Stream identity is **immutable** once set:
- Stream ID is set at `StreamStateMachine` construction
- Never modified during lifecycle
- Persists for entire trade lifecycle

**Location**: `modules/robot/core/StreamStateMachine.cs:46`
```csharp
public string Stream { get; }  // Read-only property
```

---

## 2. Intent ID & Stream Attribution

### 2.1 Intent ID Composition

**Intent ID** is a hash-based identifier computed from **15 canonical fields**, including `stream`:

**Fields Included**:
1. `tradingDate`
2. `stream` ✅ **Stream included**
3. `instrument` (canonical)
4. `session`
5. `slotTimeChicago`
6. `direction`
7. `entryPrice`
8. `stopPrice`
9. `targetPrice`
10. `beTrigger`
11. ... (5 more fields)

**Location**: `modules/robot/core/Execution/ExecutionJournal.cs:52-72`
```csharp
public static string ComputeIntentId(
    string tradingDate,
    string stream,  // ✅ Stream included in hash
    string instrument,
    // ... other fields
)
```

**Result**: 16-character hex string (e.g., `"abc123def4567890"`)

### 2.2 Order Tagging

**All orders are tagged with Intent ID**:
- Entry orders: `"QTSW2:{intentId}"`
- Stop orders: `"QTSW2:{intentId}:STOP"`
- Target orders: `"QTSW2:{intentId}:TARGET"`

**Recovery Path**: From order tag → Intent ID → Intent Map → Stream

---

## 3. Fill Attribution to Streams

### 3.1 Critical Fix: Context Resolution

**Problem (Before Fix)**: Fills were recorded with empty `tradingDate` and `stream`, losing stream identity.

**Solution**: `ResolveIntentContextOrFailClosed()` helper resolves intent context before recording fills.

**Location**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`

**Flow**:
```csharp
// 1. Extract Intent ID from order tag
var intentId = RobotOrderIds.DecodeIntentId(orderTag);

// 2. Resolve intent context (fail-closed if missing)
var context = ResolveIntentContextOrFailClosed(intentId, order, utcNow);
// Returns: (tradingDate, stream, direction, contractMultiplier)

// 3. Validate all fields non-empty (fail-closed if empty)
if (string.IsNullOrWhiteSpace(context.tradingDate) || 
    string.IsNullOrWhiteSpace(context.stream)) {
    // Log orphan fill + block execution
    return;
}

// 4. Record fill with resolved context
_executionJournal.RecordEntryFill(
    intentId,
    context.tradingDate,  // ✅ Non-empty
    context.stream,        // ✅ Non-empty
    fillPrice,
    fillQuantity,          // Delta only
    utcNow,
    context.contractMultiplier,
    context.direction,
    executionInstrument,
    canonicalInstrument
);
```

### 3.2 Entry Fill Recording

**Method**: `ExecutionJournal.RecordEntryFill()`

**Key Features**:
- ✅ **Delta quantities only**: Accepts `fillQuantity` (this fill's qty), NOT cumulative
- ✅ **Cumulative tracking**: Updates `EntryFilledQuantityTotal` internally
- ✅ **Weighted average**: Calculates `EntryAvgFillPrice` from cumulative fills
- ✅ **Validation**: Rejects empty `tradingDate` or `stream` (fail-closed)
- ✅ **Idempotent**: Can be called multiple times safely

**Fields Updated**:
```csharp
entry.EntryFilledQuantityTotal += fillQuantity;  // Cumulative
entry.EntryFillNotional += fillPrice * fillQuantity;  // Cumulative notional
entry.EntryAvgFillPrice = entry.EntryFillNotional / entry.EntryFilledQuantityTotal;
entry.EntryFilledAtUtc = utcNow.ToString("o");  // First fill time
entry.Direction = direction;  // Persisted on first fill
entry.ContractMultiplier = contractMultiplier;  // Persisted on first fill
```

**Journal Key**: `"{tradingDate}_{stream}_{intentId}"`

**Example**:
```
Key: "2025-02-01_ES1_abc123def4567890"
```

### 3.3 Exit Fill Recording

**Method**: `ExecutionJournal.RecordExitFill()`

**Key Features**:
- ✅ **Delta quantities only**: Accepts `exitFillQuantity` (this fill's qty)
- ✅ **Cumulative tracking**: Updates `ExitFilledQuantityTotal`
- ✅ **Weighted average**: Calculates `ExitAvgFillPrice`
- ✅ **Trade completion detection**: Checks `ExitFilledQuantityTotal == EntryFilledQuantityTotal`
- ✅ **P&L calculation**: Only computed on completion

**Fields Updated**:
```csharp
entry.ExitFilledQuantityTotal += exitFillQuantity;  // Cumulative
entry.ExitFillNotional += exitFillPrice * exitFillQuantity;  // Cumulative
entry.ExitAvgFillPrice = entry.ExitFillNotional / entry.ExitFilledQuantityTotal;
entry.ExitOrderType = exitOrderType;  // "STOP" or "TARGET"
entry.ExitFilledAtUtc = utcNow.ToString("o");  // First exit fill time

// Trade completion check
if (entry.ExitFilledQuantityTotal == entry.EntryFilledQuantityTotal) {
    // Calculate P&L
    ComputePnL(entry);
    entry.TradeCompleted = true;
    entry.CompletedAtUtc = utcNow.ToString("o");
    entry.CompletionReason = exitOrderType;
}
```

---

## 4. P&L Calculation at Stream Level

### 4.1 Calculation Trigger

**Condition**: P&L is **only calculated** when:
```csharp
ExitFilledQuantityTotal == EntryFilledQuantityTotal
```

**If Partial Exit**: P&L is **NOT** calculated (trade not complete)

**If Overfill**: Triggers emergency (fail-closed)

### 4.2 P&L Formula

**Location**: `ExecutionJournal.RecordExitFill()` (inside completion check)

**Points Calculation**:
```csharp
decimal points;
if (entry.Direction == "Long") {
    points = entry.ExitAvgFillPrice - entry.EntryAvgFillPrice;
} else if (entry.Direction == "Short") {
    points = entry.EntryAvgFillPrice - entry.ExitAvgFillPrice;
}
```

**Dollar Calculation**:
```csharp
entry.RealizedPnLPoints = points;
entry.RealizedPnLGross = points * entry.EntryFilledQuantityTotal * entry.ContractMultiplier.Value;
entry.RealizedPnLNet = entry.RealizedPnLGross - slippageDollars - commission - fees;
```

**Example**:
```
Direction: "Long"
EntryAvgFillPrice: 5000.25
ExitAvgFillPrice: 5010.50
EntryFilledQuantityTotal: 2
ContractMultiplier: 50

Points: 5010.50 - 5000.25 = 10.25
Gross P&L: 10.25 * 2 * 50 = $1,025.00
Net P&L: $1,025.00 - slippage - commission - fees
```

### 4.3 Weighted Average Handling

**Partial Fills**: System correctly handles multiple partial fills:

**Entry Example**:
```
Fill 1: 1 contract @ $5000.00
Fill 2: 1 contract @ $5000.50

EntryFilledQuantityTotal: 2
EntryFillNotional: (5000.00 * 1) + (5000.50 * 1) = 10000.50
EntryAvgFillPrice: 10000.50 / 2 = 5000.25
```

**Exit Example**:
```
Exit Fill 1: 1 contract @ $5010.00
Exit Fill 2: 1 contract @ $5011.00

ExitFilledQuantityTotal: 2
ExitFillNotional: (5010.00 * 1) + (5011.00 * 1) = 10021.00
ExitAvgFillPrice: 10021.00 / 2 = 5010.50
```

**P&L**: Uses weighted averages, not individual fill prices.

---

## 5. ExecutionJournalEntry Structure

### 5.1 Identity Fields (Required)

```csharp
public string IntentId { get; set; }           // Hash of 15 fields
public string TradingDate { get; set; }         // ✅ Required (non-empty)
public string Stream { get; set; }              // ✅ Required (non-empty)
public string Instrument { get; set; }          // Execution instrument
```

**Validation**: Both `TradingDate` and `Stream` are validated as non-empty at persistence time (fail-closed if empty).

### 5.2 Entry Tracking Fields

```csharp
public int EntryFilledQuantityTotal { get; set; }      // Cumulative entry qty
public decimal EntryAvgFillPrice { get; set; }          // Weighted average
public decimal EntryFillNotional { get; set; }          // Sum(price * qty)
public string EntryFilledAtUtc { get; set; }           // First entry fill time
public string Direction { get; set; }                  // "Long" or "Short"
public decimal? ContractMultiplier { get; set; }       // Points → dollars
```

### 5.3 Exit Tracking Fields

```csharp
public int ExitFilledQuantityTotal { get; set; }        // Cumulative exit qty
public decimal ExitAvgFillPrice { get; set; }          // Weighted average
public decimal ExitFillNotional { get; set; }          // Sum(price * qty)
public string ExitOrderType { get; set; }             // "STOP" or "TARGET"
public string ExitFilledAtUtc { get; set; }           // First exit fill time
```

### 5.4 Completion Fields

```csharp
public bool TradeCompleted { get; set; }              // true when exit qty == entry qty
public decimal RealizedPnLGross { get; set; }         // Gross P&L in dollars
public decimal RealizedPnLNet { get; set; }          // Net P&L (gross - costs)
public decimal RealizedPnLPoints { get; set; }       // Points P&L
public string CompletedAtUtc { get; set; }           // Completion timestamp
public string CompletionReason { get; set; }        // Exit order type
```

---

## 6. Stream-Level P&L Query

### 6.1 Journal File Pattern

**Location**: `data/execution_journals/{tradingDate}_{stream}_{intentId}.json`

**Example**:
```
data/execution_journals/2025-02-01_ES1_abc123def4567890.json
```

### 6.2 Querying Stream P&L

**Method 1: Scan Journal Files**

```csharp
// Pattern: {tradingDate}_{stream}_*.json
var journalFiles = Directory.GetFiles(
    journalDir, 
    $"{tradingDate}_{stream}_*.json"
);

foreach (var file in journalFiles) {
    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(File.ReadAllText(file));
    if (entry.TradeCompleted) {
        totalPnL += entry.RealizedPnLNet;
    }
}
```

**Method 2: Use ExecutionJournal Methods**

```csharp
// Check if stream had fills
bool hasFills = _executionJournal.HasEntryFillForStream(stream, tradingDate);

// Get all entries for stream (requires scanning)
// (No direct method yet - use file pattern scan)
```

### 6.3 Stream-Level Aggregation

**Current State**: No built-in aggregation method (future enhancement)

**Manual Aggregation**:
1. Scan journal files matching `{tradingDate}_{stream}_*.json`
2. Filter entries where `TradeCompleted == true`
3. Sum `RealizedPnLNet` for completed trades
4. Count wins/losses based on `RealizedPnLNet` sign

**Example Aggregation**:
```csharp
var streamPnL = new {
    TotalPnL = 0m,
    WinCount = 0,
    LossCount = 0,
    CompletedTrades = 0
};

foreach (var entry in completedEntries) {
    streamPnL.TotalPnL += entry.RealizedPnLNet;
    streamPnL.CompletedTrades++;
    if (entry.RealizedPnLNet > 0) streamPnL.WinCount++;
    else if (entry.RealizedPnLNet < 0) streamPnL.LossCount++;
}
```

---

## 7. Orphan Fill Handling

### 7.1 Orphan Detection

**Definition**: A fill that cannot be attributed to a stream because:
- Intent not found in `_intentMap`
- `tradingDate` is empty/whitespace
- `stream` is empty/whitespace
- `direction` is missing
- `contractMultiplier` unavailable

**Location**: `NinjaTraderSimAdapter.ResolveIntentContextOrFailClosed()`

### 7.2 Orphan Logging

**Location**: `data/execution_incidents/orphan_fills_YYYY-MM-DD.jsonl`

**Format**:
```json
{
  "event_type": "ORPHAN_FILL",
  "timestamp_utc": "2025-02-01T14:30:00Z",
  "intent_id": "abc123def456",
  "tag": "QTSW2:abc123def456",
  "order_type": "ENTRY",
  "instrument": "MGC",
  "fill_price": 2500.50,
  "fill_quantity": 1,
  "stream": "GC1",
  "reason": "INTENT_NOT_FOUND",
  "action_taken": "EXECUTION_BLOCKED"
}
```

**Fail-Closed Actions**:
- ✅ Log CRITICAL event
- ✅ Write orphan record to JSONL
- ✅ Stand down stream (if known)
- ✅ Block instrument execution
- ✅ **DO NOT call journal with empty strings**

---

## 8. Key Invariants

### 8.1 Enforced Invariants

1. **Stream Identity Required**: Every fill must have non-empty `tradingDate` and `stream`
   - **Enforcement**: Validation in `RecordEntryFill()` and `RecordExitFill()`
   - **Violation**: Fail-closed (fill not recorded)

2. **Delta Quantities Only**: Journal methods accept delta quantities, NOT cumulative
   - **Enforcement**: Method signatures (`fillQuantity`, not `filledTotal`)
   - **Violation**: Compiler error (type mismatch)

3. **Trade Completion Gating**: P&L only calculated when `ExitFilledQuantityTotal == EntryFilledQuantityTotal`
   - **Enforcement**: Conditional check in `RecordExitFill()`
   - **Violation**: P&L not calculated (trade incomplete)

4. **Direction Consistency**: Direction cannot change mid-trade
   - **Enforcement**: Validation in `RecordEntryFill()`
   - **Violation**: Fail-closed (fill not recorded)

5. **Contract Multiplier Consistency**: Multiplier cannot change mid-trade
   - **Enforcement**: Validation in `RecordEntryFill()`
   - **Violation**: Fail-closed (fill not recorded)

### 8.2 Fail-Closed Principles

**Missing Context**:
- Orphan fill → Block execution
- Empty `tradingDate` → Block execution
- Empty `stream` → Block execution

**Journal Corruption**:
- Corrupted journal file → Stand down stream
- Prevent duplicate submissions

**Overfill**:
- `ExitFilledQuantityTotal > EntryFilledQuantityTotal` → Emergency handler
- Stand down stream

---

## 9. Data Flow: Fill → Stream Attribution

### 9.1 Complete Flow

```
NinjaTrader Execution Event
    ↓
HandleExecutionUpdateReal()
    ↓
Extract Intent ID from order tag
    ↓
ResolveIntentContextOrFailClosed()
    ├─ Lookup intent in _intentMap
    ├─ Extract: tradingDate, stream, direction, contractMultiplier
    └─ Validate all fields non-empty
    ↓ (if validation passes)
Classify Fill Type
    ├─ Entry fill → RecordEntryFill()
    └─ Exit fill → RecordExitFill()
    ↓
RecordEntryFill() / RecordExitFill()
    ├─ Validate tradingDate non-empty (fail-closed if empty)
    ├─ Validate stream non-empty (fail-closed if empty)
    ├─ Update cumulative quantities (delta-based)
    ├─ Update weighted averages
    └─ Check trade completion (exit fills only)
    ↓ (if trade completed)
ComputePnL()
    ├─ Calculate points P&L
    ├─ Calculate dollar P&L
    └─ Mark TradeCompleted = true
    ↓
Persist to Journal
    └─ File: {tradingDate}_{stream}_{intentId}.json
```

### 9.2 Stream Attribution Guarantee

**Guarantee**: Every fill recorded in the journal has:
- ✅ Non-empty `TradingDate`
- ✅ Non-empty `Stream`
- ✅ Valid `IntentId`
- ✅ Valid `Direction`
- ✅ Valid `ContractMultiplier`

**If any field is missing**: Fill is **not recorded** (fail-closed), orphan logged.

---

## 10. Recovery & Reconstruction

### 10.1 Journal Persistence

**Format**: JSON files per `(tradingDate, stream, intentId)`

**Location**: `data/execution_journals/{tradingDate}_{stream}_{intentId}.json`

**Content**: Complete `ExecutionJournalEntry` with all fields

### 10.2 Stream State Recovery

**On Restart**:
1. Scan journal files matching `{tradingDate}_{stream}_*.json`
2. Load entries into cache
3. Rebuild `_intentMap` from journal entries
4. Restore stream state from journal

**Idempotency**: `IsIntentSubmitted()` checks journal before allowing submission

### 10.3 Stream-Level P&L Reconstruction

**Query Pattern**:
```csharp
// Get all completed trades for stream ES1 on 2025-02-01
var pattern = "2025-02-01_ES1_*.json";
var files = Directory.GetFiles(journalDir, pattern);

var streamPnL = 0m;
foreach (var file in files) {
    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(File.ReadAllText(file));
    if (entry.TradeCompleted) {
        streamPnL += entry.RealizedPnLNet;
    }
}
```

---

## 11. Summary

### 11.1 Stream-Level Tracking Capabilities

✅ **Stream Identity**: Explicit, immutable, persisted  
✅ **Fill Attribution**: Every fill attributed to stream via intent context resolution  
✅ **P&L Calculation**: Deterministic P&L from weighted average entry/exit prices  
✅ **Trade Completion**: Explicit completion tracking (`ExitFilledQuantityTotal == EntryFilledQuantityTotal`)  
✅ **Partial Fill Handling**: Weighted averages for multiple partial fills  
✅ **Fail-Closed Safety**: Missing context → Block execution, orphan logged  
✅ **Idempotency**: Journal prevents double-submission  
✅ **Recovery**: Stream state reconstructible from journal files  

### 11.2 Key Files

- **ExecutionJournal.cs**: Journal persistence, P&L calculation
- **ExecutionJournalEntry.cs**: Journal entry model (defined within ExecutionJournal.cs)
- **NinjaTraderSimAdapter.NT.cs**: Fill handling, context resolution
- **StreamStateMachine.cs**: Stream lifecycle management

### 11.3 Critical Methods

- `RecordEntryFill()`: Record entry fills with stream attribution
- `RecordExitFill()`: Record exit fills, calculate P&L on completion
- `ResolveIntentContextOrFailClosed()`: Resolve intent context (fail-closed if missing)
- `ComputeIntentId()`: Generate intent ID from canonical fields (includes stream)

---

**End of Stream-Level Tracking Summary**
