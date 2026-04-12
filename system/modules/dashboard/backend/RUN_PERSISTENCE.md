# Run Persistence - First-Class Run Artifacts

## Overview

RunContext is now promoted to a first-class persisted artifact. Every pipeline run is automatically persisted to JSONL format with complete audit information.

## Architecture

### RunSummary

First-class persisted run artifact containing:
- `run_id`: Unique run identifier
- `started_at`: Run start timestamp (ISO format)
- `ended_at`: Run completion timestamp (ISO format)
- `result`: Completion result (success, failed, stopped)
- `failure_reason`: Error message if failed
- `stages_executed`: List of stages that executed (translator, analyzer, merger)
- `stages_failed`: List of stages that failed
- `retry_count`: Number of retries attempted
- `metadata`: Additional run metadata

### RunHistory

Manages persisted run summaries in JSONL format:
- **Location**: `automation/logs/runs/runs.jsonl`
- **Format**: One JSON object per line (JSONL)
- **Append-only**: Efficient writes, sequential reads
- **Queryable**: List runs, filter by result, get specific run

### Persistence Points

Runs are automatically persisted when they complete:

1. **SUCCESS**: When pipeline completes successfully
2. **FAILED**: When pipeline fails (stage failure or exception)
3. **STOPPED**: When pipeline is explicitly stopped

## Implementation

### Files Created

1. **`orchestrator/run_history.py`**
   - `RunSummary` dataclass
   - `RunHistory` manager class
   - JSONL persistence logic

### Files Modified

1. **`orchestrator/state.py`**
   - Added `stages_executed` and `stages_failed` to `RunContext`
   - Tracks which stages ran during execution

2. **`orchestrator/runner.py`**
   - Tracks stages executed in `run_pipeline()`
   - Records failed stages

3. **`orchestrator/service.py`**
   - Added `RunHistory` instance
   - Added `_persist_run_summary()` method
   - Hooks into completion points (SUCCESS, FAILED, STOPPED)

4. **`routers/pipeline.py`**
   - Added `/api/pipeline/runs` endpoint (list runs)
   - Added `/api/pipeline/runs/{run_id}` endpoint (get specific run)

## API Endpoints

### List Runs

```
GET /api/pipeline/runs?limit=100&result=success
```

Query parameters:
- `limit`: Maximum number of runs to return (default: 100)
- `result`: Filter by result (success, failed, stopped)

Returns:
```json
{
  "runs": [
    {
      "run_id": "abc123...",
      "started_at": "2025-01-15T10:00:00",
      "ended_at": "2025-01-15T10:30:00",
      "result": "success",
      "failure_reason": null,
      "stages_executed": ["translator", "analyzer", "merger"],
      "stages_failed": [],
      "retry_count": 0,
      "metadata": {}
    }
  ],
  "total": 1
}
```

### Get Specific Run

```
GET /api/pipeline/runs/{run_id}
```

Returns:
```json
{
  "run_id": "abc123...",
  "started_at": "2025-01-15T10:00:00",
  "ended_at": "2025-01-15T10:30:00",
  "result": "success",
  "failure_reason": null,
  "stages_executed": ["translator", "analyzer", "merger"],
  "stages_failed": [],
  "retry_count": 0,
  "metadata": {}
}
```

## Storage Format

Runs are stored in JSONL format (one JSON object per line):

```jsonl
{"run_id": "abc123...", "started_at": "2025-01-15T10:00:00", "ended_at": "2025-01-15T10:30:00", "result": "success", "failure_reason": null, "stages_executed": ["translator", "analyzer", "merger"], "stages_failed": [], "retry_count": 0, "metadata": {}}
{"run_id": "def456...", "started_at": "2025-01-15T11:00:00", "ended_at": "2025-01-15T11:15:00", "result": "failed", "failure_reason": "Stage translator failed after 2 retries", "stages_executed": ["translator"], "stages_failed": ["translator"], "retry_count": 2, "metadata": {}}
```

## Benefits

1. **Auditability**: Complete history of all pipeline runs
2. **Debuggability**: See which stages executed and which failed
3. **Analytics**: Query run history for patterns and trends
4. **Reliability**: Runs are persisted even if system crashes
5. **Simplicity**: JSONL format is human-readable and easy to parse

## Usage Examples

### Query Recent Successful Runs

```python
runs = orchestrator.run_history.list_runs(limit=10, result_filter="success")
for run in runs:
    print(f"Run {run.run_id[:8]}: {run.stages_executed}")
```

### Get Run Details

```python
run = orchestrator.run_history.get_run("abc123...")
if run:
    print(f"Result: {run.result}")
    print(f"Stages: {run.stages_executed}")
    if run.failure_reason:
        print(f"Error: {run.failure_reason}")
```

### Count Runs by Result

```python
success_count = orchestrator.run_history.get_run_count(result_filter="success")
failed_count = orchestrator.run_history.get_run_count(result_filter="failed")
stopped_count = orchestrator.run_history.get_run_count(result_filter="stopped")
```

## Future Enhancements

- Add run duration calculation
- Add stage-level timing information
- Add run comparison utilities
- Add run statistics and analytics
- Add run export functionality


