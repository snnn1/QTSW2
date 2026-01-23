# Robot Event Type Catalog

**Last Updated**: 2026-01-22  
**Total Events**: 159

This catalog documents all event types emitted by the robot logging system. Events are organized by category for easy reference.

## Event Schema

All events follow the `RobotLogEvent` schema:

```json
{
  "ts_utc": "2026-01-22T21:51:15.9111629+00:00",
  "level": "INFO",
  "source": "RobotEngine",
  "instrument": "ES",
  "account": "SIM",
  "run_id": "abc123",
  "event": "ORDER_SUBMITTED",
  "message": "ORDER_SUBMITTED",
  "data": { ... }
}
```

### Required Fields
- `ts_utc`: ISO8601 UTC timestamp
- `level`: Log level (DEBUG, INFO, WARN, ERROR)
- `source`: Component name (RobotEngine, StreamStateMachine, ExecutionAdapter, etc.)
- `instrument`: Instrument symbol (ES, NQ, GC, etc.) or empty string
- `event`: Event type identifier

### Optional Fields
- `account`: Account identifier
- `run_id`: Correlation ID for tracing
- `message`: Human-readable message
- `data`: Event-specific payload (structure varies)

---

## Event Categories

### 1. Engine Lifecycle (9 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `ENGINE_START` | INFO | RobotEngine | Engine started |
| `ENGINE_STOP` | INFO | RobotEngine | Engine stopped |
| `ENGINE_STAND_DOWN` | WARN | RobotEngine | Engine entered stand-down state |
| `ENGINE_TICK_INVALID_STATE` | ERROR | RobotEngine | Tick called in invalid state |
| `ENGINE_TICK_STALL_DETECTED` | ERROR | RobotEngine | Tick stall detected (no ticks for N seconds) |
| `ENGINE_TICK_STALL_RECOVERED` | INFO | RobotEngine | Tick stall recovered |
| `ENGINE_TICK_HEARTBEAT` | DEBUG | RobotEngine | Tick heartbeat (proves Tick is advancing) |
| `ENGINE_BAR_HEARTBEAT` | DEBUG | RobotEngine | Bar heartbeat (proves bar ingress) |

**Payload Examples**:
- `ENGINE_START`: `{}`
- `ENGINE_TICK_HEARTBEAT`: `{ "trading_date": "2026-01-22", "minutes_since_last_tick": 5.2 }`

---

### 2. Configuration (13 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `PROJECT_ROOT_RESOLVED` | INFO | RobotEngine | Project root resolved |
| `LOG_DIR_RESOLVED` | INFO | RobotEngine | Log directory resolved |
| `LOGGING_CONFIG_LOADED` | INFO | RobotEngine | Logging configuration loaded |
| `SPEC_LOADED` | INFO | RobotEngine | Parity spec loaded |
| `SPEC_INVALID` | ERROR | RobotEngine | Parity spec invalid |
| `SPEC_NAME_LOADED` | INFO | RobotEngine | Spec name loaded |
| `HEALTH_MONITOR_CONFIG_LOADED` | INFO | RobotEngine | Health monitor config loaded |
| `HEALTH_MONITOR_CONFIG_ERROR` | ERROR | RobotEngine | Health monitor config error |
| `HEALTH_MONITOR_CONFIG_MISSING` | WARN | RobotEngine | Health monitor config missing |
| `HEALTH_MONITOR_CONFIG_NULL` | WARN | RobotEngine | Health monitor config null |
| `HEALTH_MONITOR_DISABLED` | INFO | RobotEngine | Health monitor disabled |
| `HEALTH_MONITOR_STARTED` | INFO | RobotEngine | Health monitor started |
| `HEALTH_MONITOR_EVALUATION_ERROR` | ERROR | RobotEngine | Health monitor evaluation error |
| `PUSHOVER_CONFIG_MISSING` | WARN | RobotEngine | Pushover config missing |

**Payload Examples**:
- `LOG_DIR_RESOLVED`: `{ "log_dir": "C:\\Users\\jakej\\QTSW2\\logs\\robot", "source": "env:QTSW2_LOG_DIR" }`
- `HEALTH_MONITOR_CONFIG_LOADED`: `{ "config_path": "configs/robot/health_monitor.json" }`

