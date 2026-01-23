# Run ID + Critical Push Notification Dedupe

## What changed

- Every `RobotEngine.Start()` generates a **new GUID run_id** (stable for that engine lifetime).
- `RobotLogger` attaches `run_id` to **every** `RobotLogEvent`.
- `HealthMonitor.ReportCritical(eventType, payload)` dedupes **exactly one push** per:

`(eventType, run_id)`

## Dedupe rules (deterministic)

When `HealthMonitor.ReportCritical(...)` is called:

1. **Preferred**: `dedupe_key = eventType + ":" + run_id`
2. **Fallback** (only if trading date is already known/locked): `dedupe_key = eventType + ":" + trading_date`
3. **Last resort**: `dedupe_key = eventType + ":UNKNOWN_RUN"` and logs `CRITICAL_DEDUPE_MISSING_RUN_ID` once per process

## Verification (logs-only)

1. **Restart robot**
   - Confirm new log lines include `"run_id": "<non-null>"`.

2. **Trigger** `EXECUTION_GATE_INVARIANT_VIOLATION` **twice in the same run**
   - Expect exactly **one** `CRITICAL_EVENT_REPORTED`
   - Expect exactly **one** `PUSHOVER_NOTIFY_ENQUEUED`

3. **Trigger** `DISCONNECT_FAIL_CLOSED_ENTERED` **twice in the same run**
   - Same dedupe behavior as above

4. **Restart robot and trigger again**
   - You should receive a new notification because `run_id` changed

## Notes

- No config files are modified by this change.
- Only the whitelisted critical events are eligible for `ReportCritical()`:
  - `EXECUTION_GATE_INVARIANT_VIOLATION`
  - `DISCONNECT_FAIL_CLOSED_ENTERED`

