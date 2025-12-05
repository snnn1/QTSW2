# Pipeline Dashboard - Full Description

## Overview

The Pipeline Dashboard is a real-time web application for monitoring and controlling a quantitative trading data pipeline. It provides a live view of pipeline execution, metrics, events, and allows manual control of pipeline stages. The dashboard consists of a **FastAPI backend** (Python) and a **React frontend** (JavaScript/JSX) that communicate via REST API and WebSocket connections.

## Architecture

### Backend (FastAPI)
- **Framework**: FastAPI (Python web framework)
- **Location**: `dashboard/backend/`
- **Port**: 8000 (default)
- **Main File**: `dashboard/backend/main.py`
- **Routers**: Modular API endpoints in `dashboard/backend/routers/`

### Frontend (React)
- **Framework**: React 18+ with Vite
- **Location**: `dashboard/frontend/`
- **Port**: 5173 or 5174 (Vite dev server)
- **Styling**: Tailwind CSS
- **Main File**: `dashboard/frontend/src/App.jsx`

### Communication
- **REST API**: HTTP endpoints for status, control, configuration
- **WebSocket**: Real-time event streaming (`/ws/events/{run_id}`)
- **CORS**: Configured for localhost and network access

## Technologies

### Backend Stack
- **FastAPI**: Modern Python web framework
- **Uvicorn**: ASGI server
- **Pydantic**: Data validation and models
- **Pandas/NumPy**: Data processing (for metrics)
- **asyncio**: Asynchronous operations
- **WebSocket**: Real-time bidirectional communication

### Frontend Stack
- **React**: UI library
- **Vite**: Build tool and dev server
- **Tailwind CSS**: Utility-first CSS framework
- **WebSocket API**: Native browser WebSocket for real-time updates

## Core Features

### 1. Real-Time Pipeline Monitoring
- **Live Event Stream**: WebSocket connection streams events as they happen
- **Stage Status**: Shows current stage (translator, analyzer, merger)
- **Progress Tracking**: Real-time progress for each stage
- **Status Indicators**: Visual status badges (running, success, failure, waiting)

### 2. Pipeline Control
- **Start Pipeline**: Manually trigger full pipeline execution
- **Run Individual Stages**: Run translator, analyzer, or merger independently
- **Reset Pipeline**: Clear pipeline state and reset to initial state
- **Stop Operations**: Ability to stop running operations

### 3. Metrics Display
- **Processing Rates**: Rows per minute, files processed
- **Stage Metrics**: File counts, processing times, success rates
- **Merger Information**: Data merger status and results
- **Export Information**: Export status and file counts

### 4. Event Logging
- **Live Events Panel**: Real-time scrollable event log
- **Event Filtering**: Filter by stage, event type, or search
- **Event Types**: start, success, failure, log, metric, error
- **Timestamps**: All events include Chicago timezone timestamps

### 5. Schedule Management
- **View Schedule**: Display current pipeline schedule
- **Update Schedule**: Change scheduled run times
- **Next Run Info**: Shows when next scheduled run will occur
- **Schedule Persistence**: Schedule saved to JSON config file

### 6. Application Launcher
- **Launch Apps**: Start Streamlit applications (translator, analyzer, sequential processor)
- **App Status**: Check if applications are running
- **App Management**: Start/stop external applications

### 7. Master Matrix Integration
- **Matrix Data**: Access to master matrix calculations
- **Matrix Endpoints**: API endpoints for matrix data retrieval
- **Matrix Visualization**: (If implemented in frontend)

## Key Components

### Backend Components

#### Main Application (`main.py`)
- FastAPI app initialization
- CORS middleware configuration
- Router registration
- Lifespan management (startup/shutdown)
- Scheduler process management
- Logging configuration

#### Routers (`routers/`)
- **pipeline.py**: Pipeline control endpoints (start, status, stages)
- **schedule.py**: Schedule management endpoints
- **websocket.py**: WebSocket connection management and event streaming
- **apps.py**: Application launcher endpoints
- **metrics.py**: Metrics calculation and retrieval
- **matrix.py**: Master matrix data endpoints

#### WebSocket Manager (`routers/websocket.py`)
- Connection management (connect/disconnect)
- File tailing for event logs
- Real-time event broadcasting
- Connection health monitoring
- Automatic reconnection handling

### Frontend Components

#### Main App (`App.jsx`)
- Root component
- State management via `usePipelineState` hook
- Chicago time display
- Backend connection monitoring
- Alert system
- Component orchestration

#### Pipeline Components (`components/pipeline/`)
- **PipelineControls.jsx**: Start/stop/reset controls, schedule display
- **MetricsPanel.jsx**: Metrics display, stage information, manual stage triggers
- **EventsLog.jsx**: Real-time event log with filtering
- **ExportPanel.jsx**: Export information display
- **ProcessingRateCard.jsx**: Processing rate visualization
- **NextRunInfo.jsx**: Next scheduled run information