---

### 3. Timetable (11 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `TIMETABLE_LOADED` | INFO | RobotEngine | Timetable loaded |
| `TIMETABLE_UPDATED` | INFO | RobotEngine | Timetable updated |
| `TIMETABLE_VALIDATED` | INFO | RobotEngine | Timetable validated |
| `TIMETABLE_INVALID` | ERROR | RobotEngine | Timetable invalid |
| `TIMETABLE_INVALID_TRADING_DATE` | ERROR | RobotEngine | Timetable has invalid trading date |
| `TIMETABLE_MISSING_TRADING_DATE` | ERROR | RobotEngine | Timetable missing trading date |
| `TIMETABLE_TRADING_DATE_MISMATCH` | ERROR | RobotEngine | Timetable trading date mismatch |
| `TIMETABLE_APPLY_SKIPPED` | INFO | RobotEngine | Timetable apply skipped |
| `TIMETABLE_PARSING_COMPLETE` | INFO | RobotEngine | Timetable parsing complete |
| `TIMETABLE_POLL_STALL_DETECTED` | ERROR | RobotEngine | Timetable poll stall detected |
| `TIMETABLE_POLL_STALL_RECOVERED` | INFO | RobotEngine | Timetable poll stall recovered |

**Payload Examples**:
- `TIMETABLE_LOADED`: `{ "timetable_path": "timetables/2026-01-22.json", "stream_count": 12 }`
- `TIMETABLE_INVALID`: `{ "error": "Invalid JSON", "line": 42 }`

---

### 4. Trading Date (1 event)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `TRADING_DATE_LOCKED` | INFO | RobotEngine | Trading date locked |

**Payload Examples**:
- `TRADING_DATE_LOCKED`: `{ "trading_date": "2026-01-22" }`

---

### 5. Streams (6 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `STREAMS_CREATED` | INFO | RobotEngine | Streams created |
| `STREAMS_CREATION_FAILED` | ERROR | RobotEngine | Stream creation failed |
| `STREAMS_CREATION_SKIPPED` | WARN | RobotEngine | Stream creation skipped |
| `DUPLICATE_STREAM_ID` | ERROR | RobotEngine | Duplicate stream ID detected |
| `STREAM_STAND_DOWN` | WARN | RobotEngine | Stream entered stand-down |
| `STREAM_SKIPPED` | INFO | RobotEngine | Stream skipped (disabled/invalid) |

**Payload Examples**:
- `STREAMS_CREATED`: `{ "stream_count": 12, "instruments": ["ES", "NQ", "GC"] }`
- `DUPLICATE_STREAM_ID`: `{ "stream_id": "ES1", "reason": "Duplicate in timetable" }`

---

### 6. Bars (13 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `BAR_ACCEPTED` | INFO | RobotEngine | Bar accepted |
| `BAR_DATE_MISMATCH` | WARN | RobotEngine | Bar date mismatch (outside session window) |
| `BAR_PARTIAL_REJECTED` | WARN | RobotEngine | Bar partially rejected |
| `BAR_RECEIVED_BEFORE_DATE_LOCKED` | WARN | RobotEngine | Bar received before trading date locked |
| `BAR_RECEIVED_NO_STREAMS` | WARN | RobotEngine | Bar received but no streams active |
| `BAR_REJECTION_CONTINUOUS_FUTURE` | WARN | RobotEngine | Continuous future bar rejection |
| `BAR_REJECTION_CONTINUOUS_NO_DATE_LOCK` | WARN | RobotEngine | Bar rejection (no date lock) |
| `BAR_REJECTION_RATE_HIGH` | WARN | RobotEngine | Bar rejection rate high |
| `BAR_REJECTION_SUMMARY` | INFO | RobotEngine | Bar rejection summary |
| `BAR_DELIVERY_SUMMARY` | INFO | RobotEngine | Bar delivery summary |
| `BAR_DELIVERY_TO_STREAM` | DEBUG | RobotEngine | Bar delivered to stream |
| `BAR_TIME_INTERPRETATION_MISMATCH` | WARN | RobotEngine | Bar time interpretation mismatch |
| `BAR_TIME_INTERPRETATION_LOCKED` | INFO | RobotEngine | Bar time interpretation locked |
| `BAR_TIME_DETECTION_STARTING` | DEBUG | RobotEngine | Bar time detection starting |

