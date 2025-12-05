# Dashboard Diagnostic Test Suite

Comprehensive end-to-end diagnostic tests for all dashboard subsystems as described in `DASHBOARD_OVERVIEW.md`.

## Overview

This diagnostic suite tests:

1. **Frontend (React/Vite)** - Build, WebSocket, error handling, state management
2. **Backend (FastAPI)** - Routes, file system, WebSocket, CORS, logging
3. **Pipeline Stages** - Timeout logic, stall detection, status tracking
4. **Event Log & Data Flow** - Tailing, corrupted JSON, rapid writes
5. **Schedule System** - Validation, format checking
6. **Streamlit Apps** - Port scanning, launch logic
7. **Data Merger** - Timeout handling
8. **End-to-End Resilience** - Graceful degradation

## Running the Diagnostics

### Prerequisites

```bash
pip install -r requirements.txt
```

### Run All Tests

```bash
python diagnostic_runner.py
```

### Output

The diagnostic suite generates two reports:

1. **`diagnostic_report.json`** - Structured JSON with all test results, latencies, errors, and remediation suggestions
2. **`diagnostic_summary.md`** - Human-readable markdown report with executive summary, test results by category, and detailed test table

## Test Categories

### 1. Frontend Diagnostics (8 tests)
- React build compilation
- Port 5173 availability
- WebSocket auto-reconnect logic
- ErrorBoundary component structure
- API error handling
- State store integrity
- Event deduplication
- Memory capping (100 events)

### 2. Backend Diagnostics (8 tests)
- Port 8000 availability
- Backend API routes
- File system error handling
- JSONL malformed line handling
- WebSocket burst load (1k events)
- Module reload error handling
- CORS middleware
- Logging system fallback

### 3. Pipeline Stage Diagnostics (3 tests)
- Export timeout logic (60min)
- Stall detection (5min)
- Pipeline status detection

### 4. Event Log & Data Flow (3 tests)
- Event log tailing
- Corrupted JSON handling
- Rapid writes handling

### 5. Schedule System (1 test)
- Schedule validation

### 6. Streamlit App (1 test)
- Port scanning logic

### 7. Data Merger (1 test)
- Merger timeout (5min)

### 8. End-to-End Resilience (1 test)
- Graceful degradation

## Interpreting Results

### Pass/Fail Status
- ✅ **PASS** - Test passed, feature working as expected
- ❌ **FAIL** - Test failed, review error details and remediation suggestions

### Latency Metrics
Each test reports latency in milliseconds. High latency may indicate:
- Network issues
- Backend not running (connection timeouts)
- Resource constraints

### Error Details
Failed tests include:
- Error message
- Stack trace (if available)
- Remediation suggestions

## Remediation

The JSON report includes `remediation_suggestions` for failed tests. Common issues:

- **Backend not running**: Start backend with `cd dashboard/backend && python main.py`
- **Frontend build failed**: Run `cd dashboard/frontend && npm install && npm run build`
- **Port conflicts**: Check if services are already running on ports 8000/5173
- **Missing dependencies**: Install requirements with `pip install -r requirements.txt`

## Continuous Integration

This diagnostic suite can be integrated into CI/CD pipelines:

```bash
python diagnostic_runner.py
if [ $? -ne 0 ]; then
    echo "Diagnostics failed"
    exit 1
fi
```

## Notes

- Some tests require the backend to be running (will gracefully handle if not)
- Frontend build test requires npm/node.js
- WebSocket tests may show connection errors if backend is not running (this is expected)
- Tests that check code structure will pass even if services aren't running

## Files

- `diagnostic_runner.py` - Main test runner
- `requirements.txt` - Python dependencies
- `diagnostic_report.json` - Generated JSON report
- `diagnostic_summary.md` - Generated markdown report



