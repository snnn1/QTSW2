# Log Analysis Guide: Diagnosing Gap Violations

## Overview
This guide explains what information is available in the logs to diagnose gap tolerance violations and other stream issues.

## Key Log Events to Check

### 1. Stream Creation (`STREAMS_CREATED`)
**When:** At startup, after trading date is locked  
**What it tells you:**
- Total number of streams created
- Which streams exist for each instrument
- Stream details: `stream_id`, `instrument`, `session`, `slot_time`, `committed` status, `state`
- Streams grouped by instrument

**Example:**
```json
{
  "eventType": "STREAMS_CREATED",
  "stream_count": 5,
  "streams": [
    {"stream_id": "ng1", "instrument": "NG", "session": "S1", "slot_time": "07:30", "committed": false, "state": "PRE_HYDRATION"},
    {"stream_id": "es1", "instrument": "ES", "session": "S1", "slot_time": "07:30", "committed": false, "state": "PRE_HYDRATION"}
  ],
  "streams_by_instrument": {
    "NG": [{"session": "S1", "slot_time": "07:30", "committed": false}],
    "ES": [{"session": "S1", "slot_time": "07:30", "committed": false}]
  }
}
```

**What to check:**
- Are all expected streams present?
- Are streams in the correct state (`PRE_HYDRATION` initially)?
- Are any streams already `committed` (should be false at startup)?

---

### 2. BarsRequest Status (`BARSREQUEST_STREAM_STATUS`, `BARSREQUEST_RANGE_DETERMINED`)
**When:** When `GetBarsRequestTimeRange()` is called  
**What it tells you:**
- All streams for the instrument (committed and uncommitted)
- Which streams are enabled (not committed)
- How the BarsRequest range was calculated:
  - `earliest_range_start` (earliest `range_start_time` across sessions)
  - `latest_slot_time` (latest `slot_time` across enabled streams)
  - `sessions_used` (which sessions are involved)
  - `session_range_starts` (range_start_time for each session)
  - `stream_slot_times` (slot_time for each enabled stream)

**Example:**
```json
{
  "eventType": "BARSREQUEST_STREAM_STATUS",
  "instrument": "NG",
  "total_streams": 2,
  "streams": [
    {"stream_id": "ng1", "session": "S1", "slot_time": "07:30", "committed": false, "state": "PRE_HYDRATION"},
    {"stream_id": "ng2", "session": "S2", "slot_time": "09:30", "committed": false, "state": "PRE_HYDRATION"}
  ]
}
```

```json
{
  "eventType": "BARSREQUEST_RANGE_DETERMINED",
  "instrument": "NG",
  "earliest_range_start": "02:00",
  "latest_slot_time": "09:30",
  "enabled_stream_count": 2,
  "sessions_used": ["S1", "S2"],
  "session_range_starts": {"S1": "02:00", "S2": "08:00"},
  "stream_slot_times": [
    {"stream_id": "ng1", "session": "S1", "slot_time": "07:30"},
    {"stream_id": "ng2", "session": "S2", "slot_time": "09:30"}
  ]
}
```

**What to check:**
- Is the BarsRequest range correct?
- Are all expected streams included?
- Are any streams missing or committed unexpectedly?

---

### 3. BarsRequest Raw Results (`BARSREQUEST_RAW_RESULT`)
**When:** After BarsRequest completes  
**What it tells you:**
- How many bars NinjaTrader returned (`bars_returned_raw`)
- First and last bar timestamps
- Request time range (`request_start_local`, `request_end_local`)

**Example:**
```json
{
  "eventType": "BARSREQUEST_RAW_RESULT",
  "instrument": "NG",
  "bars_returned_raw": 330,
  "first_bar_time": "2026-01-19T08:00:00.0000000+00:00",
  "last_bar_time": "2026-01-19T15:30:00.0000000+00:00",
  "request_start_local": "2026-01-19T02:00:00.0000000",
  "request_end_local": "2026-01-19T09:30:00.0000000"
}
```

