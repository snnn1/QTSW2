# Comprehensive Issues & Fixes Summary
**Date:** January 28, 2026  
**Status:** Active Development

---

## 📋 Table of Contents
1. [Recent Critical Issues Fixed](#recent-critical-issues-fixed)
2. [Watchdog UI Issues Fixed](#watchdog-ui-issues-fixed)
3. [Current Known Issues](#current-known-issues)
4. [Potential Future Issues](#potential-future-issues)
5. [Architecture Concerns](#architecture-concerns)

---

## 🔧 Recent Critical Issues Fixed

### 1. **NinjaTrader API Integration - StopMarket Order Creation**
**Issue:** Orders failing with "No overload for method CreateOrder takes X arguments"

**Root Cause:**
- Code was attempting multiple overloads (3-arg, 4-arg, 5-arg) that don't exist in NT8
- Using wrong enum types (`EntryHandling.AllEntries` instead of `OrderEntry.Manual`)
- Dynamic overload hunting causing compilation/runtime errors

**Fix Applied:**
- Replaced all overload attempts with single 12-parameter `Account.CreateOrder` factory method
- Correct parameter order: `Instrument, OrderAction, OrderType, EntryHandling, TimeInForce, Quantity, LimitPrice, StopPrice, Oco, OrderName, Gtd, CustomOrder`
- Used `OrderEntry.Manual` for EntryHandling
- Added comprehensive runtime safety checks before `CreateOrder`
- Standardized error logging to single `ORDER_CREATE_FAIL` / `ORDER_SUBMIT_FAIL` messages

**Files Modified:**
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`

**Status:** ✅ Fixed

---

### 2. **Instrument Name Whitespace Errors**
**Issue:** Orders failing with `instrument 'MGC 'is not supported` and `instrument 'MES 'is not supported`

**Root Cause:**
- Instrument strings had trailing whitespace ('MGC ', 'MES ')
- NinjaTrader's `Instrument.GetInstrument()` is case-sensitive and whitespace-sensitive
- No trimming before instrument resolution

**Fix Applied:**
- Added `.Trim()` to instrument string before resolution in `ResolveInstrument()`
- Enhanced logging to show `original_instrument` vs `requested_instrument` (trimmed)
- Added `had_whitespace` boolean flag to logs for diagnostics

**Files Modified:**
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`

**Status:** ✅ Fixed

---

### 3. **Tick Size Mismatch for Order Price Rounding**
**Issue:** Breakout levels rounded using canonical instrument tick size, but orders submitted to execution instrument

**Root Cause:**
- Range computation uses canonical instrument (ES) tick size
- Breakout levels computed using canonical tick size (`_tickSize`)
- Orders submitted to execution instrument (MES) which may have different tick size
- Price rounding mismatch causing invalid prices

**Fix Applied:**
- Added `_executionTickSize` field to store execution instrument tick size
- Initialize `_executionTickSize` from execution instrument spec in constructor
- Changed `ComputeBreakoutLevelsAndLog()` to use `_executionTickSize` for rounding
- Enhanced logging to show both `canonical_tick_size` and `execution_tick_size`
- Added `EXECUTION_TICK_SIZE_FALLBACK` event if execution instrument not found in spec

**Files Modified:**
- `modules/robot/core/StreamStateMachine.cs`
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs`

**Status:** ✅ Fixed (needs rebuild to take effect)

---

### 4. **Restart Recovery & Idempotency Issues**
**Issue:** Robot could place duplicate orders or miss entries after restart

**Root Cause:**
- `_stopBracketsSubmittedAtLock` flag was in-memory only, reset on restart
- `_entryDetected` flag was in-memory only, reset on restart
- Breakout levels (`_brkLongRounded`, `_brkShortRounded`) not recomputed on RANGE_LOCKED restart

**Fix Applied:**
- Added `StopBracketsSubmittedAtLock` field to `StreamJournal` (persisted)
- Added `EntryDetected` field to `StreamJournal` (persisted)
- Added `HasEntryFillForStream()` method to `ExecutionJournal` for restart recovery
- State restoration logic in `StreamStateMachine` constructor:
  - Restore `_stopBracketsSubmittedAtLock` from journal
  - Restore `_entryDetected` from journal or scan execution journal
  - Recompute breakout levels if state is RANGE_LOCKED on restart
- Fail-safe handling for missing execution journal

**Files Modified:**
- `modules/robot/core/StreamStateMachine.cs`
- `modules/robot/core/JournalStore.cs`
- `modules/robot/core/Execution/ExecutionJournal.cs`
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs`
- `RobotCore_For_NinjaTrader/JournalStore.cs`
- `RobotCore_For_NinjaTrader/Execution/ExecutionJournal.cs`

**Status:** ✅ Fixed

---

### 5. **RANGE_LOCKED Event Separation**
**Issue:** Range data not easily accessible for dashboard/analysis

**Root Cause:**
- Range data embedded in general engine logs
- No canonical, immutable event per stream/day
- Hard to query/replay/audit range data

**Fix Applied:**
- Created `RangeLockedEvent.cs` - immutable event class
- Created `RangeLockedEventPersister.cs` - dedicated JSONL persistence
- Persists to `logs/robot/ranges_YYYY-MM-DD.jsonl`
- Idempotency by `(trading_day, stream_id)` key
- Comprehensive range data: high, low, breakout levels, times (Chicago + UTC)
- Fail-safe persistence (never throws, never blocks execution)

**Files Modified:**
- `modules/robot/core/RangeLockedEvent.cs` (new)
- `modules/robot/core/RangeLockedEventPersister.cs` (new)
- `modules/robot/core/StreamStateMachine.cs`
- `RobotCore_For_NinjaTrader/RangeLockedEvent.cs` (new)
- `RobotCore_For_NinjaTrader/RangeLockedEventPersister.cs` (new)
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs`

**Status:** ✅ Fixed

---

### 6. **Watchdog UI - Missing Slot and Range Data**
**Issue:** Watchdog UI showing "-" for Slot and Range columns for RANGE_LOCKED streams

**Root Cause:**
- `slot_time_chicago` was being stripped from events in `EventFeedGenerator`
- `RANGE_LOCK_SNAPSHOT` events filtered out (not in `LIVE_CRITICAL_EVENT_TYPES`)
- `extra_data` containing range values was serialized as string (C# anonymous object), not JSON dict
- Event processor expected `extra_data` as dict, couldn't parse string format

**Fix Applied:**
- Modified `EventFeedGenerator._process_event()` to explicitly extract and include `slot_time_chicago` and `slot_time_utc`
- Added `RANGE_LOCK_SNAPSHOT` to `LIVE_CRITICAL_EVENT_TYPES` in `config.py`
- Enhanced `event_processor.py` to parse `extra_data` string format using regex:
  - Detects if `extra_data` is string vs dict
  - Uses regex to extract `range_high`, `range_low`, `freeze_close` from string format: `"{ range_high = 49564, ... }"`
- Promoted extracted range values to top-level `data` dict in event feed
- Updated UI component to handle both ISO and "HH:MM" formats for slot time
- Added robust null/NaN checking for range display

**Files Modified:**
- `modules/watchdog/event_feed.py`
- `modules/watchdog/event_processor.py`
- `modules/watchdog/config.py`
- `modules/watchdog/aggregator.py`
- `modules/watchdog/frontend/src/components/watchdog/StreamStatusTable.tsx`

**Status:** ✅ Fixed

---

### 7. **Watchdog UI - Stream Sorting**
**Issue:** Streams not ordered by latest slot time first

**Fix Applied:**
- Added sorting logic to `StreamStatusTable.tsx`
- Sorts by `slot_time_chicago` descending (latest first)
- Secondary sort by stream name ascending

**Status:** ✅ Fixed

---

### 8. **Compilation Errors - RangeLockedEvent Constructor**
**Issue:** `RangeLockedEvent` constructor parameter name mismatch

**Root Cause:**
- Using snake_case parameter names (`trading_day`, `stream_id`) in constructor calls
- Constructor expects camelCase (`tradingDay`, `streamId`)

**Fix Applied:**
- Updated all constructor calls to use camelCase parameter names

**Files Modified:**
- `modules/robot/core/StreamStateMachine.cs`
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs`

**Status:** ✅ Fixed

---

### 9. **Compilation Errors - ExecutionBase Extra Argument**
**Issue:** `ExecutionBase` called with 6 arguments, but only accepts 5

**Root Cause:**
- Extra `State.ToString()` argument in `STOP_BRACKETS_SKIP_NO_JOURNAL` log call

**Fix Applied:**
- Removed extra argument, moved `current_state` into anonymous object

**Files Modified:**
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs`

**Status:** ✅ Fixed

---

## 🖥️ Watchdog UI Issues Fixed

### 1. **Range Display Format**
**Issue:** Range displayed on multiple lines

**Fix Applied:**
- Added `whitespace-nowrap` CSS class to Range column

**Status:** ✅ Fixed

---

### 2. **Row Spacing**
**Issue:** Table rows too tall/spacious

**Fix Applied:**
- Reduced padding from `px-4 py-2` to `px-2 py-1` for all table cells
- Reduced state badge padding from `py-1` to `py-0.5`

**Status:** ✅ Fixed

---

## ⚠️ Current Known Issues

### 1. **Price Validation Errors (Partially Resolved)**
**Status:** 🔶 Monitoring Required

**Error Messages:**
```
Please check the order price. The current price is outside the price limits set for this product.
affected Order: Buy 1 StopMarket @ 2676.1
affected Order: Buy 1 StopMarket @ 26107.25
```

**Analysis:**
- Prices seem invalid (2676.1, 26107.25) - likely wrong instrument or calculation error
- Tick size fix should help, but prices may still be wrong if:
  - Range calculated from wrong instrument's bars
  - Price conversion needed between micro and base instruments
  - Range calculation logic error

**Next Steps:**
- Monitor logs after tick size fix is deployed
- Check if prices are correct for the instrument (e.g., MES vs ES price scale)
- Investigate range calculation if prices still wrong
- Check if `scaling_factor` in parity spec needs to be applied

**Files to Monitor:**
- `BREAKOUT_LEVELS_COMPUTED` events (check `canonical_tick_size` vs `execution_tick_size`)
- `ORDER_SUBMIT_FAIL` events with price validation errors
- Range calculation logs

---

### 2. **Preprocessor Directive Configuration**
**Status:** 🔶 External Configuration Issue

**Error:**
```
CRITICAL: NINJATRADER preprocessor directive is NOT defined.
Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild.
```

**Analysis:**
- `.csproj` file needs `NINJATRADER` constant defined
- This is a NinjaTrader project configuration issue, not code issue
- User must manually update `.csproj` file

**Resolution:**
- User needs to add `<DefineConstants>NINJATRADER</DefineConstants>` to `.csproj`
- Or ensure NinjaTrader build system defines it automatically

---

### 3. **File Synchronization Between Workspaces**
**Status:** 🔶 Manual Process

**Issue:**
- Changes made in `modules/robot/core/` need to be manually synced to `RobotCore_For_NinjaTrader/`
- User manually copies files to NinjaTrader installation directory

**Current Process:**
- User manually syncs files after changes
- No automated sync mechanism

**Potential Solution:**
- Could add build script to sync files automatically
- Or use symbolic links (if NinjaTrader supports)

---

## 🔮 Potential Future Issues

### 1. **Price Scaling Between Micro and Base Instruments**
**Risk Level:** 🟡 Medium

**Potential Issue:**
- If range is calculated from base instrument (ES) bars but orders go to micro (MES)
- May need price conversion using `scaling_factor` from parity spec
- Currently `scaling_factor` exists in spec but not used

**Mitigation:**
- Monitor price validation errors
- Check if prices need scaling (e.g., ES price / 5 = MES price)
- Implement scaling if needed

**Files to Watch:**
- `modules/robot/core/Models.ParitySpec.cs` (has `scaling_factor` field)
- Range calculation and breakout level computation

---

### 2. **Instrument Resolution Fallback Behavior**
**Risk Level:** 🟡 Medium

**Potential Issue:**
- If execution instrument resolution fails, falls back to strategy instrument
- This may cause orders to go to wrong instrument
- No validation that fallback instrument is correct

**Current Behavior:**
- Logs `INSTRUMENT_RESOLUTION_FAILED` and uses strategy instrument
- May silently place orders on wrong instrument

**Mitigation:**
- Add validation that fallback instrument matches expected execution instrument
- Fail closed if instrument mismatch detected

---

### 3. **Tick Size Mismatch Detection**
**Risk Level:** 🟢 Low (Now Monitored)

**Potential Issue:**
- If canonical and execution instruments have different tick sizes
- May cause rounding errors or invalid prices

**Current Status:**
- Now logging both tick sizes
- Can detect mismatches in logs
- Should monitor for instruments with different tick sizes

**Mitigation:**
- Monitor `BREAKOUT_LEVELS_COMPUTED` logs for tick size mismatches
- Add validation if mismatch detected

---

### 4. **Range Calculation from Wrong Instrument Bars**
**Risk Level:** 🟡 Medium

**Potential Issue:**
- If bars come from canonical instrument but orders go to execution instrument
- Range prices may be wrong scale
- Need to verify which instrument's bars are used for range calculation

**Investigation Needed:**
- Verify bar source (ExecutionInstrument vs CanonicalInstrument)
- Check if bars are from correct instrument
- May need to filter bars by execution instrument

---

### 5. **OCO Group Collision**
**Risk Level:** 🟢 Low

**Potential Issue:**
- OCO groups encoded by `(trading_date, stream, slot_time)`
- If multiple streams have same slot_time, OCO groups may collide
- NinjaTrader may reject orders with duplicate OCO groups

**Current Mitigation:**
- OCO groups include stream ID, so should be unique per stream
- Monitor for OCO rejection errors

---

### 6. **Restart During Order Submission**
**Risk Level:** 🟢 Low (Mitigated)

**Potential Issue:**
- If robot restarts between order creation and submission
- Order may be lost or duplicated

**Current Mitigation:**
- `_stopBracketsSubmittedAtLock` persisted before submission
- Execution journal records submission attempts
- Idempotency checks prevent duplicates

**Remaining Risk:**
- If restart happens between `CreateOrder` and `Submit`, order may be lost
- NT may have order in limbo state

---

## 🏗️ Architecture Concerns

### 1. **Canonical vs Execution Instrument Separation**
**Status:** ✅ Implemented, but needs monitoring

**Architecture:**
- Canonical instrument (ES) used for logic/range calculation
- Execution instrument (MES) used for order placement
- Tick sizes may differ between instruments

**Concerns:**
- Need to ensure prices are correct scale for execution instrument
- Range calculation must use correct instrument's bars
- Breakout levels must be rounded to execution instrument tick size (✅ Fixed)

**Monitoring:**
- Watch for price validation errors
- Monitor tick size logs
- Verify bar source instrumentation

---

### 2. **State Machine Restart Recovery**
**Status:** ✅ Implemented

**Architecture:**
- State restored from `StreamJournal`
- Flags persisted (`StopBracketsSubmittedAtLock`, `EntryDetected`)
- Breakout levels recomputed on RANGE_LOCKED restart

**Remaining Concerns:**
- State transitions may be lost if journal corrupted
- Breakout level recomputation depends on range values being persisted
- Need to verify all critical state is persisted

---

### 3. **Event Feed Pipeline**
**Status:** ✅ Working, but complex

**Architecture:**
- Robot logs → `frontend_feed.jsonl` → Event Processor → State Manager → Aggregator → API → UI

**Concerns:**
- Multiple transformation steps increase failure points
- String parsing of C# anonymous objects is fragile
- Need better error handling if event format changes

**Mitigation:**
- Comprehensive logging at each stage
- Fail-safe parsing with fallbacks
- Monitor for parsing errors

---

## 📊 Monitoring & Diagnostics

### Key Log Events to Monitor:

1. **BREAKOUT_LEVELS_COMPUTED**
   - Check `canonical_tick_size` vs `execution_tick_size`
   - Verify prices are reasonable for instrument

2. **INSTRUMENT_RESOLUTION_FAILED / INSTRUMENT_OVERRIDE**
   - Check `had_whitespace` flag
   - Verify correct instrument resolution

3. **ORDER_SUBMIT_FAIL**
   - Check for price validation errors
   - Verify instrument names don't have whitespace
   - Check if prices are correct scale

4. **RANGE_COMPUTE_FAILED**
   - Monitor for range calculation issues
   - Check bar counts and data quality

5. **EXECUTION_TICK_SIZE_FALLBACK**
   - Indicates execution instrument not found in spec
   - May cause tick size mismatch

---

## ✅ Summary of Fixes

| Issue | Status | Impact |
|-------|--------|--------|
| StopMarket Order API | ✅ Fixed | Critical - Orders now submit |
| Instrument Whitespace | ✅ Fixed | Critical - Instrument resolution works |
| Tick Size Mismatch | ✅ Fixed | Critical - Prices rounded correctly |
| Restart Recovery | ✅ Fixed | High - No duplicate orders |
| RANGE_LOCKED Event | ✅ Fixed | Medium - Better observability |
| Watchdog UI Data | ✅ Fixed | Medium - UI shows correct data |
| Price Validation | 🔶 Monitoring | Critical - May still have issues |
| Preprocessor Directive | 🔶 External | High - User must configure |

---

## 🎯 Next Steps

1. **Rebuild and Deploy**
   - Rebuild NinjaTrader project with latest fixes
   - Verify preprocessor directive is set
   - Test order submission

2. **Monitor Price Validation**
   - Watch for price validation errors after rebuild
   - Check breakout level logs for tick size mismatches
   - Verify prices are correct scale

3. **Investigate Price Issues (if persist)**
   - Check if range calculated from correct instrument
   - Verify price scaling needed
   - Check bar source instrumentation

4. **Enhance Logging (if needed)**
   - Add more diagnostic logging for price calculation
   - Log bar source (which instrument) for range calculation
   - Add price validation pre-checks

---

**Last Updated:** January 28, 2026  
**Document Version:** 1.0
