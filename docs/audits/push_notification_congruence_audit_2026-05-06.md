# QTSW2 Push Notification Congruence Audit

Audit date: 2026-05-06

Purpose:
- Check robot-side and watchdog-side push notifications against the current robot/watchdog architecture.
- Identify notification gaps, duplicate-suppression risk, stale event registration, and evidence-storage mismatches.

Evidence level:
- Source-proven and build-proven after this audit.
- Watchdog notification contract is harness-proven by focused pytest coverage.
- Robot notification registry/whitelist changes are build-proven only until a deployed DLL hash is confirmed and a SIM/runtime run emits the relevant events.
- Deploy attempt on 2026-05-06 was blocked because NinjaTrader held `Robot.Core.dll` open; deployed DLL hash did not match the rebuilt DLL at audit end.

## Findings

1. Watchdog notification config path was wrong by default.
   - `NotificationService` defaulted to `system/modules/configs/watchdog/...`.
   - Actual config lives at `configs/watchdog/...`.
   - Impact: watchdog notifications could silently disable unless paths were injected.

2. Watchdog alert ledger path was wrong by default.
   - `AlertLedger` defaulted to `system/modules/data/watchdog/alert_ledger.jsonl`.
   - Actual ledger path is `data/watchdog/alert_ledger.jsonl`.
   - Impact: push alert evidence could be split from the operator/audit data root.

3. Watchdog `ROBOT_HEARTBEAT_LOST` was suppressed as robot overlap.
   - That is unsafe for a crash/freeze case because the robot may not be able to notify.
   - It is now unsuppressed; `NINJATRADER_PROCESS_STOPPED` remains separate and non-suppressed.

4. Watchdog resend intervals were configured but ignored.
   - `raise_alert(... min_resend_interval_seconds=...)` now respects the caller interval.
   - Callers that pass `0` still send fail-loud repeated alerts.

5. Watchdog generated alerts were missing labels.
   - Added labels for recovery loop, feed ingestion delay, watchdog loop slow, anomaly rate, order stuck, reconciliation gate fail-closed incident, and adoption grace expired incident.

6. Robot emitted `MID_SESSION_RESTART_NOTIFICATION` but the event registry did not include it.
   - Latest evidence before fix:
     - `logs/robot/robot_ENGINE_20260506_104534.jsonl` logged `UNREGISTERED_EVENT_TYPE` for `MID_SESSION_RESTART_NOTIFICATION`.
   - The event is now registered in core, NinjaTrader runtime copy, and NT_ADDONS copy.

7. Robot `ReportCritical` callers and whitelist were not congruent.
   - The whitelist now includes current direct caller classes: execution policy validation, IEA enqueue fail-closed block, disconnect fail-closed/recovery, slot/reentry protection failures, reconciliation mismatch/fail-closed, supervisory halt/ack/kill-switch events, and state-consistency recovery failure.

## Changes

- `configs/watchdog/notifications.json`
  - `ROBOT_HEARTBEAT_LOST` suppression changed to `false`.
- `system/modules/watchdog/notifications/notification_service.py`
  - Uses repo-root config/secrets constants.
  - Adds missing alert labels.
  - Honors per-alert resend intervals.
- `system/modules/watchdog/alert_ledger.py`
  - Uses repo-root alert ledger constant.
  - Ignores malformed non-object JSONL records during rehydrate.
- `system/modules/robot/core/HealthMonitor.cs`
  - Aligns critical notification whitelist with current robot callers.
- `system/modules/robot/core/RobotEventTypes.*`
  - Registers `MID_SESSION_RESTART_NOTIFICATION`, `EXECUTION_POLICY_VALIDATION_FAILED`, and `FLATTEN_EMERGENCY_ON_BLOCK_ENQUEUE_UNSUPPORTED`.
- Runtime mirrors updated:
  - `system/RobotCore_For_NinjaTrader/*`
  - `system/NT_ADDONS/*`
- Added focused tests:
  - `system/modules/watchdog/tests/test_notification_service.py`

## Verification

- `pytest system/modules/watchdog/tests/test_notification_service.py system/modules/watchdog/tests/test_watchdog_operator_contracts.py -q`
  - Passed: 14 tests.
- `pytest system/modules/watchdog/tests/test_notification_service.py -q`
  - Passed: 6 tests.
- `dotnet build system/modules/robot/core/Robot.Core.csproj -c Release -v:minimal`
  - Passed: 0 errors.
- `dotnet build system/RobotCore_For_NinjaTrader/Robot.Core.csproj -c Release -v:minimal`
  - Passed: 0 errors.
- `tools/rebuild_and_deploy.ps1`
  - Build passed.
  - Deploy failed because NinjaTrader locked the deployed DLL for all 30 copy attempts.

## Deploy State

- Rebuilt DLL hash:
  - `0D4788415A72ACA27A4B23D1247F6427E5BF2B702BDB8B6FE4BCF900B7C6DDB6`
- Currently deployed DLL hash:
  - `655A9ABADA4C6B19BB781733C0CF79C938D17D660DD454CF32F5BB1A2FDCA988`
- Conclusion:
  - Source/build/harness are ready.
  - Deployed runtime is still old until NinjaTrader is closed and the DLL copy succeeds.