**Payload Examples**:
- `BAR_ACCEPTED`: `{ "instrument": "ES", "bar_time": "2026-01-22T14:30:00-06:00", "bar_age_minutes": 0.5 }`
- `BAR_REJECTION_SUMMARY`: `{ "rejected_count": 5, "accepted_count": 100, "rejection_rate": 0.05 }`

---

### 7. BarsRequest (11 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `BARSREQUEST_REQUESTED` | INFO | RobotSimStrategy | BarsRequest requested |
| `BARSREQUEST_EXECUTED` | INFO | RobotSimStrategy | BarsRequest executed |
| `BARSREQUEST_FAILED` | ERROR | RobotSimStrategy | BarsRequest failed |
| `BARSREQUEST_SKIPPED` | INFO | RobotSimStrategy | BarsRequest skipped |
| `BARSREQUEST_INITIALIZATION` | INFO | RobotSimStrategy | BarsRequest initialization |
| `BARSREQUEST_RANGE_CHECK` | DEBUG | RobotEngine | BarsRequest range check |
| `BARSREQUEST_RANGE_DETERMINED` | INFO | RobotEngine | BarsRequest range determined |
| `BARSREQUEST_STREAM_STATUS` | DEBUG | RobotEngine | BarsRequest stream status |
| `BARSREQUEST_FILTER_SUMMARY` | DEBUG | RobotEngine | BarsRequest filter summary |
| `BARSREQUEST_UNEXPECTED_COUNT` | WARN | RobotSimStrategy | BarsRequest unexpected count |
| `BARSREQUEST_ZERO_BARS_DIAGNOSTIC` | DEBUG | RobotEngine | BarsRequest zero bars diagnostic |

**Payload Examples**:
- `BARSREQUEST_EXECUTED`: `{ "instrument": "ES", "bar_count": 100, "start_time": "2026-01-22T08:00:00-06:00" }`
- `BARSREQUEST_FAILED`: `{ "instrument": "ES", "error": "No data available" }`

---

### 8. Pre-hydration (13 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `PRE_HYDRATION_BARS_LOADED` | INFO | RobotEngine | Pre-hydration bars loaded |
| `PRE_HYDRATION_BARS_FILTERED` | DEBUG | RobotEngine | Pre-hydration bars filtered |
| `PRE_HYDRATION_BARS_SKIPPED` | DEBUG | RobotEngine | Pre-hydration bars skipped |
| `PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE` | DEBUG | RobotEngine | Pre-hydration bars skipped (stream state) |
| `PRE_HYDRATION_BARS_SKIPPED_ACTIVE_STREAM` | DEBUG | RobotEngine | Pre-hydration bars skipped (active stream) |
| `PRE_HYDRATION_NO_BARS_AFTER_FILTER` | WARN | RobotEngine | Pre-hydration no bars after filter |
| `PRE_HYDRATION_COMPLETE_SET` | DEBUG | StreamStateMachine | Pre-hydration complete set |
| `PRE_HYDRATION_COMPLETE_BLOCK_ENTERED` | DEBUG | StreamStateMachine | Pre-hydration complete block entered |
| `PRE_HYDRATION_AFTER_COMPLETE_BLOCK` | DEBUG | StreamStateMachine | Pre-hydration after complete block |
| `PRE_HYDRATION_AFTER_VARIABLES` | DEBUG | StreamStateMachine | Pre-hydration after variables |
| `PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC` | DEBUG | StreamStateMachine | Pre-hydration before range diagnostic |
| `PRE_HYDRATION_RANGE_START_DIAGNOSTIC` | DEBUG | StreamStateMachine | Pre-hydration range start diagnostic |
| `PRE_HYDRATION_HANDLER_TRACE` | DEBUG | StreamStateMachine | Pre-hydration handler trace |
| `PRE_HYDRATION_FAILED` | ERROR | StreamStateMachine | Pre-hydration failed |

