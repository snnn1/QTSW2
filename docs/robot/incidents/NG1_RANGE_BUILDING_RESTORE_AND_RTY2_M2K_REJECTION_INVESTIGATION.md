# Full Investigation: NG1 Post-Restart Recovery & RTY2/M2K Broker Rejection

**Date:** 2026-03-12  
**Scope:** Root cause analysis, code paths, and remediation options for both issues.

---

# Part 1 — NG1 Post-Restart: RANGE_BUILDING Restore

## 1.1 Incident Recap

| Field | Value |
|-------|-------|
| **Stream** | NG1 |
| **Slot time** | 09:00 CT (14:00 UTC) |
| **Restart** | 14:18:05 UTC (09:18 CT) |
| **Pre-restart state** | RANGE_BUILDING |
| **Post-restart state** | ARMED (bar_count=0) |
| **Outcome** | Never reached RANGE_LOCKED |

NG1 had entered RANGE_BUILDING at 07:00 UTC. At 14:18, slot time had already passed (09:00 CT). After restart, NG1 went to ARMED with bar_count=0 and never recovered.

---

## 1.2 Existing RANGE_BUILDING Restore Implementation

**Location:** `StreamStateMachine.cs` lines 5424–5576, 5584–5636

### Flow

1. **Constructor:** When `existing.LastState == "RANGE_BUILDING"` and `!_rangeLocked`, calls `RestoreRangeBuildingFromSnapshot(tradingDateStr, Stream)`.
2. **RestoreRangeBuildingFromSnapshot:**
   - Requires `_rangeBuildingSnapshotPersister != null` (from `RangeBuildingSnapshotPersister.GetInstance(_projectRoot)`).
   - Loads latest snapshot via `LoadLatest(tradingDay, streamId)` from `logs/robot/range_building_{trading_date}.jsonl`.
   - Validates identity: trading_date, stream_id, instrument, session, slot_time must match.
   - Validates timestamp: `last_processed_bar_time_utc` must not be in the future.
   - Restores bars into `_barBuffer`, RangeHigh, RangeLow, FreezeClose.
   - Sets `State = RANGE_BUILDING`, `_preHydrationComplete = true`.
   - If `utcNow >= SlotTimeUtc`, calls `TryLockRange(utcNow)` immediately.
3. **Persist:** On RANGE_BUILD_START and on each bar update in RANGE_BUILDING, `PersistRangeBuildingSnapshot(lastBarUtc)` writes to the same file.

### Failure Modes (Why NG1 Could Fall Back to ARMED)

| # | Cause | Evidence / Check |
|---|-------|------------------|
| 1 | **No snapshot file** | `range_building_2026-03-16.jsonl` missing or empty for NG1 |
| 2 | **Persister null** | Unlikely — `GetInstance` is called with `_projectRoot` in constructor |
| 3 | **Project root mismatch** | Multi-strategy instances may use different `projectRoot`; snapshot written to path A, restore reads from path B |
| 4 | **Identity mismatch** | Timetable changed (session, slot_time, instrument) between persist and restore; snapshot validation rejects |
| 5 | **Snapshot never persisted** | NG1 had bar_count=0 in RANGE_BUILDING (no bars at all); `PersistRangeBuildingSnapshot` is called on RANGE_BUILD_START with `barsAtStart` — if `barsAtStart.Count == 0`, we still persist (with empty bars). But `PersistRangeBuildingSnapshot` is also called from `HandleRangeBuildingState` on each bar — if no bars ever arrived, we'd have one snapshot from RANGE_BUILD_START with 0 bars |
| 6 | **Last bar in future** | Clock skew; `lastBarUtc > utcNow` → restore rejected |
| 7 | **Order of restore** | RANGE_LOCKED restore runs first. If hydration log had a RANGE_LOCKED event for NG1 (unlikely — NG1 never locked), RANGE_BUILDING restore would be skipped. NG1 had no RANGE_LOCKED, so this doesn't apply |
| 8 | **BarsRequest / data path** | After restore, NG1 needs bars to continue. If `_projectRoot` or BarsRequest path differs, no bars arrive → stays in RANGE_BUILDING with restored bars. But we restore bars from snapshot. If snapshot had bars, we'd have them. If snapshot had 0 bars, we'd restore 0 bars. Then `TryLockRange` would need `HasSufficientRangeBars` — might fail. We'd stay RANGE_BUILDING and wait for more bars. The incident says "bar_count=0" — that could mean GetBarBufferCount() at ARMED diagnostic time. So we never entered RANGE_BUILDING at all — we fell back to ARMED. So RestoreRangeBuildingFromSnapshot either wasn't called or returned early (no snapshot, persister null, identity mismatch, etc.) |