**What to check:**
- Did BarsRequest return bars? (`bars_returned_raw > 0`)
- Does the time range match expectations?
- Are there gaps between `first_bar_time` and `request_start_local`?
- Are there gaps between `last_bar_time` and `request_end_local`?

---

### 4. Pre-Hydration Bars (`PRE_HYDRATION_BARS_LOADED`, `PRE_HYDRATION_BARS_FILTERED`)
**When:** When bars are loaded from BarsRequest  
**What it tells you:**
- How many bars were loaded
- How many bars were filtered (outside range, duplicates, etc.)
- Bar source (BARSREQUEST)

**Example:**
```json
{
  "eventType": "PRE_HYDRATION_BARS_LOADED",
  "bars_loaded": 330,
  "bars_filtered": 0,
  "bar_source": "BARSREQUEST",
  "instrument": "NG"
}
```

**What to check:**
- Were bars successfully loaded?
- Were many bars filtered? (indicates range issues)
- Is the bar source correct?

---

### 5. Gap Tolerance Violations (`GAP_TOLERANCE_VIOLATION`)
**When:** When a gap exceeds tolerance limits  
**What it tells you:**
- **`violation_reason`**: Which rule was violated:
  - `"Single gap X minutes exceeds MAX_SINGLE_GAP_MINUTES (3.0)"`
  - `"Total gap X minutes exceeds MAX_TOTAL_GAP_MINUTES (6.0)"`
  - `"Gap X minutes in last 10 minutes exceeds MAX_GAP_LAST_10_MINUTES (2.0)"`
- **`gap_minutes`**: The specific gap that triggered the violation
- **`largest_single_gap_minutes`**: Largest single gap seen so far
- **`total_gap_minutes`**: Cumulative gap time
- **`previous_bar_open_chicago`**: Timestamp of previous bar
- **`current_bar_open_chicago`**: Timestamp of current bar (showing the gap)
- **`slot_time_chicago`**: When the slot ends

**Example:**
```json
{
  "eventType": "GAP_TOLERANCE_VIOLATION",
  "instrument": "NG",
  "slot": "ng1",
  "violation_reason": "Single gap 5.2 minutes exceeds MAX_SINGLE_GAP_MINUTES (3.0)",
  "gap_minutes": 5.2,
  "largest_single_gap_minutes": 5.2,
  "total_gap_minutes": 5.2,
  "previous_bar_open_chicago": "2026-01-19T07:15:00.0000000-06:00",
  "current_bar_open_chicago": "2026-01-19T07:20:12.0000000-06:00",
  "slot_time_chicago": "2026-01-19T07:30:00.0000000-06:00"
}
```

**What to check:**
- **Which rule was violated?** (single gap, total gap, or last 10 minutes)
- **How big was the gap?** (`gap_minutes`)
- **When did it occur?** (check `current_bar_open_chicago` relative to `slot_time_chicago`)
- **Was this in the last 10 minutes?** (if so, this is more critical)
- **What was the previous bar time?** (helps identify missing data)

---

### 6. Gap Tolerated (`GAP_TOLERATED`)
**When:** When a gap exists but is within tolerance  
**What it tells you:**
- Gap size (within limits)
- Cumulative gap tracking

**Example:**
```json
{
  "eventType": "GAP_TOLERATED",
  "instrument": "NG",
  "slot": "ng1",
  "gap_minutes": 2.5,
  "largest_single_gap_minutes": 2.5,
  "total_gap_minutes": 2.5
}
```

**What to check:**
- Are there many tolerated gaps? (may indicate data quality issues)
- Is `total_gap_minutes` approaching the limit (6.0)?

---

### 7. Gap Violations Summary (`GAP_VIOLATIONS_SUMMARY`)
**When:** Every 5 minutes (if any streams are invalidated)  
**What it tells you:**
- **`invalidated_stream_count`**: How many streams are invalidated
- **`invalidated_streams`**: List of all invalidated streams with details
- **`total_streams`**: Total number of streams

