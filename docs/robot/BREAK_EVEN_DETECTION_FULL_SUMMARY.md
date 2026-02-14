# Break-Even Detection — Full Summary

**Last updated:** 2026-02-13  
**Scope:** RobotCore_For_NinjaTrader (live NinjaTrader robot)

---

## 1. What It Does

Break-even (BE) detection monitors filled positions and automatically moves the protective stop loss to break-even when price reaches **65% of the target distance** from entry. The BE stop is placed at **1 tick before the breakout level** (entry price), protecting the trade from loss while leaving room to hit the target.

---

## 2. End-to-End Flow

```
Entry Detected → BE Trigger Calculated → Intent Registered → Entry Fills → Protective Orders Placed
                                                                              ↓
                                    OnBarUpdate (BIP 1 or BIP 0) ← every 5s when in position
                                                                              ↓
                                    RunBreakEvenCheck() → Closes[1][0] or Closes[0][0]
                                                                              ↓
                                    CheckBreakEvenTriggersTickBased(price, utcNow)
                                                                              ↓
                                    GetActiveIntentsForBEMonitoring(executionInstrument)
                                                                              ↓
                                    price >= beTrigger (Long) or price <= beTrigger (Short)?
                                                                              ↓
                                    ModifyStopToBreakEven(intentId, instrument, beStopPrice)
                                                                              ↓
                                    NinjaTrader account.Change(stopOrder) — stop moved to BE
```

---

## 3. Key Components

### 3.1 BE Trigger Calculation

**Location:** `StreamStateMachine.cs` → `ComputeAndLogProtectiveOrders()` (lines ~5288–5360)

**When:** At entry detection, when breakout level is determined.

**Formula:**
```csharp
beTriggerPts = _baseTarget * 0.65m;  // 65% of target distance
beTriggerPrice = direction == "Long" ? entryPrice + beTriggerPts : entryPrice - beTriggerPts;
beStopPrice    = direction == "Long" ? entryPrice - _tickSize    : entryPrice + _tickSize;
```

**Example (Long):**
- Entry: 100.00, Target: 4.00 pts  
- BE Trigger: 100.00 + 2.60 = **102.60**  
- BE Stop: 100.00 − 0.25 = **99.75** (1 tick below breakout)

**Stored:** `_intendedBeTrigger` → passed into `Intent` via `RegisterIntent` / `ComputeProtectivesFromLockSnapshot`.

---

### 3.2 Intent Registration

**Location:** `StreamStateMachine.cs` → `RegisterIntent()` / breakout entry path

**Intent fields used for BE:**
- `BeTrigger` — price level that triggers BE modification
- `EntryPrice` — breakout level (used for BE stop, not actual fill)
- `Direction` — "Long" or "Short"
- `ExecutionInstrument` — e.g. MES, MGC (for instrument filtering)

**Source of truth for entry fill:** `ExecutionJournal` — `GetActiveIntentsForBEMonitoring` checks `journalEntry.EntryFilled`.

---

### 3.3 BE Detection Path (Strategy Layer)

**Location:** `RobotSimStrategy.cs` → `OnBarUpdate()` → `RunBreakEvenCheck()` → `CheckBreakEvenTriggersTickBased()`

**When BE runs:**
1. **BIP 1 (1-second bars):** When `BarsArray.Length >= 2`, in position, Realtime. Throttled to **every 5 seconds**.
2. **BIP 0 fallback:** When no secondary series, or BIP 1 stale (no update in 10s). Also throttled to **every 5 seconds**.

**Price source:** `Closes[priceBarsIndex][0]` — i.e. **bar close**, not true tick:
- With 1-second series: last close of current 1-second bar
- Fallback: last close of primary bar (e.g. 5-min)

**Important:** The method is named "TickBased" but it uses **bar close prices**. True tick-based detection would require `OnMarketData(MarketDataType.Last)`.

---

### 3.4 Active Intent Filtering

**Location:** `NinjaTraderSimAdapter.cs` → `GetActiveIntentsForBEMonitoring(string? executionInstrument)`

