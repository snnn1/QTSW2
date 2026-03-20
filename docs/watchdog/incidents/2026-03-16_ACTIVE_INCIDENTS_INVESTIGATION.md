# Active Incidents Investigation — CONNECTION_LOST & DATA_STALL MNG

**Date:** 2026-03-16  
**Scope:** CONNECTION_LOST (22h), DATA_STALL MNG (34s)

---

## Summary

| Incident | Status | Root Cause |
|----------|--------|------------|
| **CONNECTION_LOST** | Stale — should be closed | `CONNECTION_CONFIRMED` does not end the incident |
| **DATA_STALL MNG** | Likely recovered | Brief data gap; `DATA_STALL_RECOVERED` in feed |

---

## 1. CONNECTION_LOST (22:40:07 CT, 1326 min)

### Timeline

- **Start:** 2026-03-15T03:40:07 UTC (22:40:07 CT March 14)
- **Persisted in:** `data/watchdog/active_incidents.json`
- **Duration:** ~22 hours (still open)

### Root Cause

The incident recorder ends `CONNECTION_LOST` only when it sees:

- `CONNECTION_RECOVERED`
- `CONNECTION_RECOVERED_NOTIFICATION`

When NinjaTrader is **closed and restarted**:

1. `CONNECTION_LOST` fires when the connection drops.
2. NinjaTrader exits — no recovery event.
3. On restart, HealthMonitor starts with `_currentIncident = null`.
4. Connection is Connected at startup → HealthMonitor emits **`CONNECTION_CONFIRMED`**, not `CONNECTION_RECOVERED`.
5. The incident recorder never receives a recovery event → `CONNECTION_LOST` stays active.

### Evidence

- Feed has many `CONNECTION_CONFIRMED` events (e.g. 23:10:19 UTC March 14).
- `CONNECTION_CONFIRMED` is not in `INCIDENT_END_EVENTS` in `incident_recorder.py`.
- HealthMonitor emits `CONNECTION_CONFIRMED` when “Connected from start (no prior incident)”.

### Fix

Add `CONNECTION_CONFIRMED` to `INCIDENT_END_EVENTS` for `CONNECTION_LOST`:

```python
# incident_recorder.py INCIDENT_END_EVENTS
"CONNECTION_RECOVERED": "CONNECTION_LOST",
"CONNECTION_RECOVERED_NOTIFICATION": "CONNECTION_LOST",
"CONNECTION_CONFIRMED": "CONNECTION_LOST",  # ADD: NinjaTrader restart = connection restored
```

### Immediate Workaround

Manually clear the stale incident:

```bash
# Backup first
cp data/watchdog/active_incidents.json data/watchdog/active_incidents.json.bak

# Clear CONNECTION_LOST (or delete the file to clear all)
# Edit data/watchdog/active_incidents.json and remove the CONNECTION_LOST entry,
# or replace with {}
```

---

## 2. DATA_STALL MNG (20:46:33 CT, 34s)

### Timeline

- **Start:** 2026-03-16T01:46:33 UTC (20:46:33 CT March 15)
- **Feed events:**
  - `DATA_LOSS_DETECTED` at 01:46:33 (event_seq 1198) — “elapsed_seconds: 213, threshold: 180”
  - `DATA_STALL_RECOVERED` at 01:46:33 and 01:47:23
- **Note:** “Notification suppressed - handled by gap tolerance + range invalidation (log only, rate-limited)”

### Analysis

1. MNG had a data gap (last bar 01:43, detected at 01:46).
2. `DATA_LOSS_DETECTED` started the `DATA_STALL` incident.
3. `DATA_STALL_RECOVERED` should end it when processed.
4. The 34s duration matches a short stall before recovery.

### Current State

- `active_incidents.json` currently shows only `CONNECTION_LOST` (no `DATA_STALL`).
- If the UI still shows `DATA_STALL`, it may be:
  - Cached
  - From before the watchdog processed `DATA_STALL_RECOVERED`

### MNG-Specific Notes

- Natural gas (NG) can have gaps when the market is closed or on weekends.
- HealthMonitor rate-limits `DATA_LOSS_DETECTED` to once per 15 minutes per instrument.
- The “gap tolerance + range invalidation” path suppresses notifications but still logs.

---

## 3. Recommendations

1. **Implement fix:** Add `CONNECTION_CONFIRMED` to `INCIDENT_END_EVENTS` in `incident_recorder.py`.
2. **Clear stale incident:** Remove `CONNECTION_LOST` from `active_incidents.json` (or apply the fix and wait for the next `CONNECTION_CONFIRMED`).
3. **Monitor MNG:** Occasional short data stalls are expected; ensure `DATA_STALL_RECOVERED` is processed and incidents are closed.
