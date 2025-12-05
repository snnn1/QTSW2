# How the Orchestrator Works with the Dashboard

## Overview

The **Pipeline Orchestrator** is the central brain of your data pipeline system. It runs inside the FastAPI backend and coordinates everything: scheduling, execution, monitoring, and real-time updates to the dashboard.

---

## What the Orchestrator Does

### 1. **Scheduling** (Every 15 Minutes)
- **Runs automatically** at :00, :15, :30, :45 of every hour
- Calculates the next 15-minute mark
- Triggers pipeline runs automatically
- Skips if pipeline is already running

### 2. **Pipeline Execution**
- Manages the complete pipeline: Translator → Analyzer → Merger
- Tracks state transitions (idle → starting → running → success/failed)
- Handles retries if a stage fails
- Prevents overlapping runs (locking mechanism)

### 3. **Event Broadcasting**
- Emits events for every action (stage start, progress, completion, errors)
- Writes events to JSONL files for audit trail
- Broadcasts events in real-time via WebSocket to dashboard

### 4. **Health Monitoring**
- Watchdog monitors pipeline health
- Detects hung/stuck runs
- Automatically transitions failed runs to proper state
- Emits heartbeat events

---

## How the Scheduler Works (Every 15 Minutes)

### Step-by-Step Flow

```
1. Backend starts → Orchestrator initializes
   ↓
2. Scheduler starts as background task
   ↓
3. Scheduler calculates next 15-minute mark:
   - Current time: 12:07 → Next: 12:15
   - Current time: 12:23 → Next: 12:30
   - Current time: 12:45 → Next: 13:00
   ↓
4. Waits until that time arrives
   ↓
5. Checks if pipeline is already running
   - If YES → Skip this run, wait for next 15-minute mark
   - If NO → Continue
   ↓
6. Triggers pipeline: orchestrator.start_pipeline(manual=False)
   ↓
7. Pipeline runs through stages:
   - Translator (processes raw CSV files)
   - Analyzer (analyzes processed data)
   - Merger (combines results)
   ↓
8. After pipeline completes, calculates next 15-minute mark
   ↓
9. Repeat from step 4
```

### Code Logic

```python
# In scheduler.py - _calculate_next_run_time()

Current time: 12:07
├─ Current minute: 7
├─ 7 < 15? YES
└─ Next minute: 15
Result: Next run at 12:15

Current time: 12:23
├─ Current minute: 23
├─ 23 < 30? YES
└─ Next minute: 30
Result: Next run at 12:30

Current time: 12:45
├─ Current minute: 45
├─ 45 < 45? NO
└─ Next minute: 0 (next hour)
Result: Next run at 13:00
```

---

## How It Integrates with the Dashboard

### Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Dashboard Frontend                    │
│                  (React - Port 5173)                     │
│                                                          │
│  • PipelineControls (start/stop buttons)                │
│  • MetricsPanel (status, stages, metrics)                │
│  • EventsLog (real-time event stream)                    │
│  • ExportPanel (export information)                      │
└──────────────────┬──────────────────────────────────────┘
                   │
                   │ HTTP API + WebSocket
                   │
┌──────────────────▼──────────────────────────────────────┐
│              FastAPI Backend (Port 8000)                │
│                                                          │
│  ┌──────────────────────────────────────────────────┐   │
│  │         Pipeline Orchestrator                    │   │
│  │                                                   │   │
│  │  ┌──────────┐  ┌──────────┐  ┌──────────┐      │   │
│  │  │Scheduler │  │  Runner  │  │ Watchdog │      │   │
│  │  │(15 min)  │  │(executes)│  │(monitors)│      │   │
│  │  └────┬─────┘  └────┬─────┘  └────┬─────┘      │   │
│  │       │            │              │             │   │
│  │       └────────────┴──────────────┘             │   │
│  │                    │                            │   │
│  │            ┌───────▼────────┐                   │   │
│  │            │   EventBus     │                   │   │
│  │            │ (broadcasts)   │                   │   │
│  │            └───────┬────────┘                   │   │
│  └────────────────────┼────────────────────────────┘   │
│                       │                                  │
│  ┌────────────────────▼────────────────────────────┐   │
│  │         API Endpoints                            │   │
│  │  • GET  /api/pipeline/status                     │   │
│  │  • POST /api/pipeline/start                      │   │
│  │  • GET  /api/pipeline/snapshot                   │   │
│  │  • WS   /ws/events (WebSocket)                    │   │
│  └──────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────┘
                   │
                   │ Executes
                   │
┌──────────────────▼──────────────────────────────────────┐
│              Pipeline Services                           │
│  • TranslatorService (processes raw CSV)                 │
│  • AnalyzerService (analyzes data)                        │
│  • MergerService (combines results)                       │
└──────────────────────────────────────────────────────────┘
```

---

## Data Flow: How Events Reach the Dashboard

### 1. **Pipeline Starts** (Scheduled or Manual)

```
Scheduler triggers → orchestrator.start_pipeline()
  ↓
State changes to "starting"
  ↓
EventBus.publish({
  "run_id": "abc-123",
  "stage": "pipeline",
  "event": "start",
  "timestamp": "2025-12-05T12:15:00",
  "msg": "Pipeline started"
})
  ↓
EventBus does TWO things:
  1. Writes to JSONL file: automation/logs/events/pipeline_abc-123.jsonl
  2. Broadcasts to all WebSocket subscribers
  ↓
Dashboard WebSocket receives event
  ↓
Frontend updates UI in real-time
```

### 2. **Stage Executes**

```
Runner executes TranslatorService
  ↓