#### UI Components (`components/ui/`)
- **MetricCard.jsx**: Reusable metric display card
- **ProgressBar.jsx**: Progress bar component
- **StatusBadge.jsx**: Status indicator badge
- **TimeDisplay.jsx**: Time display component

#### Hooks (`hooks/`)
- **usePipelineState.js**: Main state management hook
  - Pipeline status polling
  - WebSocket connection management
  - Event processing and formatting
  - Stage control functions
  - Alert management

#### Services (`services/`)
- **pipelineManager.js**: REST API client for pipeline operations
- **websocketManager.js**: WebSocket connection management
- **appsManager.js**: Application launcher API client

#### Utils (`utils/`)
- **timeUtils.js**: Time formatting and parsing utilities
- **eventFilter.js**: Event filtering logic

## Data Flow

### Pipeline Execution Flow

1. **Pipeline Starts** (manual or scheduled)
   - Backend creates new run_id (UUID)
   - Pipeline writes events to `automation/logs/events/pipeline_{run_id}.jsonl`
   - Frontend detects new run via status polling

2. **Event Streaming**
   - Frontend connects to WebSocket: `ws://localhost:8000/ws/events/{run_id}`
   - Backend tails the event log file
   - New events are broadcast to connected clients
   - Frontend receives events and updates UI

3. **Status Updates**
   - Frontend polls `/api/pipeline/status` every 2 seconds
   - Backend reads event logs and calculates current status
   - Status includes: current stage, progress, file counts, metrics

4. **Metrics Calculation**
   - Backend calculates metrics from event logs
   - Metrics include: rows per minute, files processed, processing rates
   - Metrics updated in real-time as events arrive

### Event Format

Events are JSONL (JSON Lines) format:
```json
{
  "run_id": "uuid-string",
  "stage": "translator|analyzer|merger|pipeline",
  "event": "start|success|failure|log|metric|error",
  "timestamp": "2025-04-12T20:15:00-05:00",
  "msg": "Optional message",
  "data": {
    "key": "value",
    ...
  }
}
```

## API Endpoints

### Pipeline Endpoints
- `GET /api/pipeline/status` - Get current pipeline status
- `POST /api/pipeline/start` - Start pipeline manually
- `POST /api/pipeline/stage/{stage}` - Run individual stage
- `POST /api/pipeline/merger` - Run data merger
- `POST /api/pipeline/reset` - Reset pipeline state

### Schedule Endpoints
- `GET /api/schedule` - Get current schedule
- `POST /api/schedule` - Update schedule
- `GET /api/scheduler/status` - Get scheduler process status
- `POST /api/scheduler/start` - Start scheduler
- `POST /api/scheduler/stop` - Stop scheduler

### Metrics Endpoints
- `GET /api/metrics` - Get pipeline metrics
- `GET /api/metrics/{run_id}` - Get metrics for specific run

### Application Endpoints
- `GET /api/apps` - List available applications
- `POST /api/apps/{app_name}/start` - Start application
- `POST /api/apps/{app_name}/stop` - Stop application
- `GET /api/apps/{app_name}/status` - Get application status

### Matrix Endpoints
- `GET /api/matrix` - Get master matrix data
- `GET /api/matrix/{instrument}` - Get matrix for specific instrument

### WebSocket Endpoints
- `WS /ws/events` - General event stream
- `WS /ws/events/{run_id}` - Event stream for specific run

## File Structure

```
dashboard/
├── backend/
│   ├── main.py                 # Main FastAPI application
│   ├── config.py               # Backend configuration
│   ├── models.py               # Pydantic models
│   └── routers/
│       ├── pipeline.py         # Pipeline control endpoints
│       ├── schedule.py         # Schedule management
│       ├── websocket.py        # WebSocket event streaming
│       ├── apps.py             # Application launcher
│       ├── metrics.py          # Metrics calculation
│       └── matrix.py           # Master matrix endpoints
│
├── frontend/
│   ├── src/
│   │   ├── App.jsx             # Main React component
│   │   ├── main.jsx            # React entry point
│   │   ├── components/
│   │   │   ├── pipeline/       # Pipeline-specific components
│   │   │   └── ui/             # Reusable UI components
│   │   ├── hooks/
│   │   │   └── usePipelineState.js  # Main state hook
│   │   ├── services/           # API clients
│   │   └── utils/              # Utility functions
│   ├── package.json            # Dependencies
│   ├── vite.config.js          # Vite configuration
│   └── tailwind.config.js      # Tailwind CSS config
│
└── [documentation files]
```

## Integration with Pipeline

### Event Log Files
- **Location**: `automation/logs/events/pipeline_{run_id}.jsonl`
- **Format**: JSONL (one JSON object per line)
- **Naming**: `pipeline_{uuid}.jsonl`
- **Content**: All pipeline events (start, progress, success, failure, metrics)

