# MNG RECONCILIATION_QTY_MISMATCH Investigation (2026-03-14)

## Summary

- **Incident:** RECONCILIATION_QTY_MISMATCH
- **Instrument:** MNG
- **Started:** 2026-03-14 22:42:23 UTC (17:42:23 Chicago)
- **Duration:** 165+ minutes (still open)
- **Active incidents file:** `data/watchdog/active_incidents.json`

## Why the Incident Hasn't Closed

The incident ends when the watchdog receives **RECONCILIATION_PASS_SUMMARY** from the robot. That event is emitted at the end of every reconciliation pass (every ~60 seconds when the robot is running and broker is connected).

Possible reasons it's still open:

1. **RECONCILIATION_PASS_SUMMARY was filtered before the fix** – Before the notification coverage fix, `RECONCILIATION_PASS_SUMMARY` was not in `LIVE_CRITICAL_EVENT_TYPES`, so the event feed dropped it. The watchdog would never close the incident. **Fix applied:** RECONCILIATION_PASS_SUMMARY is now in LIVE_CRITICAL. **Action:** Restart the watchdog so it picks up the fix; new RECONCILIATION_PASS_SUMMARY events will then close the incident.

2. **Robot not running or broker disconnected** – Reconciliation only runs when `IsBrokerConnected` is true. If the robot stopped or disconnected, no RECONCILIATION_PASS_SUMMARY would be emitted.

3. **Robot logs not being read** – If the event feed isn't reading `robot_ENGINE.jsonl` (e.g. wrong path, archive rotation), events won't reach the watchdog.

## Investigation Steps

### 1. Run the diagnostic script

```powershell
.\scripts\diagnose_reconciliation.ps1
```

This shows:
- Risk latches (frozen instruments)
- Open journals by instrument
- Recent RECONCILIATION_QTY_MISMATCH and RECONCILIATION_CONTEXT events

### 2. Check robot logs for RECONCILIATION_PASS_SUMMARY

```powershell
# Last 20 RECONCILIATION_PASS_SUMMARY events
Select-String -Path "logs\robot\robot_ENGINE.jsonl" -Pattern "RECONCILIATION_PASS_SUMMARY" | Select-Object -Last 20

# Last 10 RECONCILIATION events (any type)
Get-Content logs\robot\robot_ENGINE.jsonl -Tail 10000 | Select-String "RECONCILIATION"
```

If RECONCILIATION_PASS_SUMMARY appears every ~60 seconds, the robot is emitting it. If not, reconciliation may not be running (broker disconnected, robot stopped, or historical mode).

### 3. Check if RECONCILIATION_PASS_SUMMARY reaches the watchdog

```powershell
# Search frontend_feed for RECONCILIATION_PASS_SUMMARY
Select-String -Path "logs\robot\frontend_feed.jsonl" -Pattern "RECONCILIATION_PASS_SUMMARY" | Select-Object -Last 10
```

If RECONCILIATION_PASS_SUMMARY is in `frontend_feed.jsonl`, the event feed is passing it through (requires the fix + watchdog restart). If it's in `robot_ENGINE.jsonl` but not in `frontend_feed.jsonl`, the event feed was filtering it before the fix.

### 4. Verify the MNG mismatch

The mismatch means broker position ≠ journal quantity for MNG. Typical cases:

- **Broker ahead:** Broker has position, journal says 0 (e.g. manual trade, journal not written)
- **Journal ahead:** Journal has position, broker says 0 (e.g. fill not reported, journal out of sync)

Check open journals for MNG:

```powershell
Get-ChildItem data\execution_journals -Filter "*MNG*" | ForEach-Object {
    $j = Get-Content $_.FullName -Raw | ConvertFrom-Json
    Write-Host "$($_.Name) EntryFilled=$($j.EntryFilled) TradeCompleted=$($j.TradeCompleted) Qty=$($j.EntryFilledQuantityTotal)"
}
```

### 5. Resolution options

**Option A – Flatten and restart (when broker position is wrong)**

1. Manually flatten MNG in NinjaTrader.
2. Restart the robot. Reconciliation will close orphan journals when broker is flat.

**Option B – Force reconcile (when broker position is correct)**

```powershell
.\scripts\force_reconcile.ps1 MNG
```

Creates `pending_force_reconcile.json`; the robot picks it up on the next Tick.

**Option C – Clear the stuck incident (watchdog only)**

If the mismatch is resolved but the incident is stuck because RECONCILIATION_PASS_SUMMARY was never received, you can manually clear `data/watchdog/active_incidents.json` by removing the RECONCILIATION_QTY_MISMATCH entry. **Only do this if you've verified the mismatch is resolved** (broker and journal agree).

## Related Files

- `modules/robot/core/Execution/ReconciliationRunner.cs` – Emits RECONCILIATION_QTY_MISMATCH and RECONCILIATION_PASS_SUMMARY
- `RobotCore_For_NinjaTrader/RobotEngine.cs` – `RunReconciliationPeriodicThrottle`, `onQuantityMismatch` callback
- `modules/watchdog/incident_recorder.py` – INCIDENT_END_EVENTS: RECONCILIATION_PASS_SUMMARY → RECONCILIATION_QTY_MISMATCH
- `docs/robot/execution/RECONCILIATION_QTY_MISMATCH_RESOLUTION.md` – Resolution steps