**Payload Examples**:
- `PRE_HYDRATION_BARS_LOADED`: `{ "instrument": "ES", "stream": "ES1", "bar_count": 50 }`
- `PRE_HYDRATION_FAILED`: `{ "error": "Failed to resolve project root" }`

---

### 9. Range Computation (9 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `RANGE_LOCKED` | INFO | StreamStateMachine | Range locked (computed successfully) |
| `RANGE_COMPUTE_FAILED` | ERROR | StreamStateMachine | Range computation failed |
| `RANGE_COMPUTE_START` | DEBUG | StreamStateMachine | Range computation started |
| `RANGE_COMPUTE_NO_BARS_DIAGNOSTIC` | DEBUG | StreamStateMachine | Range compute no bars diagnostic |
| `RANGE_COMPUTE_BAR_FILTERING` | DEBUG | StreamStateMachine | Range compute bar filtering |
| `RANGE_FIRST_BAR_ACCEPTED` | DEBUG | StreamStateMachine | Range first bar accepted |
| `RANGE_INTENT_ASSERT` | DEBUG | StreamStateMachine | Range intent assert |
| `RANGE_LOCK_ASSERT` | DEBUG | StreamStateMachine | Range lock assert |
| `RANGE_INVALIDATED` | WARN | StreamStateMachine | Range invalidated |

**Payload Examples**:
- `RANGE_LOCKED`: `{ "stream": "ES1", "range_high": 6964.75, "range_low": 6925.25, "bar_count": 100 }`
- `RANGE_COMPUTE_FAILED`: `{ "stream": "ES1", "reason": "Insufficient bars", "bar_count": 5 }`

---

### 10. Execution (10 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `EXECUTION_MODE_SET` | INFO | RobotEngine | Execution mode set |
| `EXECUTION_BLOCKED` | WARN | ExecutionAdapter | Execution blocked |
| `EXECUTION_ERROR` | ERROR | ExecutionAdapter | Execution error |
| `EXECUTION_FILLED` | INFO | ExecutionAdapter | Execution filled |
| `EXECUTION_PARTIAL_FILL` | INFO | ExecutionAdapter | Execution partial fill |
| `EXECUTION_SKIPPED_DUPLICATE` | WARN | StreamStateMachine | Execution skipped (duplicate) |
| `EXECUTION_UPDATE_UNKNOWN_ORDER` | WARN | ExecutionAdapter | Execution update unknown order |
| `EXECUTION_UPDATE_MOCK` | DEBUG | ExecutionAdapter | Execution update (mock) |
| `EXECUTION_SUMMARY_WRITTEN` | INFO | RobotEngine | Execution summary written |
| `EXECUTION_GATE_INVARIANT_VIOLATION` | ERROR | ExecutionAdapter | Execution gate invariant violation |

**Payload Examples**:
- `EXECUTION_FILLED`: `{ "intent_id": "abc123", "instrument": "ES", "fill_price": 6964.75, "quantity": 1 }`
- `EXECUTION_GATE_INVARIANT_VIOLATION`: `{ "error": "EXECUTION_SHOULD_BE_ALLOWED_BUT_IS_NOT" }`

---

### 11. Orders (17 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `ORDER_SUBMITTED` | INFO | ExecutionAdapter | Order submitted |
| `ORDER_SUBMIT_SUCCESS` | INFO | ExecutionAdapter | Order submit success |
| `ORDER_SUBMIT_FAIL` | ERROR | ExecutionAdapter | Order submit failed |
| `ORDER_SUBMIT_ATTEMPT` | DEBUG | ExecutionAdapter | Order submit attempt |
| `ORDER_ACKNOWLEDGED` | INFO | ExecutionAdapter | Order acknowledged |
| `ORDER_REJECTED` | ERROR | ExecutionAdapter | Order rejected |
| `ORDER_CANCELLED` | INFO | ExecutionAdapter | Order cancelled |
| `ENTRY_SUBMITTED` | INFO | StreamStateMachine | Entry submitted |
| `PROTECTIVES_PLACED` | INFO | ExecutionAdapter | Protectives placed |
| `PROTECTIVE_ORDERS_SUBMITTED` | INFO | ExecutionAdapter | Protective orders submitted |
| `PROTECTIVE_ORDERS_FAILED_FLATTENED` | WARN | ExecutionAdapter | Protective orders failed (flattened) |
| `STOP_BRACKETS_SUBMITTED` | INFO | StreamStateMachine | Stop brackets submitted |
| `STOP_BRACKETS_SUBMIT_ATTEMPT` | DEBUG | StreamStateMachine | Stop brackets submit attempt |
| `STOP_BRACKETS_SUBMIT_FAILED` | ERROR | StreamStateMachine | Stop brackets submit failed |
| `STOP_MODIFY_ATTEMPT` | DEBUG | ExecutionAdapter | Stop modify attempt |
| `STOP_MODIFY_SUCCESS` | INFO | ExecutionAdapter | Stop modify success |
| `STOP_MODIFY_FAIL` | ERROR | ExecutionAdapter | Stop modify failed |
| `STOP_MODIFY_SKIPPED` | INFO | ExecutionAdapter | Stop modify skipped |