**Filter criteria (all must pass):**
1. `executionInstrument` matches `intent.ExecutionInstrument` (e.g. MES strategy → MES intents only)
2. `intent.BeTrigger`, `EntryPrice`, `Direction` are set
3. `ExecutionJournal.GetEntry()` shows `EntryFilled == true`
4. `!IsBEModified(intentId, tradingDate, stream)` — not already moved to BE

**Instrument resolution:** `GetExecutionInstrumentForBE()` parses `Instrument.FullName` (e.g. "MGC 03-26" → "MGC") so it matches `Intent.ExecutionInstrument`. Using `MasterInstrument.Name` (e.g. "GC") would incorrectly filter out MGC intents.

---

### 3.5 Stop Order Modification

**Location:** `NinjaTraderSimAdapter.NT.cs` → `ModifyStopToBreakEvenReal()`

**Flow:**
1. Find stop order by tag `QTSW2:{intentId}:STOP`
2. Verify order is Working or Accepted
3. Set `stopOrder.StopPrice = beStopPrice`
4. Call `account.Change(new[] { stopOrder })`
5. Mark BE modified in execution journal

**Throttling:** BE modify attempts are throttled to **200 ms** per intent (`BE_MODIFY_ATTEMPT_INTERVAL_MS`) to avoid hammering `account.Orders` and `Change()`.

---

## 4. Integration With Rest of Code

| Component | Interaction |
|-----------|-------------|
| **StreamStateMachine** | Computes BE trigger, passes to Intent; engine/adapter agnostic |
| **NinjaTraderSimAdapter** | Holds intents, filters by instrument, modifies stop via NT API |
| **ExecutionJournal** | Tracks entry fill and `BEModified`; source of truth for BE eligibility |
| **RobotSimStrategy** | Runs BE check from OnBarUpdate; one instance per chart/instrument |
| **Engine** | No direct BE logic; logs events (BE_TRIGGER_REACHED, etc.) |

---

## 5. Potential Issues & Improvements

### 5.1 Bar-Based vs Tick-Based (Current Limitation)

**Issue:** BE detection uses bar close (`Closes[1][0]` or `Closes[0][0]`), not live ticks. If price crosses the BE trigger and reverses within the same bar, the trigger may be missed until the next bar close.

**Impact:** With 1-second bars, worst-case delay ≈ 1 second. With primary-bar fallback (e.g. 5-min), delay can be several minutes.

**Improvement:** Override `OnMarketData()` and use `MarketDataType.Last` for true tick-based detection. NinjaTrader calls `OnMarketData` for each Last tick. This would require:
- Ensuring `OnMarketData` is called for the strategy’s instrument
- Keeping the same throttling/caching to avoid UI lag

---

### 5.2 5-Second Throttle

**Issue:** BE check runs at most every 5 seconds when in position. Price could cross BE trigger and reverse within that window.

**Rationale:** Throttle was added to reduce chart lag (multiple strategies × frequent checks).

**Improvement:** Consider a shorter throttle (e.g. 1–2 s) when only one strategy has a position, or use `OnMarketData` with a lightweight check.

---

### 5.3 1-Second Bars Not Available

**Issue:** Some instruments (e.g. MCL, M2K) may not have 1-second bars from the data feed. Fallback uses primary bar close, which can be much slower (e.g. 5-min bars).

**Mitigation:** `bip1StaleSeconds = 10` detects when BIP 1 hasn’t updated and switches to BIP 0.

**Improvement:** Log when fallback is used so operators know BE is slower for that instrument.

---

### 5.4 BE_PATH_ACTIVE Uses Unfiltered Count

**Issue:** `BE_PATH_ACTIVE` logs `GetActiveIntentsForBEMonitoring().Count` **without** the execution instrument filter. That returns total intents across all instruments, not just for this chart.

**Location:** Lines 1325, 1359 in `RobotSimStrategy.cs`

**Fix:** Pass `GetExecutionInstrumentForBE()` into `GetActiveIntentsForBEMonitoring()` for the diagnostic count so the logged value matches what this strategy actually evaluates.

---

### 5.5 Intent Cache Staleness

**Issue:** `_cachedActiveIntentsForBE` is refreshed every 200 ms. If an intent is filled or BE-modified in another path, the cache can be stale for up to 200 ms.

