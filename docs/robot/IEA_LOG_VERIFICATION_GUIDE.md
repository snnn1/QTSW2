# IEA Log Verification Guide

How to verify the new IEA implementations are working correctly by checking logs.

## Log Locations

| Log File | Contents |
|----------|----------|
| `logs/robot/robot_ENGINE.jsonl` | ENGINE events (IEA binding, heartbeat, timeout, overflow, worker error) |
| `logs/robot/robot_<instrument>.jsonl` | Per-instrument events (execution updates, order submission, FailClosed) |

Project root is typically `QTSW2` or set via `QTSW2_PROJECT_ROOT`.

---

## IEA Events to Look For

### 1. IEA Binding (startup)
**Event:** `IEA_BINDING`  
**When:** Strategy starts with IEA enabled  
**Verifies:** IEA is created and bound to adapter

```json
{"event":"IEA_BINDING","data":{"payload":{"account_name":"...","execution_instrument_key":"MNQ","iea_instance_id":1,"note":"IEA bound for execution routing"}}}
```

### 2. IEA Heartbeat (every 60s when queue active)
**Event:** `IEA_HEARTBEAT`  
**When:** Worker processes work; emitted every 60 seconds  
**Verifies:** Worker is running; shows `enqueue_sequence`, `last_processed_sequence`

```json
{"event":"IEA_HEARTBEAT","data":{"payload":{"iea_instance_id":1,"execution_instrument_key":"MNQ","queue_depth":0,"enqueue_sequence":42,"last_processed_sequence":42}}}
```

### 3. Execution Updates Routed (when fills occur)
**Event:** `IEA_EXEC_UPDATE_ROUTED`  
**When:** Execution update enqueued to IEA (rate-limited to once per second)  
**Verifies:** Gap 2: Execution updates go through IEA queue

### 4. Entry Aggregation (when multiple streams at same price)
**Event:** `ENTRY_AGGREGATION_ATTEMPT`, `ENTRY_AGGREGATION_SUCCESS`  
**Verifies:** Gap 1: Aggregation path runs on worker

### 5. Single-Order Fallback (when no aggregation)
**Event:** `ORDER_SUBMIT_SUCCESS`, `ORDER_CREATED_STOPMARKET`  
**Verifies:** Gap 1: Single-order path also runs on worker (no off-worker fallback)

### 6. FailClosed with IEA Context
**Event:** `PROTECTIVE_ORDER_REJECTED_FLATTENED`, `PROTECTIVE_QUANTITY_MISMATCH_FAIL_CLOSE`, etc.  
**Verifies:** Gap 3: CRITICAL logs include `iea_instance_id` and `execution_instrument_key` in payload

### 7. EnqueueAndWait Timeout (failure path)
**Event:** `IEA_ENQUEUE_AND_WAIT_TIMEOUT`  
**When:** Worker doesn't complete within 5s  
**Verifies:** Gap 4 & 5: Includes `enqueue_sequence`, `last_processed_sequence`; instrument blocked

```json
{"event":"IEA_ENQUEUE_AND_WAIT_TIMEOUT","data":{"payload":{"iea_instance_id":1,"execution_instrument_key":"MNQ","timeout_ms":5000,"enqueue_sequence":100,"last_processed_sequence":95,"policy":"IEA_FAIL_CLOSED_BLOCK_INSTRUMENT"}}}
```

### 8. Queue Overflow (failure path)
**Event:** `IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW`  
**When:** Queue depth > 500  
**Verifies:** Gap 4 & 5: Same as timeout; instrument blocked

### 9. Instrument Blocked (after timeout/overflow)
**Event:** `IEA_ENQUEUE_REJECTED_INSTRUMENT_BLOCKED`  
**When:** New EnqueueAndWait called after prior timeout/overflow  
**Verifies:** Gap 5: No new work accepted until restart

### 10. Health Monitor Report (block callback)
**Event:** `IEA_ENQUEUE_FAILURE_INSTRUMENT_BLOCKED` (via HealthMonitor)  
**When:** blockInstrumentCallback invoked  
**Verifies:** Gap 5: Engine notified; instrument frozen

---

## Quick Commands

### Search for IEA events in ENGINE log
```powershell
Select-String -Path "logs\robot\robot_ENGINE.jsonl" -Pattern "IEA_|iea_instance_id|enqueue_sequence|last_processed" | Select-Object -Last 50
```

### Search for IEA context in per-instrument logs
```powershell
Get-ChildItem logs\robot\robot_*.jsonl | ForEach-Object { Select-String -Path $_.FullName -Pattern "iea_instance_id" | Select-Object -Last 5 }
```

### Tail ENGINE log (live)
```powershell
Get-Content logs\robot\robot_ENGINE.jsonl -Wait -Tail 20
```

### Count IEA events by type
```powershell
Get-Content logs\robot\robot_ENGINE.jsonl | Select-String "IEA_" | ForEach-Object { ($_ -match '"event":"([^"]+)"') | Out-Null; $matches[1] } | Group-Object | Sort-Object Count -Descending
```

---

## Verification Checklist

| Check | Event(s) | Pass Criteria |
|-------|----------|---------------|
| IEA enabled | `IEA_BINDING` | Present at strategy start |
| Worker running | `IEA_HEARTBEAT` | Present when queue has activity |
| Execution updates queued | `IEA_EXEC_UPDATE_ROUTED` | Present when fills occur |
| Entry on worker | `ORDER_SUBMIT_SUCCESS` after `IEA_BINDING` | No off-worker fallback |
| Order updates queued | `ORDER_ACKNOWLEDGED`, `ORDER_REJECTED` in instrument log | Via ProcessOrderUpdate |
| FailClosed has IEA context | Any CRITICAL event | `iea_instance_id` in payload |
| Timeout/overflow have sequences | `IEA_ENQUEUE_AND_WAIT_TIMEOUT` | `enqueue_sequence`, `last_processed_sequence` present |
| Block policy | `IEA_ENQUEUE_REJECTED_INSTRUMENT_BLOCKED` | After timeout, new work rejected |

---

## Notes

- **IEA not appearing?** Ensure `configs/execution_policy.json` has `"use_instrument_execution_authority": true`.
- **IEA_HEARTBEAT at DEBUG?** If `min_log_level` in `configs/robot/logging.json` is INFO or higher, DEBUG events may be filtered. Set to DEBUG for full visibility.
- **No IEA events in recent logs?** Run the strategy with IEA enabled; trigger an entry (e.g. RANGE_LOCKED → breakout) to see execution flow.