### Most Likely Root Cause

**No valid snapshot found** — `LoadLatest` returned null. Possible reasons:

1. **File missing:** `range_building_2026-03-16.jsonl` did not exist or had no NG1 lines.
2. **Project root:** NinjaTrader strategy may resolve `projectRoot` differently than expected (e.g. strategy assembly path vs. workspace path). Snapshot written to one location, restore reads from another.
3. **Identity mismatch:** Timetable changed between 07:00 and 14:18 (slot_time, session). Snapshot had old slot_time; current stream had new slot_time → validation failed.
4. **Stream_id / trading_date:** Snapshot uses `stream_id` from the stream. NG1 → stream_id "NG1". Trading date "2026-03-16". LoadLatest filters `snap.stream_id == streamId` and `snap.trading_date == tradingDay`. Should match.

---

## 1.3 Diagnostic Recommendations

1. **Check for snapshot file:** `logs/robot/range_building_2026-03-16.jsonl` — does it exist? Any lines with `"stream_id":"NG1"`?
2. **Check logs for restore events:** Search for `RANGE_BUILDING_SNAPSHOT_RESTORED`, `RANGE_BUILDING_SNAPSHOT_RESTORE_FAILED`, `RANGE_BUILDING_RESTORE_FALLBACK_TO_EMPTY`, `RANGE_BUILDING_RESTORE_IDENTITY_MISMATCH` in robot logs for 2026-03-16.
3. **Verify project root:** Log `_projectRoot` at StreamStateMachine construction for NG1; confirm it matches the path where `range_building_*.jsonl` is written.
4. **Timetable at 14:18:** What was the timetable for NG1 at restart? Same slot_time (09:00) and session (S1) as at 07:00?

---

## 1.4 Remediation Options

### A. Fallback: Hydration Log for RANGE_BUILDING

When `RestoreRangeBuildingFromSnapshot` fails (no snapshot), consider scanning `hydration_{day}.jsonl` for `RANGE_BUILDING_START` for this stream. If found, we know the stream was building. We could:

- Attempt to rebuild from BarsRequest historical bars (same as post-restart CL2, RTY2, NG2).
- The current flow already does this: when restore fails, we fall back to ARMED. From ARMED, we need `utcNow >= RangeStartUtc` and `barCount > 0` to transition to RANGE_BUILDING. So the blocker is **bar_count=0**. Why no bars?

### B. Root Cause: Why bar_count=0 After Restart?

After restart, each stream re-initializes. For ARMED state, `GetBarBufferCount()` returns bars in `_barBuffer`. The bar buffer is populated by:

1. **BarsRequest** (historical bars up to current time) — triggered by `RESTART_BARSREQUEST_NEEDED` when `is_mid_session_restart`.
2. **Live bars** via `AddBarToBuffer` in `OnBar` / bar handler.

If BarsRequest hasn't completed yet when we run the ARMED diagnostic, bar_count could be 0. But CL2, RTY2, NG2 eventually got bars and transitioned to RANGE_BUILDING. NG1 did not. So either:

