# Push Notification Reliability Assessment

**Date**: 2026-03-06  
**Purpose**: Assess whether Watchdog push notifications will fire as intended, given the code and NinjaTrader complexities.

---

## 1. Data Flow: Heartbeat to Alert

```
NinjaTrader Tick() 
  → LogEvent(ENGINE_TICK_CALLSITE)  [RobotEngine.cs:1381]
  → RobotLogger.Write() 
  → RobotLoggingService.Log() [enqueue]
  → Background worker [flush every ~500ms]
  → robot_ENGINE.jsonl
  → EventFeedGenerator.process_new_events() [Watchdog, every 1s]
  → frontend_feed.jsonl
  → Aggregator._process_feed_events_sync()
  → EventProcessor.process_event(ENGINE_TICK_CALLSITE)
  → state_manager.update_engine_tick()
  → engine_alive = (now - last_tick) < 60s
  → _check_alert_conditions() → raise_alert(ROBOT_HEARTBEAT_LOST) if !engine_alive
  → NotificationService → Pushover
```

---

## 2. Failure Points and Mitigations

### 2.1 NinjaTrader / Robot (C#)

| Risk | Location | Mitigation |
|------|----------|------------|
| **Tick() not called** | NinjaTrader strategy | NinjaTrader can pause strategy (e.g. "Calculating", disconnect). No tick = no heartbeat. **LOG_FILE_STALLED** catches this: feed file stops growing. |
| **LogEvent before early returns** | RobotEngine.cs:1377 | ENGINE_TICK_CALLSITE is emitted at very start of Tick(), before spec/time checks. ✅ |
| **Logger conversion returns null** | RobotLogger.cs:95 | ENGINE events would be dropped. Fallback writes to per-instance file, not robot_ENGINE. Rare. |
| **Queue backpressure** | RobotLoggingService.cs:369 | If `_queue.Count >= MAX_QUEUE_SIZE` (default 50k), **INFO events are dropped** including ENGINE_TICK_CALLSITE. WARN/ERROR never dropped. ⚠️ Under extreme load, heartbeat could be dropped. |
| **Worker deadlock/crash** | RobotLoggingService | Background worker stops flushing. robot_ENGINE.jsonl stops growing. **LOG_FILE_STALLED** fires. ✅ |
| **File handle lost / NT not flushing** | StreamWriter | robot_ENGINE.jsonl or frontend_feed stops growing. **LOG_FILE_STALLED** fires. ✅ |

### 2.2 EventFeedGenerator (Python, Watchdog process)

| Risk | Location | Mitigation |
|------|----------|------------|
| **Watchdog not running** | — | No alerts. Process monitor (psutil) only checks NinjaTrader.exe. Watchdog must run separately. |
| **process_new_events not called** | aggregator.py | Runs every 1s in _process_events_loop. If loop crashes, no new events. **LOG_FILE_STALLED** would fire if robot_ENGINE grows but frontend_feed doesn't. |
| **Rate limit too aggressive** | event_feed.py:132 | ENGINE_TICK_CALLSITE written at most every 5s per run_id. Heartbeat threshold is 60s. 5s << 60s. ✅ |
| **Read position corruption** | robot_log_read_positions.json | Could skip events. Persisted per file. Rare. |
| **run_id missing** | _process_event | Event skipped if no run_id. ENGINE_TICK_CALLSITE from EngineBase has run_id from RobotLogEvent. |

### 2.3 Aggregator / EventProcessor (Python)

| Risk | Location | Mitigation |
|------|----------|------------|
| **Cursor logic skips events** | _read_feed_events_since | Past bugs (ENGINE_STALLED_INVESTIGATION.md). Current impl uses tail read. Verify cursor logic. |
| **Stall threshold** | config.py | ENGINE_TICK_STALL_THRESHOLD_SECONDS = 60. Rate limit 5s. ✅ |
| **Supervision window** | _check_alert_conditions | Alerts only when market open OR active intents OR recent activity. Avoids false alerts when market closed. |

### 2.4 NotificationService (Python)

| Risk | Location | Mitigation |
|------|----------|------------|
| **Pushover disabled** | notifications.secrets.json | User must configure. No fallback. |
| **Queue full** | notification_service.py | Max 100. Drops oldest. Rate limit 20/hour. |
| **Network failure** | pushover_client | Retries (2). Logs failure. Ledger marks delivery_status=failed. |

---

## 3. Alert-Specific Reliability