Translator processes files
  ↓
EventBus.publish({
  "run_id": "abc-123",
  "stage": "translator",
  "event": "start",
  "msg": "Translating raw files"
})
  ↓
Translator completes
  ↓
EventBus.publish({
  "run_id": "abc-123",
  "stage": "translator",
  "event": "success",
  "data": {"files_processed": 5}
})
  ↓
Dashboard shows: "Translator: ✓ Complete (5 files)"
```

### 3. **Real-Time Updates**

```
Every event is:
  ✓ Written to JSONL file (permanent record)
  ✓ Broadcast via WebSocket (real-time)
  ✓ Available via API /api/pipeline/snapshot (on-demand)
```

---

## Dashboard Frontend Integration

### 1. **Initial Connection**

When dashboard loads:

```javascript
// Frontend connects to WebSocket
const ws = new WebSocket('ws://localhost:8000/ws/events')

// Backend sends initial snapshot
ws.onmessage = (event) => {
  const data = JSON.parse(event.data)
  if (data.type === 'snapshot') {
    // Update UI with current state
    setPipelineStatus(data.data.status)
    setEvents(data.data.events)
  }
}
```

### 2. **Real-Time Event Stream**

```javascript
// Every event from orchestrator
ws.onmessage = (event) => {
  const eventData = JSON.parse(event.data)
  
  // Update UI based on event type
  switch(eventData.stage) {
    case 'translator':
      updateTranslatorStatus(eventData)
      break
    case 'analyzer':
      updateAnalyzerStatus(eventData)
      break
    case 'merger':
      updateMergerStatus(eventData)
      break
  }
  
  // Add to events log
  addEventToLog(eventData)
}
```

### 3. **User Actions**

```javascript
// User clicks "Start Pipeline"
startPipeline() {
  fetch('http://localhost:8000/api/pipeline/start', {
    method: 'POST'
  })
  // Orchestrator starts pipeline
  // Events flow back via WebSocket
  // UI updates automatically
}
```

---

## Complete Example Flow

### Scenario: Scheduled Run at 12:15

```
12:15:00 - Scheduler detects it's time
  ↓
12:15:00 - Orchestrator checks: pipeline idle? YES
  ↓
12:15:00 - EventBus.publish("Pipeline started")
  ↓
12:15:00 - Dashboard receives: "Pipeline: Starting"
  ↓
12:15:01 - Runner starts TranslatorService
  ↓
12:15:01 - EventBus.publish("Translator started")
  ↓
12:15:01 - Dashboard shows: "Translator: Running..."
  ↓
12:15:30 - Translator completes (5 files processed)
  ↓
12:15:30 - EventBus.publish("Translator success", {files: 5})
  ↓
12:15:30 - Dashboard shows: "Translator: ✓ Complete (5 files)"
  ↓
12:15:31 - Runner starts AnalyzerService
  ↓
12:15:31 - EventBus.publish("Analyzer started")
  ↓
12:15:31 - Dashboard shows: "Analyzer: Running..."
  ↓
12:16:45 - Analyzer completes
  ↓
12:16:45 - EventBus.publish("Analyzer success")
  ↓
12:16:45 - Dashboard shows: "Analyzer: ✓ Complete"
  ↓
12:16:46 - Runner starts MergerService
  ↓
12:16:46 - EventBus.publish("Merger started")
  ↓
12:16:46 - Dashboard shows: "Merger: Running..."
  ↓
12:17:00 - Merger completes
  ↓
12:17:00 - EventBus.publish("Pipeline success")
  ↓
12:17:00 - Dashboard shows: "Pipeline: ✓ Complete"
  ↓
12:17:00 - State changes to "success"
  ↓
12:17:00 - Scheduler calculates next run: 12:30
  ↓
12:17:00 - Dashboard shows: "Next run: 12:30 (13 minutes)"
```

---

## Key Components

### 1. **EventBus**
- Central event broadcaster
- Writes to JSONL files
- Broadcasts to WebSocket subscribers
- Maintains ring buffer of recent events

### 2. **StateManager**
- Tracks pipeline state (idle, starting, running, success, failed)
- Validates state transitions
- Prevents invalid operations

### 3. **LockManager**
- Prevents overlapping runs
- Uses file-based locking
- Handles stale locks

### 4. **PipelineRunner**
- Executes pipeline stages
- Handles retries
- Updates state
- Emits events

### 5. **Scheduler**
- Runs every 15 minutes
- Calculates next run time
- Triggers pipeline automatically

### 6. **Watchdog**
- Monitors pipeline health
- Detects hung runs
- Emits heartbeat events

---

## Benefits

✅ **Automatic**: Runs every 15 minutes without manual intervention  
✅ **Real-Time**: Dashboard updates instantly via WebSocket  
✅ **Reliable**: Prevents overlapping runs, handles errors  
✅ **Auditable**: All events written to JSONL files  
✅ **Observable**: Full visibility into pipeline state and progress  
✅ **Resilient**: Watchdog detects and handles issues  

---

## Summary

The orchestrator is the **single source of truth** for your pipeline. It:
- Runs automatically every 15 minutes
- Executes the pipeline (Translator → Analyzer → Merger)
- Emits events for everything that happens
- Broadcasts events to the dashboard in real-time
- Maintains state and prevents conflicts
- Monitors health and handles errors

The dashboard is a **real-time view** into the orchestrator:
- Connects via WebSocket for live updates
- Shows current status, stages, and metrics
- Displays events as they happen
- Allows manual control (start/stop/reset)

Everything is **event-driven** - the orchestrator emits events, and the dashboard reacts to them in real-time.