### Pipeline Runner
- **Location**: `automation/pipeline_runner.py`
- **Integration**: Pipeline writes events that dashboard reads
- **No Direct Coupling**: Dashboard reads files, doesn't control pipeline directly

### Scheduler Integration
- **Old Scheduler**: `automation/daily_data_pipeline_scheduler.py` (can be started by dashboard)
- **Simple Scheduler**: `automation/simple_scheduler.py` (runs independently)
- **Dashboard**: Can start/stop scheduler, view schedule, but scheduler runs independently

## How It Works

### Startup Sequence

1. **Backend Starts** (`python -m dashboard.backend.main` or `uvicorn dashboard.backend.main:app`)
   - FastAPI app initializes
   - Routers registered
   - CORS middleware configured
   - Optional: Auto-starts scheduler process

2. **Frontend Starts** (`npm run dev` in `dashboard/frontend/`)
   - Vite dev server starts
   - React app loads
   - Connects to backend API
   - Polls for pipeline status

3. **WebSocket Connection**
   - Frontend detects active pipeline run
   - Connects to WebSocket endpoint with run_id
   - Backend starts tailing event log file
   - Events stream to frontend in real-time

### Real-Time Updates

1. **Pipeline writes event** → `pipeline_{run_id}.jsonl`
2. **Backend WebSocket tail** → Detects new line
3. **Backend parses JSON** → Validates event format
4. **Backend broadcasts** → Sends to all connected clients
5. **Frontend receives** → Updates state and UI
6. **UI re-renders** → Shows new event/log/metric

### Status Polling

- Frontend polls `/api/pipeline/status` every 2 seconds
- Backend reads event log files to determine current status
- Status includes: stage, progress, file counts, metrics
- Used as fallback if WebSocket disconnects

## Configuration

### Backend Configuration
- **CORS Origins**: Configured in `main.py` (localhost:5173, 5174, 3000, network IPs)
- **Event Logs Directory**: `automation/logs/events/`
- **Schedule Config**: `automation/schedule_config.json`
- **Log File**: `logs/backend_debug.log`

### Frontend Configuration
- **API Base URL**: `http://localhost:8000` (hardcoded in services)
- **WebSocket URL**: `ws://localhost:8000` (hardcoded in websocketManager)
- **Poll Interval**: 2 seconds (in usePipelineState hook)
- **WebSocket Reconnect**: Automatic with exponential backoff

## Key Features Details

### Event Filtering
- Filter by stage (translator, analyzer, merger)
- Filter by event type (start, success, failure, log, metric)
- Search by text content
- Real-time filtering as events arrive

### Error Handling
- **Error Boundary**: React ErrorBoundary catches component errors
- **Connection Monitoring**: Shows warning if backend disconnected
- **WebSocket Reconnection**: Automatic reconnection with backoff
- **Graceful Degradation**: Falls back to polling if WebSocket fails

### Time Display
- **Chicago Time**: All times displayed in America/Chicago timezone
- **Real-Time Clock**: Updates every second
- **Event Timestamps**: Parsed and displayed in Chicago time

### Alert System
- **Success Alerts**: Green alerts for successful operations
- **Error Alerts**: Red alerts for errors
- **Auto-Dismiss**: Alerts can be manually dismissed
- **Non-Blocking**: Alerts don't block UI interaction

## Dependencies

### Backend
- `fastapi`: Web framework
- `uvicorn`: ASGI server
- `pydantic`: Data validation
- `pandas`: Data processing
- `numpy`: Numerical operations

### Frontend
- `react`: UI library
- `react-dom`: React DOM bindings
- `vite`: Build tool
- `tailwindcss`: CSS framework
- `autoprefixer`: CSS post-processing

## Usage

### Starting the Dashboard

**Backend:**
```bash
cd dashboard/backend
python -m uvicorn main:app --reload
# Or use: dashboard/START_BACKEND.bat
```

**Frontend:**
```bash
cd dashboard/frontend
npm run dev
# Or use: dashboard/frontend/START_FRONTEND.bat
```

### Accessing the Dashboard
- **URL**: `http://localhost:5173` (or 5174)
- **Backend API**: `http://localhost:8000`
- **API Docs**: `http://localhost:8000/docs` (Swagger UI)

## Summary

The Pipeline Dashboard is a **real-time monitoring and control system** for a quantitative trading data pipeline. It provides:

- ✅ **Real-time event streaming** via WebSocket
- ✅ **Live metrics and status** updates
- ✅ **Manual pipeline control** (start, stop, run stages)
- ✅ **Schedule management** (view, update schedule)
- ✅ **Event log viewing** with filtering
- ✅ **Application launcher** for external tools
- ✅ **Master matrix integration** for trading data

The architecture is **decoupled** - the dashboard reads event logs that the pipeline writes, allowing the pipeline to run independently while the dashboard provides monitoring and control capabilities.

