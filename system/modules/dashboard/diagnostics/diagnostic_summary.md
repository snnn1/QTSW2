# Dashboard Diagnostic Report

**Generated:** 2025-12-03 21:42:06  
**Duration:** 88.94 seconds  
**Total Tests:** 38  
**Passed:** 36 (94.7%)  
**Failed:** 2

## Executive Summary

This diagnostic report covers all subsystems of the Dashboard as described in `DASHBOARD_OVERVIEW.md`.

### Overall Status

⚠️ **2 test(s) failed.** Review the details below.

## Test Results by Category

### ✅ Frontend

- **Passed:** 8/8
- **Failed:** 0

### ❌ Backend

- **Passed:** 6/8
- **Failed:** 2

**Failed Tests:**
- **Backend API Routes**
  - Error: Test failed
- **WebSocket Burst Load (1k events)**
  - Error: Test failed

### ✅ Pipeline

- **Passed:** 3/3
- **Failed:** 0

### ✅ EventLog

- **Passed:** 3/3
- **Failed:** 0

### ✅ Schedule

- **Passed:** 1/1
- **Failed:** 0

### ✅ Streamlit

- **Passed:** 1/1
- **Failed:** 0

### ✅ Merger

- **Passed:** 1/1
- **Failed:** 0

### ✅ Resilience

- **Passed:** 1/1
- **Failed:** 0

### ✅ ErrorInjection

- **Passed:** 4/4
- **Failed:** 0

### ✅ Integration

- **Passed:** 3/3
- **Failed:** 0

### ✅ Performance

- **Passed:** 2/2
- **Failed:** 0

### ✅ ErrorRecovery

- **Passed:** 3/3
- **Failed:** 0

## Critical Issues

The following issues require immediate attention:

- Backend API Routes
- WebSocket Burst Load (1k events)

## Detailed Test Results

| Test | Category | Status | Latency (ms) | Error |
|------|----------|--------|--------------|-------|
| React Build Compilation | Frontend | ✅ PASS | 2491.8 |  |
| Port 5173 Availability | Frontend | ✅ PASS | 2050.9 |  |
| WebSocket Auto-Reconnect Logic | Frontend | ✅ PASS | 3013.0 |  |
| ErrorBoundary Component | Frontend | ✅ PASS | 0.2 |  |
| API Error Handling | Frontend | ✅ PASS | 0.2 |  |
| State Store Integrity | Frontend | ✅ PASS | 0.4 |  |
| Event Deduplication | Frontend | ✅ PASS | 0.2 |  |
| Memory Capping (100 events) | Frontend | ✅ PASS | 0.2 |  |
| Port 8000 Availability | Backend | ✅ PASS | 0.5 |  |
| Backend API Routes | Backend | ❌ FAIL | 49317.1 | Test failed |
| File System Error Handling | Backend | ✅ PASS | 0.5 |  |
| JSONL Malformed Line Handling | Backend | ✅ PASS | 0.4 |  |
| WebSocket Burst Load (1k events) | Backend | ❌ FAIL | 5021.9 | Test failed |
| Module Reload Error Handling | Backend | ✅ PASS | 0.8 |  |
| CORS Middleware | Backend | ✅ PASS | 0.4 |  |
| Logging System Fallback | Backend | ✅ PASS | 0.3 |  |
| Export Timeout Logic (60min) | Pipeline | ✅ PASS | 0.7 |  |
| Stall Detection (5min) | Pipeline | ✅ PASS | 0.5 |  |
| Pipeline Status Detection | Pipeline | ✅ PASS | 5616.5 |  |
| Event Log Tailing | EventLog | ✅ PASS | 1.0 |  |
| Corrupted JSON Handling | EventLog | ✅ PASS | 1.2 |  |
| Rapid Writes Handling | EventLog | ✅ PASS | 3.2 |  |
| Schedule Validation | Schedule | ✅ PASS | 4093.9 |  |
| Port Scanning Logic | Streamlit | ✅ PASS | 0.8 |  |
| Merger Timeout (5min) | Merger | ✅ PASS | 1.1 |  |
| Graceful Degradation | Resilience | ✅ PASS | 0.3 |  |
| File Permission Error | ErrorInjection | ✅ PASS | 1.4 |  |
| Subprocess Failure | ErrorInjection | ✅ PASS | 25.8 |  |
| API 404 Error | ErrorInjection | ✅ PASS | 2039.4 |  |
| API 500 Error Simulation | ErrorInjection | ✅ PASS | 2048.4 |  |
| WebSocket Event Ordering | Integration | ✅ PASS | 2080.7 |  |
| Pipeline Start Integration | Integration | ✅ PASS | 2051.3 |  |
| File Counts Integration | Integration | ✅ PASS | 2062.4 |  |
| High Frequency Events (100/sec) | Performance | ✅ PASS | 32.3 |  |
| Large Event Log (10k events) | Performance | ✅ PASS | 2899.6 |  |
| Backend Connection Recovery | ErrorRecovery | ✅ PASS | 2015.5 |  |
| Event Log Deletion During Tail | ErrorRecovery | ✅ PASS | 4.3 |  |
| Concurrent WebSocket Connections | ErrorRecovery | ✅ PASS | 2058.2 |  |

## Files Referenced

- `dashboard/backend/main.py` - Backend API server
- `dashboard/frontend/src/App.jsx` - Frontend main component
- `dashboard/frontend/src/hooks/usePipelineState.js` - State management
- `dashboard/frontend/src/services/websocketManager.js` - WebSocket handling
- `dashboard/frontend/src/services/pipelineManager.js` - API client
- `dashboard/frontend/src/components/ErrorBoundary.jsx` - Error boundary
- `automation/run_pipeline_standalone.py` - Pipeline entry point (uses orchestrator)

## Next Steps

1. Review failed tests above
2. Check remediation suggestions
3. Verify backend is running: `cd dashboard/backend && python main.py`
4. Verify frontend dependencies: `cd dashboard/frontend && npm install`
5. Check logs: `logs/backend_debug.log`