| Alert | Primary Signal | Backup / Overlap |
|-------|----------------|------------------|
| **ROBOT_HEARTBEAT_LOST** | No ENGINE_TICK_CALLSITE for 60s | LOG_FILE_STALLED if feed stops growing |
| **LOG_FILE_STALLED** | frontend_feed.jsonl size unchanged 60s | Catches: logging deadlock, file handle lost, NT not flushing, EventFeedGenerator not running |
| **NINJATRADER_PROCESS_STOPPED** | psutil: NinjaTrader.exe not running | Independent of logs |
| **CONNECTION_LOST_SUSTAINED** | Robot emits CONNECTION_LOST; HealthMonitor sustains 60s | Requires Robot to emit. If Robot dead, no event. Heartbeat/process alerts cover that. |
| **POTENTIAL_ORPHAN** | (heartbeat lost OR process missing) + active intents | — |
| **CONFIRMED_ORPHAN** | Heartbeat lost >120s + active intents | — |

---

## 4. Gaps and Recommendations

### 4.1 ENGINE_TICK_CALLSITE Can Be Dropped Under Backpressure

**Issue**: RobotLoggingService drops INFO events when queue >= 50,000. ENGINE_TICK_CALLSITE is INFO.

**Recommendation**: Treat ENGINE_TICK_CALLSITE as critical for watchdog. Options:
- Emit ENGINE_TICK_CALLSITE at WARN level (never dropped), or
- Add ENGINE_TICK_CALLSITE to a "never drop" set in RobotLoggingService, or
- Document that under extreme load (50k+ queued events), heartbeat may be lost and LOG_FILE_STALLED is the backup.

### 4.2 Log Level Configuration

**Issue**: `min_log_level` in LoggingConfig (default INFO). If set to WARN, INFO events (including ENGINE_TICK_CALLSITE) would be filtered out.

**Recommendation**: Ensure ENGINE_TICK_CALLSITE bypasses min_log_level, or document that min_log_level must be INFO or lower for heartbeat to work.

### 4.3 EventFeedGenerator Must Run

**Issue**: EventFeedGenerator runs inside the Watchdog process. If Watchdog is down, no one reads robot logs or writes frontend_feed. LOG_FILE_STALLED won't fire (no one to check). Process monitor (NinjaTrader.exe) also won't run.

**Conclusion**: Watchdog must run as a separate, always-on process. Deploy accordingly.

### 4.4 Two Processes, One Machine

**Architecture**: NinjaTrader (Robot) and Watchdog (Python) run on same machine. If the machine reboots or both crash, no alerts. Consider external heartbeat (e.g. cron pinging a service) for "machine down" detection.

---

## 5. Summary

| Question | Answer |
|----------|--------|
| Will ROBOT_HEARTBEAT_LOST fire when Robot stops? | **Yes**, if ENGINE_TICK_CALLSITE stops flowing for 60s. Caveat: ENGINE_TICK_CALLSITE can be dropped under queue backpressure (INFO level). |
| Will LOG_FILE_STALLED catch logging failures? | **Yes**, if frontend_feed.jsonl size unchanged for 60s. Covers: deadlock, file handle lost, NT not flushing, EventFeedGenerator not running. |
| Will NINJATRADER_PROCESS_STOPPED fire? | **Yes**, if NinjaTrader.exe not running during supervision window. Independent of logs. |
| Are there NinjaTrader-specific complexities? | **Yes**: Tick() can be throttled/paused by NinjaTrader; strategy can be in "Calculating"; disconnect can stop bars. LOG_FILE_STALLED is the backup when no events flow. |
| What could prevent alerts? | (1) Watchdog not running, (2) Pushover not configured, (3) Queue backpressure dropping ENGINE_TICK_CALLSITE, (4) min_log_level filtering it out. |

---

## 6. Suggested Code Change (Recommended)

**Current**: ENGINE_TICK_CALLSITE is not in RobotEventTypes._levelMap, so it gets default "INFO". Under queue backpressure (≥50k events), INFO events are dropped.

**Fix**: Add ENGINE_TICK_CALLSITE to RobotEventTypes with WARN level (never dropped under backpressure):

```csharp
// In RobotEventTypes._levelMap, add:
["ENGINE_TICK_CALLSITE"] = "WARN",  // Watchdog liveness - never drop under backpressure
```

And add to _allEvents:
```
"ENGINE_TICK_CALLSITE",
```

This ensures the heartbeat survives queue backpressure.