**Example:**
```json
{
  "eventType": "GAP_VIOLATIONS_SUMMARY",
  "invalidated_stream_count": 1,
  "invalidated_streams": [
    {
      "stream_id": "ng1",
      "instrument": "NG",
      "session": "S1",
      "slot_time": "07:30",
      "state": "RANGE_INVALIDATED"
    }
  ],
  "total_streams": 5,
  "note": "Streams invalidated due to gap tolerance violations - trading blocked for these streams"
}
```

**What to check:**
- **Which streams are invalidated?** (not just ng1)
- **How many total streams?** (to see percentage affected)
- **Are multiple instruments affected?** (indicates systemic issue)

---

### 8. Range Compute Start (`RANGE_COMPUTE_START`)
**When:** When range computation begins  
**What it tells you:**
- Stream state at range compute time
- Whether range was invalidated

**What to check:**
- Did range computation start?
- Was the stream already invalidated at this point?

---

### 9. Slot End Summary (`SLOT_END_SUMMARY`)
**When:** When a stream commits (ends its slot)  
**What it tells you:**
- Final status: `RANGE_INVALIDATED`, `NO_TRADE`, `TRADE_EXECUTED`, etc.
- Whether range was locked
- Whether trade was executed
- Reason for the outcome

**Example:**
```json
{
  "eventType": "SLOT_END_SUMMARY",
  "instrument": "NG",
  "slot": "ng1",
  "rangeStatus": "RANGE_INVALIDATED",
  "rangeLocked": false,
  "tradeExecuted": false,
  "reason": "Range invalidated due to gap tolerance violation"
}
```

**What to check:**
- Final outcome for each stream
- Whether trading was blocked due to gap violations

---

## Diagnostic Checklist

### For Gap Violations:

1. **Check Stream Creation**
   - [ ] Are all expected streams present?
   - [ ] Are streams in correct state?

2. **Check BarsRequest**
   - [ ] Did BarsRequest succeed? (`BARSREQUEST_RAW_RESULT`)
   - [ ] How many bars were returned?
   - [ ] Does the time range cover the full session?

3. **Check Pre-Hydration**
   - [ ] Were bars successfully loaded?
   - [ ] Were many bars filtered?

4. **Check Gap Violations**
   - [ ] Which rule was violated? (single gap, total gap, last 10 minutes)
   - [ ] How big was the gap?
   - [ ] When did it occur? (relative to slot_time)
   - [ ] What was the previous bar time?

5. **Check Summary**
   - [ ] Which streams are invalidated? (`GAP_VIOLATIONS_SUMMARY`)
   - [ ] Is this isolated to one stream or multiple?

### Common Issues and What Logs Show:

| Issue | Log Evidence |
|-------|--------------|
| **Missing historical data** | `bars_returned_raw: 0` or `bars_returned_raw` much less than expected |
| **BarsRequest failed** | Error messages, `bars_returned_raw: 0` |
| **Gap in data feed** | `GAP_TOLERANCE_VIOLATION` with `gap_minutes > 3.0` |
| **Multiple small gaps** | `total_gap_minutes > 6.0` in violation |
| **Gap near slot end** | Violation in last 10 minutes (`gap_minutes > 2.0` near `slot_time`) |
| **Starting mid-session** | `Starting before range_start_time` warning, or BarsRequest skipped |
| **Systemic issue** | Multiple streams invalidated in `GAP_VIOLATIONS_SUMMARY` |

---

## Next Steps

Based on the logs, you can determine:

1. **Root Cause**: Was it missing BarsRequest data, live feed gaps, or both?
2. **Scope**: Is it isolated to ng1 or affecting multiple streams?
3. **Timing**: When did the gap occur? (early in session vs. last 10 minutes)
4. **Severity**: How big was the gap? (single large gap vs. cumulative small gaps)

This information helps decide:
- Whether to adjust gap tolerance limits
- Whether to improve BarsRequest coverage
- Whether to investigate data feed issues
- Whether this is a one-time issue or recurring problem
