# BarsRequest Error Diagnosis

## Error Message

```
Failed to request historical bars from NinjaTrader: Cannot determine BarsRequest time range for ES. 
This indicates no enabled streams exist for this instrument, or streams not yet created. 
Ensure timetable has enabled streams for ES and engine.Start() completed successfully.
```

## Root Cause Analysis

This error occurs when `GetBarsRequestTimeRange()` returns `null` because:

1. **No streams found for instrument** - Streams weren't created for ES
2. **All streams are Committed** - Streams exist but are marked as Committed (from previous day's journals)

### How Committed Works

- When a stream completes trading for a day, its journal is marked `Committed = true`
- On next startup, if a journal exists and is Committed, the stream is marked as DONE
- Committed streams are **excluded** from BarsRequest (they don't need bars)

### Why This Happens

**Scenario 1: Previous Day's Journals Exist**
- Yesterday's ES1/ES2 journals exist and are Committed
- Today's streams load those journals → marked as Committed
- BarsRequest filters out Committed streams → no enabled streams → error

**Scenario 2: Streams Not Created**
- Timetable doesn't have enabled ES streams
- Or `ApplyTimetable()` failed silently
- Or streams were skipped during creation

## Diagnosis Steps

### Step 1: Check Logs for Stream Creation

Look for `STREAMS_CREATED` event in logs:

```bash
# Search for stream creation
grep "STREAMS_CREATED" logs/robot/*.jsonl | tail -5
```

**Expected**: Should see ES1 and ES2 in the streams list

**If missing**: Streams weren't created (check timetable or ApplyTimetable errors)

### Step 2: Check Stream Status

Look for `BARSREQUEST_STREAM_STATUS` event:

```bash
# Search for stream status
grep "BARSREQUEST_STREAM_STATUS" logs/robot/*.jsonl | tail -5
```

**Check fields**:
- `total_streams`: Should be 2 (ES1, ES2)
- `streams[].committed`: Check if all are `true`
- `streams[].state`: Check states

### Step 3: Check Timetable

Verify timetable has ES streams enabled:

```bash
# Check timetable file
cat data/timetable/timetable_current.json | grep -A 5 "ES1\|ES2"
```

**Expected**:
```json
{
  "stream": "ES1",
  "enabled": true
},
{
  "stream": "ES2", 
  "enabled": true
}
```

### Step 4: Check Journal Files

Check if committed journals exist:

```bash
# List ES journal files
ls -la logs/robot/journal/*_ES*.json

# Check if they're committed
grep "Committed" logs/robot/journal/*_ES*.json
```

**If journals exist and are Committed**: This is the issue - old journals are marking streams as done

## Solutions

### Solution 1: Delete Committed Journals (If Testing)

If you're testing and want to reset streams:

```bash
# Delete ES journals for today's date
rm logs/robot/journal/2026-01-20_ES1.json
rm logs/robot/journal/2026-01-20_ES2.json
```

**Then restart strategy** - streams will be created fresh

### Solution 2: Check Trading Date

Verify trading date matches:

```bash
# Check timetable trading_date
grep "trading_date" data/timetable/timetable_current.json

# Check journal dates
ls logs/robot/journal/ | grep ES
```

**If dates don't match**: Old journals are being loaded for wrong date

### Solution 3: Verify Stream Creation Logic

Check that `ApplyTimetable()` is being called:

```bash
# Search for timetable application
grep "TIMETABLE_APPLIED\|STREAMS_CREATED" logs/robot/*.jsonl | tail -10
```

**Expected**: Should see `STREAMS_CREATED` with ES1 and ES2

## Quick Fix for Testing

If you just want to test without fixing the root cause:

1. **Delete today's journals**:
   ```bash
   rm logs/robot/journal/2026-01-20_ES*.json
   ```

2. **Restart strategy** - streams will be created fresh

3. **BarsRequest should work** - streams won't be Committed

## Expected Log Flow

**Normal startup**:
1. `TIMETABLE_LOADED` - Timetable loaded successfully
2. `STREAMS_CREATED` - Streams created (including ES1, ES2)
3. `BARSREQUEST_STREAM_STATUS` - Shows ES streams (committed: false)
4. `BARSREQUEST_REQUESTED` - Bars requested successfully

**If error occurs**:
1. `TIMETABLE_LOADED` - ✓
2. `STREAMS_CREATED` - Check if ES streams are listed
3. `BARSREQUEST_STREAM_STATUS` - Check if streams are Committed
4. `BARSREQUEST_RANGE_CHECK` - Shows "ALL_STREAMS_COMMITTED" or "NO_STREAMS_FOUND"

## Prevention

To prevent this in production:

1. **Journal cleanup**: Delete old committed journals periodically
2. **Date validation**: Ensure trading date matches journal dates
3. **Stream validation**: Verify streams are created before BarsRequest
4. **Error handling**: Make BarsRequest gracefully handle no-enabled-streams case

## Code Location

- **Error thrown**: `RobotSimStrategy.cs` line 178-182
- **Range check**: `RobotEngine.cs` line 1593-1649
- **Stream creation**: `RobotEngine.cs` line 1014-1092
- **Journal loading**: `StreamStateMachine.cs` line 218-265