**Impact:** Low; 200 ms is short. Idempotency in `ModifyStopToBreakEven` and `IsBEModified` prevents double-modification.

---

### 5.6 BE_TRIGGER_TIMEOUT (5 Seconds)

**Issue:** If BE trigger is reached but modification fails for 5+ seconds, `BE_TRIGGER_TIMEOUT_ERROR` is logged. No automatic retry beyond the normal tick/bar loop.

**Current behavior:** Retries occur on subsequent BE checks (every 5 s) until success or position is closed.

---

### 5.7 Tick Size Fallback

**Issue:** If `Instrument.MasterInstrument.TickSize` is 0 or invalid, code falls back to a hardcoded tick size. `BE_TICK_SIZE_FALLBACK_WARNING` is logged.

**Improvement:** Validate tick size at strategy init and fail fast if invalid for the instrument.

---

## 6. Event Types (Logging)

| Event | When |
|-------|------|
| `BE_PATH_ACTIVE` | BE check path ran (rate-limited 60s) |
| `BE_EVALUATION_TICK` | BE evaluation ran (rate-limited 1s) |
| `BE_TRIGGER_REACHED` | Price reached BE trigger, stop modified successfully |
| `BE_TRIGGER_RETRY_NEEDED` | Trigger reached but stop not found yet (race) |
| `BE_TRIGGER_FAILED` | Modification failed (non-retryable) |
| `BE_TRIGGER_TIMEOUT_ERROR` | Trigger reached but no success after 5 s |
| `BE_DETECTION_INVALID_DIRECTION` | Intent has invalid Direction |
| `BE_TICK_SIZE_FALLBACK_WARNING` | Tick size fallback used |
| `CHECK_BE_TRIGGERS_EXCEPTION` | Exception in CheckBreakEvenTriggersTickBased |
| `BE_CHECK_SLOW` | BE check exceeded 10 ms |

---

## 7. Configuration Constants

| Constant | Value | Purpose |
|----------|-------|---------|
| `BE_SCAN_THROTTLE_MS` | 200 | Min ms between intent scans |
| `BE_INTENTS_CACHE_MS` | 200 | Cache TTL for active intents |
| `BE_MODIFY_ATTEMPT_INTERVAL_MS` | 200 | Min ms between modify attempts per intent |
| `BE_TRIGGER_TIMEOUT_SECONDS` | 5 | Error if trigger reached but no modify in 5 s |
| `BE_PATH_ACTIVE_RATE_LIMIT_SECONDS` | 60 | Rate limit for BE_PATH_ACTIVE log |
| BIP 1 throttle | 5 s | Min interval between BE checks (BIP 1 path) |
| BIP 0 fallback throttle | 5 s | Same for primary-bar fallback |

---

## 8. Historical Bug (Fixed)

**Root cause (BE_INVESTIGATION_REPORT.md):** `GetActiveIntentsForBEMonitoring()` returned intents for all instruments, while each strategy only receives ticks/bars for its own chart. MES tick price was compared to YM/GC/NG intents → wrong price scales, false or missed triggers.

**Fix:** Added `executionInstrument` parameter and filter by `intent.ExecutionInstrument`. Strategy passes `GetExecutionInstrumentForBE()` (from `Instrument.FullName`).

---

## 9. Summary

| Aspect | Status |
|--------|--------|
| BE trigger calculation | ✅ Correct (65% of target) |
| BE stop placement | ✅ 1 tick before breakout level |
| Instrument filtering | ✅ Fixed (execution instrument match) |
| Idempotency | ✅ IsBEModified prevents double-modify |
| Retry on race | ✅ Retries when stop not found yet |
| Price source | ⚠️ Bar close, not true tick |
| Throttle | ⚠️ 5 s between checks; 200 ms between modify attempts |
| 1-second bar fallback | ✅ BIP 0 when BIP 1 stale |
| BE_PATH_ACTIVE count | ⚠️ Uses unfiltered count (cosmetic) |

The system is functionally correct and handles multi-instrument setups. The main improvement opportunity is moving to true tick-based detection via `OnMarketData` for faster and more reliable BE triggering.
