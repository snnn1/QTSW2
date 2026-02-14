# Watchdog UI Verification Report

**Date:** 2026-02-13  
**Purpose:** Verify Watchdog UI works correctly in accordance with the robot

---

## Data Flow (Robot → UI)

```
Robot (NinjaTrader) 
  → logs/robot/robot_ENGINE.jsonl (ENGINE_TICK_CALLSITE, ENGINE_ALIVE, ONBARUPDATE_CALLED)
  → EventFeedGenerator (process_new_events every 1s)
  → logs/robot/frontend_feed.jsonl
  → WatchdogAggregator._process_feed_events_sync
  → WatchdogStateManager
  → API (port 8002)
  → Watchdog UI (port 5175)
```

---

## Verification Results

### 1. Robot Event Emission ✓

Robot is emitting liveness events correctly:
- **ENGINE_TICK_CALLSITE** – Tick() heartbeat (rate-limited to every 5s in feed)
- **ENGINE_ALIVE** – Strategy heartbeat on bar processing
- **ONBARUPDATE_CALLED** – Bar processing confirmation

Multiple engine instances observed (MNQ, M2K, MYM, MGC, ES, etc.) with distinct run_ids.

### 2. File Timestamps ✓

| File | Last Modified | Status |
|------|---------------|--------|
| robot_ENGINE.jsonl | 03:13:24 UTC | Active |
| frontend_feed.jsonl | 03:13:25 UTC | Being updated |

### 3. API Status ✓

**GET /api/watchdog/status** (when robot active):

| Field | Value | Expected |
|-------|-------|----------|
| engine_alive | true | ✓ |
| engine_activity_state | ACTIVE | ✓ |
| last_engine_tick_chicago | ~21:14:10 CT | Recent |
| engine_tick_stall_detected | false | ✓ |
| connection_status | Connected | ✓ |
| recovery_state | CONNECTED_OK | ✓ |
| kill_switch_active | false | ✓ |
| market_open | true | ✓ |
| stuck_streams | [] | ✓ |

### 4. Stream States ✓

**GET /api/watchdog/stream-states** returns 9 streams from timetable:
- CL2, GC2, YM2, YM1, NG2, RTY2, ES1, NG1, NQ2
- All show empty state (no RANGE_LOCKED yet) – expected outside range windows
- timetable_unavailable: false

### 5. UI Components (Expected Display)

| Component | Data Source | Expected When Robot Active |
|-----------|-------------|---------------------------|
| WatchdogHeader | /api/watchdog/status | "ENGINE ALIVE" badge, last tick time |
| StreamStatusTable | /api/watchdog/stream-states | Stream list with timetable slots |
| RiskGatesPanel | /api/watchdog/risk-gates | Gate status |
| ActiveIntentPanel | /api/watchdog/active-intents | Open intents |
| LiveEventFeed | WebSocket /ws/events | Real-time events |
| CriticalAlertBanner | status.stuck_streams, etc. | Hidden when no alerts |

---

## Thresholds (config.py)

- **ENGINE_TICK_STALL_THRESHOLD_SECONDS:** 15 – engine_alive = false if no tick for 15s
- **DATA_STALL_THRESHOLD_SECONDS:** 120 – bar stall detection
- **STUCK_STREAM_THRESHOLD_SECONDS:** 300 – stream stuck detection

---

## Manual UI Check

1. Open http://localhost:5175
2. Verify **ENGINE ALIVE** badge (green) when robot is running
3. Verify **Last tick** shows recent Chicago time
4. Verify **Stream Status** table shows timetable streams
5. Verify **Connection: Connected**
6. Verify no critical alerts when system healthy

---

## Known Behaviors

- **engine_alive: false** when robot stopped or market closed – expected
- **ENGINE IDLE (MARKET CLOSED)** when market closed – expected
- **ENGINE STALLED** when no ENGINE_TICK_CALLSITE for 15+ seconds – expected
- Stream states empty until RANGE_LOCKED events received – expected
