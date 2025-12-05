# Dashboard Complete Overview

## 🎯 What the Dashboard Does

The Pipeline Dashboard is a **real-time monitoring and control system** for a data processing pipeline that handles trading data. It provides:

1. **Real-time Pipeline Monitoring**
   - Live tracking of all pipeline stages (Export → Translator → Analyzer → Sequential Processor)
   - WebSocket-based event streaming for instant updates
   - Visual progress indicators and metrics

2. **Pipeline Control**
   - Manual pipeline execution ("Run Now" button)
   - Individual stage execution (translator, analyzer, sequential)
   - Schedule management (set daily run times)
   - Data merger execution

3. **Visual Monitoring**
   - Export stage progress (rows processed, files created, progress bar)
   - File counts (raw, processed, analyzed)
   - Current stage tracking with elapsed time
   - Processing rate metrics
   - Event log with color-coded messages

4. **Application Launchers**
   - Launch Translator Streamlit app (port 8501)
   - Launch Analyzer Streamlit app (port 8502)
   - Launch Sequential Processor Streamlit app (port 8503)

5. **Master Matrix & Timetable Management**
   - Build master matrix from analyzer runs
   - Generate trading timetables
   - View and filter matrix data

---

## 🏗️ Architecture

### Components

```
┌─────────────────────────────────────────────────────────────┐
│                    Dashboard System                         │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌──────────────┐         ┌──────────────┐                │
│  │   Frontend   │ ◄─────► │   Backend    │                │
│  │  (React)     │ WebSocket│  (FastAPI)   │                │
│  │  Port 5173   │   HTTP  │  Port 8000   │                │
│  └──────────────┘         └──────┬───────┘                │
│                                   │                          │
│                                   │ Reads/Writes            │
│                                   ▼                          │
│                          ┌──────────────┐                   │
│                          │  Event Logs  │                   │
│                          │   (JSONL)    │                   │
│                          └──────┬───────┘                   │
│                                   │                          │
│                                   │ Launches                │
│                                   ▼                          │
│                          ┌──────────────┐                   │
│                          │   Pipeline   │                   │
│                          │  Scheduler  │                   │
│                          └──────────────┘                   │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### Technology Stack

**Frontend:**
- React 18+ with hooks
- Vite (build tool)
- Tailwind CSS (styling)
- WebSocket API (real-time communication)

**Backend:**
- FastAPI (Python web framework)
- Uvicorn (ASGI server)
- WebSockets (real-time event streaming)
- Subprocess management (pipeline execution)

### Data Flow

1. **User Action** → Frontend sends HTTP request to backend
2. **Backend** → Launches pipeline process with event log path
3. **Pipeline** → Writes structured events to JSONL file
4. **Backend** → Tails event log file and streams via WebSocket
5. **Frontend** → Receives events and updates UI in real-time

---

## 📊 Features

### 1. Pipeline Controls
- **Run Now**: Start full pipeline immediately
- **Run Stage**: Execute individual stages (translator, analyzer, sequential)
- **Schedule Management**: Set daily run time (HH:MM format)
- **Reset Pipeline**: Clear state and disconnect

### 2. Real-Time Monitoring
- **Export Stage Panel**: 
  - Status (Not Started / Active / Complete / Failed)
  - Progress bar
  - Rows processed
  - Files created
  - Current instrument being exported
  
- **Metrics Cards**:
  - Run ID
  - Current Stage
  - Raw Files Count
  - Processed Files Count
  - Analyzed Files Count
  - Processing Rate (rows/min)

- **Events Log**:
  - Real-time event stream
  - Color-coded by severity (green=success, red=failure, gray=info)
  - Expandable data sections
  - Auto-scroll to latest

### 3. Schedule Information
- Next scheduled run time
- Countdown timer
- Runs every 15 minutes (at :00, :15, :30, :45)

### 4. Data Merger
- Manual execution button
- Status tracking
- Success/error notifications

### 5. Application Launchers
- One-click launch for Streamlit apps
- Port conflict detection
- Status feedback

---

## 🚨 Possible Errors & Error Handling

### Frontend Errors

#### 1. **Initialization Errors**
- **Error**: React component initialization fails
- **Handling**: ErrorBoundary component catches and displays error screen
- **User Action**: Reload page button provided
- **Location**: `dashboard/frontend/src/components/ErrorBoundary.jsx`

#### 2. **Backend Connection Errors**
- **Error**: Cannot connect to backend API (port 8000)
- **Symptoms**: 
  - "Backend not connected" message
  - API calls fail
  - WebSocket connection fails
- **Possible Causes**:
  - Backend not running
  - Port 8000 in use by another process
  - Firewall blocking connection
  - Wrong URL/host
- **Handling**: 
  - Error messages logged to console
  - File counts show "-1" (error indicator)
  - Alerts shown to user
- **Location**: `dashboard/frontend/src/services/pipelineManager.js`

#### 3. **WebSocket Connection Errors**
- **Error**: WebSocket connection fails or drops
- **Symptoms**:
  - No real-time updates
  - Events not appearing
  - Connection status issues
- **Possible Causes**:
  - Backend not running
  - Network issues
  - Backend WebSocket endpoint error
  - Connection timeout
- **Handling**:
  - Automatic reconnection with exponential backoff (2s, 4s, 8s, 16s, max 30s)
  - Reconnection attempts logged
  - Connection state tracked
  - Only reconnects if pipeline is still running
- **Location**: `dashboard/frontend/src/services/websocketManager.js`

#### 4. **API Request Errors**
- **Error**: HTTP requests fail (404, 500, network errors)
- **Symptoms**:
  - Error alerts shown
  - Functionality unavailable
  - Status indicators show errors
- **Possible Causes**:
  - Backend endpoint doesn't exist
  - Backend crashed
  - Network issues
  - Invalid request parameters
- **Handling**:
  - Try-catch blocks around all API calls
  - Error messages logged to console
  - User-friendly error alerts
  - Graceful degradation (e.g., file counts show -1)
- **Location**: `dashboard/frontend/src/services/pipelineManager.js`

#### 5. **JSON Parsing Errors**
- **Error**: Invalid JSON in WebSocket messages
- **Symptoms**: Events not processed
- **Handling**: 
  - Try-catch around JSON.parse
  - Error logged, message skipped
  - Connection continues
- **Location**: `dashboard/frontend/src/services/websocketManager.js`

#### 6. **State Management Errors**
- **Error**: Reducer or state update fails
- **Symptoms**: UI doesn't update correctly
- **Handling**: 
  - ErrorBoundary catches React errors
  - Console logging for debugging
  - State validation
- **Location**: `dashboard/frontend/src/hooks/usePipelineState.js`

#### 7. **Event Deduplication Issues**
- **Error**: Duplicate events appear
- **Symptoms**: Same event shown multiple times
- **Handling**: 
  - Event key-based deduplication
  - Checks timestamp, stage, event type, and message
- **Location**: `dashboard/frontend/src/hooks/usePipelineState.js` (reducer)

#### 8. **Memory Issues**
- **Error**: Too many events stored
- **Symptoms**: Browser slowdown
- **Handling**: 
  - Events array limited to last 100 events
  - Old events automatically removed
- **Location**: `dashboard/frontend/src/hooks/usePipelineState.js`

---

### Backend Errors

#### 1. **Port Already in Use**
- **Error**: Port 8000 already occupied
- **Symptoms**: Backend won't start
- **Error Message**: "Address already in use"
- **Handling**: 
  - Uvicorn will fail to start
  - Error logged to console
  - User must close conflicting process or change port
- **Location**: `dashboard/backend/main.py` (uvicorn.run)

#### 2. **File System Errors**
- **Error**: Cannot read/write event log files
- **Possible Causes**:
  - Insufficient permissions
  - Disk full
  - Path doesn't exist
  - File locked by another process
- **Handling**:
  - Try-catch around file operations
  - Error logged
  - HTTPException raised with details
  - Event log directory created on startup
- **Location**: `dashboard/backend/main.py` (WebSocket tailing, file operations)

#### 3. **Subprocess Launch Errors**
- **Error**: Cannot start pipeline process
- **Possible Causes**:
  - Python not found in PATH
  - Scheduler script doesn't exist
  - Insufficient permissions
  - Invalid working directory
- **Symptoms**: 
  - HTTP 500 error
  - Error message in response
- **Handling**:
  - Try-catch around subprocess.Popen
  - HTTPException with error details
  - Error logged
- **Location**: `dashboard/backend/main.py` (start_pipeline, run_stage)

#### 4. **WebSocket Connection Errors**
- **Error**: WebSocket connection fails
- **Possible Causes**:
  - Client disconnects unexpectedly
  - Network issues
  - Backend crash
- **Handling**:
  - WebSocketDisconnect exception caught
  - Connection removed from active connections
  - Tailing tasks cancelled
  - Errors logged (but expected errors filtered)
- **Location**: `dashboard/backend/main.py` (websocket_events endpoint)

#### 5. **Event Log Tailing Errors**
- **Error**: Cannot read event log file
- **Possible Causes**:
  - File doesn't exist
  - File locked
  - Permission denied
  - File deleted during tailing
- **Handling**:
  - Waits up to 30 seconds for file creation
  - Retries with exponential backoff
  - Allows up to 20 consecutive errors before giving up
  - Continues tailing even after errors
  - Sends error message to clients if file not found
- **Location**: `dashboard/backend/main.py` (_tail_file method)

#### 6. **JSON Parsing Errors**
- **Error**: Invalid JSON in event log file
- **Symptoms**: Events not broadcast
- **Handling**:
  - JSONDecodeError caught
  - Error logged
  - Line skipped
  - Tailing continues
  - Allows up to 20 consecutive parse errors
- **Location**: `dashboard/backend/main.py` (_tail_file method)

#### 7. **Schedule Configuration Errors**
- **Error**: Invalid schedule time format
- **Symptoms**: Schedule update fails
- **Handling**:
  - Time format validation (HH:MM)
  - HTTPException with 400 status
  - Error message explains format
- **Location**: `dashboard/backend/main.py` (update_schedule endpoint)

#### 8. **Module Import Errors**
- **Error**: Cannot import MasterMatrix or TimetableEngine
- **Possible Causes**:
  - Module file doesn't exist
  - Syntax errors in module
  - Missing dependencies
  - Import path issues
- **Handling**:
  - Module reload attempted
  - HTTPException with 500 status
  - Error details in response
  - Error logged
- **Location**: `dashboard/backend/main.py` (build_master_matrix endpoint)

#### 9. **Data Processing Errors**
- **Error**: Master matrix build fails
- **Possible Causes**:
  - Invalid date ranges
  - Missing data files
  - Data format issues
  - Memory issues
- **Handling**:
  - Try-catch around build operations
  - HTTPException with error details
  - Error logged
- **Location**: `dashboard/backend/main.py` (build_master_matrix, generate_timetable)

#### 10. **File Count Errors**
- **Error**: Cannot count files in directories
- **Possible Causes**:
  - Directory doesn't exist
  - Permission denied
  - Path resolution issues
- **Handling**:
  - Try-catch around file operations
  - Returns 0 if directory doesn't exist
  - Errors logged
- **Location**: `dashboard/backend/main.py` (get_file_counts endpoint)

#### 11. **Streamlit App Launch Errors**
- **Error**: Cannot start Streamlit app
- **Possible Causes**:
  - Streamlit not installed
  - Port already in use
  - Script file doesn't exist
  - Permission issues
- **Handling**:
  - Port check before launch
  - Returns "already_running" if port in use
  - Try-catch around subprocess
  - HTTPException with error details
- **Location**: `dashboard/backend/main.py` (start_translator_app, start_analyzer_app, start_sequential_app)

#### 12. **Data Merger Errors**
- **Error**: Data merger process fails
- **Possible Causes**:
  - Script doesn't exist
  - Data processing errors
  - Timeout (5 minutes)
  - Python errors in merger script
- **Handling**:
  - Process timeout (5 minutes)
  - Return code checked
  - Error output captured
  - Status returned to frontend
- **Location**: `dashboard/backend/main.py` (run_data_merger endpoint)

#### 13. **Pipeline Status Detection Errors**
- **Error**: Cannot determine pipeline status
- **Possible Causes**:
  - Event log file read errors
  - Invalid JSON in log
  - File permissions
- **Handling**:
  - Try-catch around file operations
  - Returns {active: false} on error
  - Errors logged but not raised
- **Location**: `dashboard/backend/main.py` (get_pipeline_status endpoint)

#### 14. **CORS Errors**
- **Error**: Frontend cannot access backend due to CORS
- **Symptoms**: API calls fail with CORS error
- **Handling**:
  - CORS middleware configured
  - Allowed origins: localhost:5173, localhost:5174, localhost:3000, 192.168.1.171:5174
  - All methods and headers allowed
- **Location**: `dashboard/backend/main.py` (CORS middleware)

#### 15. **Logging Errors**
- **Error**: Cannot write to log file
- **Possible Causes**:
  - Disk full
  - Permission denied
  - Path doesn't exist
- **Handling**:
  - Log directory created on startup
  - Try-catch around log writes
  - Falls back to console logging
- **Location**: `dashboard/backend/main.py` (logging setup)

---

### Pipeline Process Errors

#### 1. **Export Stage Errors**
- **Error**: Export fails or stalls
- **Detection**: 
  - No completion after 60 minutes (timeout)
  - File not growing for 5+ minutes (stalled)
- **Handling**: 
  - Failure event emitted
  - Alert shown in dashboard
  - Export status set to "failed"
- **Location**: Pipeline scheduler (not in dashboard code)

#### 2. **Translator Stage Errors**
- **Error**: Translation fails
- **Detection**: Error in translator process
- **Handling**: 
  - Failure event emitted
  - Alert shown in dashboard
  - Stage status updated
- **Location**: Pipeline scheduler

#### 3. **Analyzer Stage Errors**
- **Error**: Analysis fails for instrument
- **Detection**: Per-instrument failures
- **Handling**: 
  - Failure event emitted per instrument
  - Alert shown in dashboard
  - Other instruments continue processing
- **Location**: Pipeline scheduler

#### 4. **Sequential Processor Errors**
- **Error**: Sequential processing fails
- **Detection**: Error in sequential processor
- **Handling**: 
  - Failure event emitted
  - Alert shown in dashboard
- **Location**: Pipeline scheduler

---

## 🔧 Error Recovery & Resilience

### Frontend Resilience
1. **Automatic Reconnection**: WebSocket reconnects automatically with exponential backoff
2. **Graceful Degradation**: Shows error indicators (-1) instead of crashing
3. **Error Boundaries**: Catches React errors and shows error screen
4. **State Persistence**: Filters and settings saved to localStorage
5. **Polling Fallback**: Status polling continues even if WebSocket fails

### Backend Resilience
1. **Error Logging**: All errors logged to file and console
2. **Exception Handling**: Try-catch around all critical operations
3. **File Operations**: Retries with backoff for file operations
4. **Process Management**: Tracks subprocesses and handles termination
5. **Connection Management**: Tracks WebSocket connections and cleans up on disconnect

### Pipeline Resilience
1. **Event Logging**: All pipeline events logged for debugging
2. **Stage Isolation**: Failures in one stage don't prevent others from running
3. **Timeout Protection**: Timeouts prevent infinite hangs
4. **Status Tracking**: Pipeline status tracked even if dashboard disconnected

---

## 📝 Error Logging

### Frontend Logging
- **Location**: Browser console
- **Level**: Error, warn, info
- **Format**: Console.log/error with context

### Backend Logging
- **Location**: `logs/backend_debug.log`
- **Level**: INFO, WARNING, ERROR
- **Format**: Timestamp - Level - Message
- **Additional**: Master matrix logs to `logs/master_matrix.log`

### Pipeline Logging
- **Location**: `automation/logs/events/pipeline_{run_id}.jsonl`
- **Format**: JSON lines (one event per line)
- **Content**: Structured events with stage, event type, timestamp, message, data

---

## 🚀 Troubleshooting Guide

### Dashboard Won't Start
1. Check backend is running (http://localhost:8000)
2. Check frontend is running (http://localhost:5173)
3. Check ports are available
4. Check dependencies installed
5. Check browser console for errors

### No Events Showing
1. Check WebSocket connection (browser console)
2. Check backend is tailing event log
3. Check event log file exists
4. Check pipeline is actually running
5. Check browser console for WebSocket errors

### Pipeline Not Starting
1. Check backend logs for errors
2. Check scheduler script exists
3. Check Python is in PATH
4. Check file permissions
5. Check event log directory is writable

### WebSocket Disconnects
1. Check network stability
2. Check backend is still running
3. Check browser console for errors
4. Check reconnection attempts in console
5. Check backend logs for WebSocket errors

### File Counts Show -1
1. Check backend is running
2. Check API endpoint is accessible
3. Check data directories exist
4. Check file permissions
5. Check browser console for API errors

---

## 📚 Key Files Reference

### Frontend
- `dashboard/frontend/src/App.jsx` - Main app component
- `dashboard/frontend/src/hooks/usePipelineState.js` - State management
- `dashboard/frontend/src/services/websocketManager.js` - WebSocket handling
- `dashboard/frontend/src/services/pipelineManager.js` - API calls
- `dashboard/frontend/src/components/ErrorBoundary.jsx` - Error boundary

### Backend
- `dashboard/backend/main.py` - Main FastAPI application
- `dashboard/backend/models.py` - Pydantic models
- `dashboard/backend/config.py` - Configuration
- `dashboard/backend/routers/schedule.py` - Schedule endpoints

### Documentation
- `dashboard/README.md` - Basic overview
- `dashboard/QUICK_START.md` - Quick start guide
- `dashboard/USER_GUIDE.md` - Complete user guide
- `dashboard/DASHBOARD_OVERVIEW.md` - This file

---

## ✅ Summary

The dashboard is a robust, real-time monitoring system with comprehensive error handling at all levels:

- **Frontend**: Error boundaries, graceful degradation, automatic reconnection
- **Backend**: Exception handling, logging, retry logic
- **Pipeline**: Event logging, stage isolation, timeout protection

All errors are logged and most are recoverable automatically. The system continues operating even when individual components fail.