**Payload Examples**:
- `ORDER_SUBMITTED`: `{ "intent_id": "abc123", "order_type": "ENTRY_STOP", "direction": "Long", "stop_price": 6964.75, "quantity": 1 }`
- `ORDER_REJECTED`: `{ "intent_id": "abc123", "reason": "Insufficient margin" }`

---

### 12. Execution Adapters (3 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `ADAPTER_SELECTED` | INFO | ExecutionAdapterFactory | Adapter selected |
| `SIM_ACCOUNT_VERIFIED` | INFO | ExecutionAdapter | SIM account verified |
| `LIVE_MODE_BLOCKED` | ERROR | ExecutionAdapter | Live mode blocked |

**Payload Examples**:
- `ADAPTER_SELECTED`: `{ "adapter_type": "NinjaTraderSimAdapter", "execution_mode": "SIM" }`
- `SIM_ACCOUNT_VERIFIED`: `{ "account": "SIM101" }`

---

### 13. Kill Switch (3 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `KILL_SWITCH_INITIALIZED` | INFO | KillSwitch | Kill switch initialized |
| `KILL_SWITCH_ACTIVE` | ERROR | KillSwitch | Kill switch active |
| `KILL_SWITCH_ERROR_FAIL_CLOSED` | ERROR | KillSwitch | Kill switch error (fail-closed) |

**Payload Examples**:
- `KILL_SWITCH_ACTIVE`: `{ "reason": "Manual activation" }`

---

### 14. Flatten (4 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `FLATTEN_ATTEMPT` | INFO | ExecutionAdapter | Flatten attempt |
| `FLATTEN_SUCCESS` | INFO | ExecutionAdapter | Flatten success |
| `FLATTEN_FAIL` | ERROR | ExecutionAdapter | Flatten failed |
| `FLATTEN_DRYRUN` | DEBUG | NullExecutionAdapter | Flatten (dryrun) |

**Payload Examples**:
- `FLATTEN_SUCCESS`: `{ "instrument": "ES", "position_closed": true }`

---

### 15. Recovery (11 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `DISCONNECT_FAIL_CLOSED_ENTERED` | ERROR | RobotEngine | Disconnect fail-closed entered |
| `DISCONNECT_RECOVERY_STARTED` | INFO | RobotEngine | Disconnect recovery started |
| `DISCONNECT_RECOVERY_COMPLETE` | INFO | RobotEngine | Disconnect recovery complete |
| `DISCONNECT_RECOVERY_ABORTED` | WARN | RobotEngine | Disconnect recovery aborted |
| `DISCONNECT_RECOVERY_SKIPPED` | INFO | RobotEngine | Disconnect recovery skipped |
| `DISCONNECT_RECOVERY_WAITING_FOR_SYNC` | INFO | RobotEngine | Disconnect recovery waiting for sync |
| `RECOVERY_ACCOUNT_SNAPSHOT` | INFO | RobotEngine | Recovery account snapshot |
| `RECOVERY_ACCOUNT_SNAPSHOT_FAILED` | ERROR | RobotEngine | Recovery account snapshot failed |
| `RECOVERY_ACCOUNT_SNAPSHOT_NULL` | WARN | RobotEngine | Recovery account snapshot null |
| `RECOVERY_CANCELLED_ROBOT_ORDERS` | INFO | RobotEngine | Recovery cancelled robot orders |
| `RECOVERY_CANCELLED_ROBOT_ORDERS_FAILED` | ERROR | RobotEngine | Recovery cancelled robot orders failed |
| `RECOVERY_POSITION_RECONCILED` | INFO | RobotEngine | Recovery position reconciled |
| `RECOVERY_POSITION_UNMATCHED` | WARN | RobotEngine | Recovery position unmatched |
| `RECOVERY_PROTECTIVE_ORDERS_PLACED` | INFO | RobotEngine | Recovery protective orders placed |
| `RECOVERY_STREAM_ORDERS_RECONCILED` | INFO | RobotEngine | Recovery stream orders reconciled |

