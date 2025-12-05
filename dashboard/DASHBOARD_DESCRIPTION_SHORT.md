# Pipeline Dashboard - Short Description for GPT

## What It Is

A real-time web dashboard for monitoring and controlling a quantitative trading data pipeline. Built with **FastAPI backend** (Python) and **React frontend** (JavaScript/JSX).

## Architecture

- **Backend**: FastAPI on port 8000, serves REST API and WebSocket endpoints
- **Frontend**: React + Vite on port 5173/5174, Tailwind CSS for styling
- **Communication**: REST API for control, WebSocket for real-time event streaming

## Key Features

1. **Real-Time Monitoring**: WebSocket streams pipeline events as they happen
2. **Pipeline Control**: Start/stop pipeline, run individual stages (translator, analyzer, merger)
3. **Live Metrics**: Processing rates, file counts, stage progress
4. **Event Log**: Real-time scrollable event log with filtering
5. **Schedule Management**: View/update pipeline schedule
6. **Application Launcher**: Start/stop Streamlit apps (translator, analyzer, etc.)

## How It Works

- Pipeline writes events to JSONL files: `automation/logs/events/pipeline_{run_id}.jsonl`
- Dashboard backend tails these files and broadcasts via WebSocket
- Frontend connects to WebSocket and receives events in real-time
- Frontend also polls REST API every 2 seconds for status updates
- All events include: run_id, stage, event type, timestamp, message, data

## Main Components

**Backend** (`dashboard/backend/`):
- `main.py`: FastAPI app, router registration, CORS config
- `routers/pipeline.py`: Pipeline control endpoints
- `routers/websocket.py`: WebSocket connection management, file tailing, event broadcasting
- `routers/schedule.py`: Schedule management
- `routers/apps.py`: Application launcher
- `routers/metrics.py`: Metrics calculation

**Frontend** (`dashboard/frontend/src/`):
- `App.jsx`: Root component, state management, component orchestration
- `hooks/usePipelineState.js`: Main state hook, WebSocket management, event processing
- `components/pipeline/`: PipelineControls, MetricsPanel, EventsLog, ExportPanel
- `services/websocketManager.js`: WebSocket connection with auto-reconnect
- `services/pipelineManager.js`: REST API client

## Event Format

JSONL format (one JSON per line):
```json
{
  "run_id": "uuid",
  "stage": "translator|analyzer|merger|pipeline",
  "event": "start|success|failure|log|metric|error",
  "timestamp": "2025-04-12T20:15:00-05:00",
  "msg": "Optional message",
  "data": {"key": "value"}
}
```

## API Endpoints

- `GET /api/pipeline/status` - Current pipeline status
- `POST /api/pipeline/start` - Start pipeline
- `POST /api/pipeline/stage/{stage}` - Run individual stage
- `WS /ws/events/{run_id}` - WebSocket event stream
- `GET /api/schedule` - Get schedule
- `POST /api/schedule` - Update schedule

## Integration

- Dashboard reads event logs that pipeline writes (file-based, no direct coupling)
- Dashboard can start/stop scheduler process
- Dashboard displays real-time metrics calculated from events
- All times in Chicago timezone (America/Chicago)

## Tech Stack

**Backend**: FastAPI, Uvicorn, Pydantic, Pandas, asyncio, WebSocket
**Frontend**: React 18+, Vite, Tailwind CSS, WebSocket API