- **NG1 never received BarsRequest** (instrument MNG might be handled differently).
- **BarsRequest for MNG failed or returned no data.**
- **NG1's strategy instance** (if per-instrument) might not have been the one to request MNG bars.
- **Timing:** NG1 slot was 09:00. At 14:18, we're 5+ hours past. The "next" slot or session logic might have excluded NG1 from bar requests.

### C. Recommended Fix: Hydration Log as Fallback for RANGE_BUILDING

When `RestoreRangeBuildingFromSnapshot` returns (failed), and `existing.LastState == "RANGE_BUILDING"`:

1. **Emit explicit diagnostic:** `RANGE_BUILDING_RESTORE_FAILED_FALLBACK_TO_ARMED` with reason (NO_SNAPSHOT, IDENTITY_MISMATCH, etc.), project_root, snapshot_file_path, and whether the file exists.
2. **Force BarsRequest for this instrument:** Ensure the strategy requests historical bars for NG1's instrument (MNG) on restart, even if it's past slot time. The goal is to get bars so we can at least attempt a late lock (or commit NO_TRADE_MARKET_CLOSE if appropriate).
3. **Consider "would have locked" from ranges log:** If `ranges_{day}.jsonl` has a RANGE_LOCKED event for NG1 from a prior process (e.g. another instance), we could use that. But NG1 never locked, so no such event exists.

### D. Implementation: Improve Diagnostics

Add to `RestoreRangeBuildingFromSnapshot` when `LoadLatest` returns null:

```csharp
var filePath = Path.Combine(_logDir, $"range_building_{tradingDay}.jsonl");
var fileExists = File.Exists(filePath);
var lineCount = fileExists ? File.ReadAllLines(filePath).Count(l => !string.IsNullOrWhiteSpace(l)) : 0;
// Log with file_path, file_exists, line_count, project_root for debugging
```

---

# Part 2 — RTY2 / M2K "Price Outside Limits" Rejection

## 2.1 Incident Recap

| Field | Value |
|-------|-------|
| **Stream** | RTY2 |
| **Execution instrument** | M2K (Micro E-mini Russell 2000) |
| **Slot time** | 09:30 CT (timetable) |
| **RANGE_LOCKED** | 15:14:05 UTC (10:14:05 CT) |
| **Breakout levels** | brk_long=2545.1, brk_short=2535.2 |
| **Rejected order** | Short stop @ 2535.2 (brk_short) |
| **Rejection** | ~350ms after submission |
| **Error** | "Order rejected: Please check the order price. The current price is outside the price limits set for this product." |

---

## 2.2 NinjaTrader / CME Price Validation

### NinjaTrader Stop Order Rules

From NinjaTrader documentation and forum posts:

- **Sell stop** must be **below** the current **bid**.
- **Buy stop** must be **above** the current **ask**.

If the stop price violates this at submission time, the order is rejected. During fast markets, price can move between validation and exchange acceptance.

### Why a Short Stop @ 2535.2 Could Be Rejected

A **sell stop at 2535.2** means "sell when price hits 2535.2." For the order to be valid:

- The stop price (2535.2) must be **below** the current bid.
- So we need **bid > 2535.2**.

If the market has already moved down through 2535.2:

- Bid might be 2534 or lower.
- A sell stop at 2535.2 would be **above** the bid → **invalid** → rejected.

### Timeline (Plausible)

| Time | Event |
|------|-------|
| 10:14:05.000 CT | RANGE_LOCKED; range computed; brk_short=2535.2 |
| 10:14:05.000 CT | Submit short stop @ 2535.2 |
| 10:14:05.350 CT | Broker receives order; market has moved; bid ≤ 2535.2 → **Rejected** |

So the breakout level was likely already crossed by the time the order reached the broker.

---

## 2.3 CME M2K Contract Specs

- **Tick size:** 0.10 index points
- **Price limits:** CME circuit breakers (5%, 7%, 13%, 20%) apply to halting trading, not to individual order validation.
- The "price outside limits" message is from **NinjaTrader/broker** validation (bid/ask rules), not CME circuit breakers.

