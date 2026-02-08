# NG1 Slot Time Issue: 07:30 vs 09:00

## Problem
NG1 triggered at 07:30 Chicago time when the timetable specifies 09:00.

## Root Cause
The stream was initialized with `slot_time: 07:30` (likely from an earlier timetable or a different timetable file), and the current timetable has `slot_time: 09:00`. However, the stream is already past `PRE_HYDRATION` state, so it rejects the slot time update per the protection logic in `StreamStateMachine.cs` (lines 597-626).

## Code Protection
The robot has protection against slot time changes after stream initialization:

```csharp
// NQ2 FIX: Prevent slot_time changes after stream initialization if stream is past PRE_HYDRATION
if (State != StreamState.PRE_HYDRATION && SlotTimeChicago != newSlotTimeChicago)
{
    // Reject the update
    return;
}
```

This prevents timetable updates from affecting active streams mid-session, but it also means that if a stream was created with the wrong slot time, it won't be corrected until the next trading day.

## Current Timetable
- **NG1**: `slot_time: 09:00`, `session: S1`, `enabled: true`
- **S1 allowed slot_end_times**: `["07:30", "08:00", "09:00"]`

Both 07:30 and 09:00 are valid slot times for S1, so the validation passes, but the wrong one is being used.

## Solution Options

### Option 1: Delete Journal and Restart (Immediate Fix)
Delete the NG1 journal file for today to force re-initialization with the correct slot time:
1. Find journal file: `data/journals/streams/2026-02-06/NG1.json`
2. Delete or rename it
3. Restart NinjaTrader strategy
4. Stream will be recreated with `slot_time: 09:00` from current timetable

### Option 2: Fix Timetable Loading Order (Long-term Fix)
Ensure the timetable is loaded and validated before any streams are created, and ensure streams are always created with the latest timetable values.

### Option 3: Allow Slot Time Updates During PRE_HYDRATION
The current protection allows updates during `PRE_HYDRATION`, but if the stream has already progressed past that state, it won't accept updates. Consider allowing updates if the new slot time hasn't been reached yet.

## Verification
Check logs for:
- `SLOT_TIME_UPDATE_REJECTED` events for NG1
- `STREAM_CREATED` events showing the initial slot time used
- Timetable loading events showing when the timetable was loaded

## Prevention
1. Ensure timetable is loaded before stream creation
2. Validate timetable slot times match expected values before creating streams
3. Consider logging a warning when a stream is created with a slot time that differs from the current timetable
