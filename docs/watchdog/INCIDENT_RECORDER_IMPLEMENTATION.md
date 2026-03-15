# Watchdog Incident Recorder — Implementation Summary

## Overview

Adds an incident tracking layer to the watchdog that records operational failures as structured start/end events with duration. The recorder is purely observational and does not modify existing monitoring logic.

---

## 1. New Module: `incident_recorder.py`

**Location:** `modules/watchdog/incident_recorder.py`

### Purpose

Track incident start → incident end → duration. Write completed incidents to `data/watchdog/incidents.jsonl` as JSON lines.

### Incident Types and Event Mapping

| Incident Type              | Start Events                          | Recovery Events                                           |
|---------------------------|----------------------------------------|-----------------------------------------------------------|
| CONNECTION_LOST           | CONNECTION_LOST, CONNECTION_LOST_SUSTAINED | CONNECTION_RECOVERED, CONNECTION_RECOVERED_NOTIFICATION    |
| ENGINE_STALLED            | ENGINE_TICK_STALL_DETECTED             | ENGINE_ALIVE, ENGINE_TICK_STALL_RECOVERED, ENGINE_TICK_CALLSITE, ENGINE_TIMER_HEARTBEAT |
| DATA_STALL                | DATA_LOSS_DETECTED, DATA_STALL_DETECTED | DATA_STALL_RECOVERED                                      |
| FORCED_FLATTEN            | FORCED_FLATTEN_TRIGGERED               | FORCED_FLATTEN_POSITION_CLOSED, SESSION_FORCED_FLATTENED  |
| RECONCILIATION_QTY_MISMATCH | RECONCILIATION_QTY_MISMATCH           | RECONCILIATION_PASS_SUMMARY                               |

### Internal State

```python
active_incidents = {
    incident_type: {
        "incident_id": "uuid",
        "start_ts": datetime,
        "instruments": set
    }
}
```

### Output Format (incidents.jsonl)

```json
{
  "incident_id": "uuid",
  "type": "CONNECTION_LOST",
  "start_ts": "2026-03-14T03:15:02Z",
  "end_ts": "2026-03-14T03:17:41Z",
  "duration_sec": 159,
  "instruments": ["MES", "MNQ"]
}
```

### Instrument Collection

Extracts instruments from event payload keys: `execution_instrument_full_name`, `instrument`, `execution_instrument_key`. Merges with recovery event instruments when closing the incident.

### Safety

- Never throws exceptions (all logic wrapped in try/except)
- Never blocks watchdog processing
- Fail silently on disk write errors
- Uses append-only writes

### Helper API

- `get_recent_incidents(limit=50)` — Read last N incidents from file
- `get_active_incidents()` — Return currently active incidents (not yet recovered)

---

## 2. Config Changes

**File:** `modules/watchdog/config.py`

Added:

```python
INCIDENT_LOG_DIR = QTSW2_ROOT / "data" / "watchdog"
INCIDENTS_FILE = INCIDENT_LOG_DIR / "incidents.jsonl"
```

---

## 3. Integration

**File:** `modules/watchdog/event_processor.py`

- Import `incident_recorder_process_event`
- At the start of `EventProcessor.process_event()`, call `incident_recorder_process_event(event)` wrapped in try/except
- Recorder observes every event before any other processing; does not modify event or state

---

## 4. Files Changed

| File | Change |
|------|--------|
| `modules/watchdog/incident_recorder.py` | **New** — Incident recorder module |
| `modules/watchdog/config.py` | Added INCIDENT_LOG_DIR, INCIDENTS_FILE |
| `modules/watchdog/event_processor.py` | Call incident recorder at start of process_event |

---

## 5. Constraints Respected

- **Not modified:** watchdog state machine, engine liveness logic, connection logic, stall detection logic
- **Recorder is purely observational:** only observes events, does not change any existing state