**Payload Examples**:
- `DISCONNECT_RECOVERY_STARTED`: `{ "recovery_state": "RECONNECTED_RECOVERY_PENDING" }`
- `RECOVERY_POSITION_RECONCILED`: `{ "instrument": "ES", "position_size": 1 }`

---

### 16. Health Monitoring (5 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `CONNECTION_LOST` | ERROR | HealthMonitor | Connection lost |
| `CONNECTION_LOST_SUSTAINED` | ERROR | HealthMonitor | Connection lost (sustained) |
| `CONNECTION_RECOVERED` | INFO | HealthMonitor | Connection recovered |
| `DATA_LOSS_DETECTED` | ERROR | HealthMonitor | Data loss detected |
| `DATA_STALL_RECOVERED` | INFO | HealthMonitor | Data stall recovered |

**Payload Examples**:
- `CONNECTION_LOST`: `{ "duration_seconds": 5.2 }`
- `DATA_LOSS_DETECTED`: `{ "missing_bars": 10 }`

---

### 17. Notifications (2 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `PUSHOVER_NOTIFY_ENQUEUED` | DEBUG | HealthMonitor | Pushover notification enqueued |
| `PUSHOVER_ENDPOINT` | DEBUG | PushoverClient | Pushover endpoint called |

**Payload Examples**:
- `PUSHOVER_NOTIFY_ENQUEUED`: `{ "message": "Connection lost", "priority": "high" }`

---

### 18. Journal (3 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `JOURNAL_WRITTEN` | INFO | StreamStateMachine | Journal written |
| `EXECUTION_JOURNAL_ERROR` | ERROR | ExecutionJournal | Execution journal error |
| `EXECUTION_JOURNAL_READ_ERROR` | ERROR | ExecutionJournal | Execution journal read error |

**Payload Examples**:
- `JOURNAL_WRITTEN`: `{ "trading_date": "2026-01-22", "stream": "ES1", "state": "RANGE_LOCKED" }`

---

### 19. Logging Service (6 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `LOG_BACKPRESSURE_DROP` | ERROR | RobotLoggingService | Log backpressure drop |
| `LOG_WORKER_LOOP_ERROR` | ERROR | RobotLoggingService | Log worker loop error |
| `LOG_WRITE_FAILURE` | ERROR | RobotLoggingService | Log write failure |
| `LOG_HEALTH_ERROR` | ERROR | StreamStateMachine | Log health error |
| `LOG_CONVERSION_ERROR` | ERROR | RobotEngine | Log conversion error |
| `LOGGER_CONVERSION_ERROR` | ERROR | RobotLogger | Logger conversion error |

**Payload Examples**:
- `LOG_BACKPRESSURE_DROP`: `{ "dropped_level": "DEBUG", "dropped_count": 100, "queue_size": 50000 }`
- `LOG_WRITE_FAILURE`: `{ "instrument": "ES", "error": "Disk full" }`

---

### 20. Account Snapshots (4 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `ACCOUNT_SNAPSHOT_DRYRUN` | DEBUG | NullExecutionAdapter | Account snapshot (dryrun) |
| `ACCOUNT_SNAPSHOT_ERROR` | ERROR | ExecutionAdapter | Account snapshot error |
| `ACCOUNT_SNAPSHOT_LIVE_ERROR` | ERROR | ExecutionAdapter | Account snapshot live error |
| `ACCOUNT_SNAPSHOT_LIVE_STUB` | DEBUG | ExecutionAdapter | Account snapshot live stub |