---

## 2.4 Current Robot Behavior

- **Initial submission:** No pre-submission check that stop price is valid vs. current bid/ask.
- **IsBreakoutValidForResubmit:** Used for recovery/restart retry only; not for initial slot-time submission.
- **Price sanity check:** Within freshness window, we block if price is *far* from breakout; we do not handle the "already crossed, use market order" case.

---

## 2.5 Remediation Options

### A. Marketable Stop → Market Order

When submitting a stop and we detect the breakout has already been crossed (e.g. for short: bid <= brk_short - 1 tick):

- **Option 1:** Submit a **market order** instead of a stop (immediate fill).
- **Option 2:** Do not submit (treat as already triggered; commit NO_TRADE or similar).

**Recommendation:** Option 1 — if the breakout has already occurred, a market order captures the intent. Option 2 is more conservative but loses the trade.

### B. Pre-Submission Validation

Before `SubmitStopEntryOrder`, call `GetCurrentMarketPrice` and validate:

- **Short stop @ P:** Require `bid > P` (or `bid >= P + 1*tick` for buffer).
- **Long stop @ P:** Require `ask < P` (or `ask <= P - 1*tick` for buffer).

If invalid, either submit market order or skip.

### C. Retry with Adjusted Price

On rejection with "price outside limits":

1. Get current bid/ask.
2. If short: use `Math.Min(brk_short, bid - 1*tick)` as stop price.
3. Retry submission (or submit market if already crossed).

### D. Alerting

Log and alert on this rejection type so it can be monitored and tuned.

---

## 2.6 Implementation Sketch

**Location:** `SubmitStopEntryBracketsAtLock` or `NinjaTraderSimAdapter.SubmitStopEntryOrderReal`

```csharp
// Before submitting stop orders:
var (bid, ask) = _executionAdapter.GetCurrentMarketPrice(ExecutionInstrument, utcNow);
// Short stop @ brkShort: valid only if bid > brkShort
if (bid.HasValue && bid.Value <= brkShort)
{
    // Breakout already crossed - submit market order for short instead of stop
    // Or: skip and log ENTRY_SUBMIT_SKIPPED_BREAKOUT_ALREADY_CROSSED
}
// Long stop @ brkLong: valid only if ask < brkLong
if (ask.HasValue && ask.Value >= brkLong)
{
    // Breakout already crossed - submit market order for long
}
```

---

# Part 3 — Summary

| Issue | Root Cause | Fix Status | Recommended Action |
|-------|------------|------------|--------------------|
| **NG1 post-restart** | RestoreRangeBuildingFromSnapshot failed (likely no snapshot or path/identity mismatch); fallback to ARMED with bar_count=0; NG1 never received bars to rebuild | Not fixed | 1) Add diagnostics (file path, existence, line count). 2) Verify BarsRequest for MNG on restart. 3) Consider hydration log fallback. |
| **RTY2/M2K rejection** | Sell stop @ 2535.2 placed when bid had already moved at or below 2535.2; NinjaTrader rejects sell stops above bid | Not fixed | 1) Pre-submission validation: if breakout crossed, submit market order. 2) Retry with adjusted price on rejection. 3) Alert on this rejection type. |

---

# Part 4 — Files Referenced

| File | Purpose |
|------|---------|
| `RobotCore_For_NinjaTrader/StreamStateMachine.cs` | RestoreRangeBuildingFromSnapshot, RestoreRangeLockedFromHydrationLog, HandleArmedState |
| `RobotCore_For_NinjaTrader/RangeBuildingSnapshotPersister.cs` | Persist/LoadLatest |
| `logs/robot/range_building_{date}.jsonl` | Snapshot storage |
| `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` | SubmitStopEntryOrderReal |
| `docs/robot/RANGE_BUILDING_PERSISTENCE_IMPLEMENTATION.md` | Design doc |
| NinjaTrader Support: "Avoiding stop order rejections due to invalid price" | Bid/ask validation rules |
