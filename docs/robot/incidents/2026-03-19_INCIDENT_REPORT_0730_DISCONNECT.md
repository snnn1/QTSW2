# Incident Report: Robot Down ~7:30 AM Chicago (2026-03-19)

**Date**: 2026-03-19  
**Type**: Connection loss / robot disconnect  
**Approximate time**: 7:30–7:38 AM Chicago (12:30–12:38 UTC)

---

## 1. TIMELINE

| Time (Chicago) | Time (UTC) | Event |
|----------------|------------|-------|
| **7:30:01** | 12:30:01 | Last normal activity: ENGINE_TIMER_HEARTBEAT, ONBARUPDATE_CALLED (MCL, MES, MNQ, M2K, MGC), RECONCILIATION_PASS_SUMMARY |
| **7:30:02** | 12:30:02 | Bar processing continues; ENGINE_ALIVE, ENGINE_TICK_CALLSITE across strategy instances |
| **7:38:52** | 12:38:52 | **CONNECTION_LOST** – Watchdog detects no new events; incident starts (duration 1793 sec) |
| **7:43:46** | 12:43:46 | Additional CONNECTION_LOST incidents (strategy instances reporting; duration ~1500 sec) |
| **8:08:45** | 13:08:45 | **Recovery** – Events resume; CONNECTION_LOST incidents end |
| **8:08:49** | 13:08:49 | Robot processing bars again (e.g. GC 7:30 bar, RANGE_BUILDING_SNAPSHOT_WRITTEN) |

---

## 2. EVENTS LEADING UP TO DISCONNECT

### Last ~2 minutes before disconnect (7:28–7:30 AM Chicago)

- **7:28–7:30**: Normal bar processing across all instruments (MCL, MES, MNQ, M2K, MGC, MNG, etc.)
- **7:30:00**: RANGE_BUILDING_SAFETY_ASSERTION_CHECK (GC1) – "Stream is -90 minutes past slot time"
- **7:30:01**: ENGINE_TIMER_HEARTBEAT, ONBARUPDATE_CALLED for MCL 05-26, MES 06-26, MNQ 06-26, M2K 06-26, MGC 04-26
- **7:30:01–02**: RECONCILIATION_PASS_SUMMARY – "No open journals to reconcile"
- **7:30:02**: ENGINE_ALIVE, ENGINE_TICK_CALLSITE (watchdog liveness)

No errors, stalls, or anomalies in the logs immediately before the gap.

### Gap (7:30–8:08 AM Chicago)

- **~38 minutes** with no events in `frontend_feed.jsonl`
- Watchdog incident log: CONNECTION_LOST from 12:38:52 UTC to 13:08:45 UTC
- No DISCONNECT_FAIL_CLOSED_ENTERED in feed for this window (robot likely could not log)

---

## 3. ROOT CAUSE ASSESSMENT

### Most likely: NinjaTrader / data connection loss

1. **Abrupt stop** – Activity stops cleanly at 7:30 with no prior warnings.
2. **No robot-side errors** – No exceptions, stalls, or DATA_STALL before the gap.
3. **Watchdog inference** – CONNECTION_LOST is inferred from absence of ENGINE_TICK / ENGINE_ALIVE; NinjaTrader was not writing to the feed.
4. **Recovery** – Events resume at 8:08, consistent with reconnection and catch-up.

### Possible contributing factors

| Factor | Notes |
|--------|-------|
| **Network** | Brief network drop affecting NinjaTrader ↔ broker/data provider |
| **Data provider** | Rithmic/CQG or other provider disconnect |
| **NinjaTrader** | Platform disconnect (e.g. "4+ disconnects in 5 min" rule) |
| **Session open** | 7:30 AM Chicago is near regular session open; possible provider/platform load |

### What the strategy did not do

- The strategy does not call `Connection.Disconnect` or `Connection.Connect`.
- Connection loss is driven by NinjaTrader/platform, not strategy logic (see [2026-03-18_STRATEGY_DISCONNECT_ASSESSMENT](../2026-03-18_STRATEGY_DISCONNECT_ASSESSMENT.md)).

---

## 4. IMPACT

- **Duration**: ~38 minutes (7:30–8:08 AM Chicago)
- **Execution**: All execution blocked during fail-closed (if entered)
- **Data**: ~38 minutes of bars likely buffered and replayed after reconnect (explains burst at 13:08)
- **Streams**: Recovery path would have run CONNECTION_RECOVERY_RESOLVED or DISCONNECT_RECOVERY_COMPLETE

---

## 5. RECOMMENDATIONS

1. **Push notification** – DISCONNECT_RECOVERY_COMPLETE push is now implemented so you get notified when the robot exits fail-closed.
2. **Network** – Check home/office network stability; consider wired connection if on Wi‑Fi.
3. **Data provider** – Review provider status/outages around 7:30 AM Central.
4. **NinjaTrader** – If disconnects recur, review NinjaTrader logs and connection settings.

---

## 6. DATA SOURCES

- `logs/robot/frontend_feed.jsonl` – Last events before gap, first events after
- `logs/robot/robot_GC.jsonl` – Bar processing around 7:30
- `data/watchdog/incidents.jsonl` – CONNECTION_LOST incidents (12:38:52, 12:43:46 UTC)