---

### 21. Order Cancellation (6 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `CANCEL_ROBOT_ORDERS_DRYRUN` | DEBUG | NullExecutionAdapter | Cancel robot orders (dryrun) |
| `CANCEL_ROBOT_ORDERS_ERROR` | ERROR | ExecutionAdapter | Cancel robot orders error |
| `CANCEL_ROBOT_ORDERS_LIVE_ERROR` | ERROR | ExecutionAdapter | Cancel robot orders live error |
| `CANCEL_ROBOT_ORDERS_LIVE_STUB` | DEBUG | ExecutionAdapter | Cancel robot orders live stub |
| `CANCEL_ROBOT_ORDERS_MOCK` | DEBUG | ExecutionAdapter | Cancel robot orders mock |
| `ROBOT_ORDERS_CANCELLED` | INFO | ExecutionAdapter | Robot orders cancelled |

---

### 22. DRYRUN Events (5 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `ENTRY_ORDER_DRYRUN` | DEBUG | NullExecutionAdapter | Entry order (dryrun) |
| `STOP_ENTRY_ORDER_DRYRUN` | DEBUG | NullExecutionAdapter | Stop entry order (dryrun) |
| `PROTECTIVE_STOP_DRYRUN` | DEBUG | NullExecutionAdapter | Protective stop (dryrun) |
| `TARGET_ORDER_DRYRUN` | DEBUG | NullExecutionAdapter | Target order (dryrun) |
| `BE_MODIFY_DRYRUN` | DEBUG | NullExecutionAdapter | BE modify (dryrun) |

---

### 23. Tick/Stream State (5 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `TICK_METHOD_ENTERED` | DEBUG | StreamStateMachine | Tick method entered |
| `TICK_METHOD_ENTERED_ERROR` | ERROR | StreamStateMachine | Tick method entered error |
| `TICK_CALLED` | DEBUG | StreamStateMachine | Tick called |
| `TICK_TRACE` | DEBUG | StreamStateMachine | Tick trace |
| `UPDATE_APPLIED` | DEBUG | StreamStateMachine | Update applied |

---

### 24. Other Events (5 events)

| Event | Level | Source | Description |
|-------|-------|--------|-------------|
| `SESSION_START_TIME_SET` | INFO | RobotEngine | Session start time set |
| `GAP_VIOLATIONS_SUMMARY` | WARN | RobotEngine | Gap violations summary |
| `SLOT_GATE_DIAGNOSTIC` | DEBUG | StreamStateMachine | Slot gate diagnostic |
| `OPERATOR_BANNER` | INFO | RobotEngine | Operator banner |
| `INVARIANT_VIOLATION` | ERROR | RobotEngine | Invariant violation |
| `INCIDENT_PERSIST_ERROR` | ERROR | ExecutionAdapter | Incident persist error |

---

## Event Level Summary

- **DEBUG**: 60 events (diagnostic, trace, heartbeat)
- **INFO**: 65 events (normal operations, state changes)
- **WARN**: 20 events (recoverable issues, blocked operations)
- **ERROR**: 14 events (failures, violations, critical errors)

---

## Common Event Patterns

### Order Lifecycle
1. `ORDER_SUBMITTED` → `ORDER_SUBMIT_SUCCESS` → `ORDER_ACKNOWLEDGED` → `EXECUTION_FILLED`

### Range Building
1. `PRE_HYDRATION_BARS_LOADED` → `RANGE_COMPUTE_START` → `RANGE_LOCKED`

### Error Recovery
1. `CONNECTION_LOST` → `DISCONNECT_RECOVERY_STARTED` → `DISCONNECT_RECOVERY_COMPLETE`

---

## See Also

- [LOGGING_SUMMARY.md](LOGGING_SUMMARY.md) - Logging system overview
- [LOGGING_QUICK_REF.md](LOGGING_QUICK_REF.md) - Quick reference
- [LOGGING_ASSESSMENT_REPORT.md](LOGGING_ASSESSMENT_REPORT.md) - Best practices assessment
