# Windows Task Scheduler Diagnostic Tool

## Purpose

Definitively determines whether the "Pipeline Runner" Windows Task Scheduler job is executing and why scheduled runs may not be occurring.

## Usage

### Quick Run
```batch
tools\RUN_SCHEDULER_DIAGNOSTIC.bat
```

### Direct Python
```bash
python tools\diagnose_scheduler.py
```

## What It Checks

### 1. Task Definition
- Task existence and name
- Executable path (Python)
- Script path (`automation\run_pipeline_standalone.py`)
- Working directory (`C:\Users\jakej\QTSW2`)
- User account and privileges
- Run level (Limited vs Highest)

### 2. Task Execution Evidence
- **LastRunTime**: When task last executed
- **LastTaskResult**: Exit code (0 = success, others = failure)
- **NextRunTime**: When task will run next
- **NumberOfMissedRuns**: How many scheduled runs were missed
- Task Scheduler event history

### 3. Backend Correlation
- Recent pipeline log files in `automation/logs/`
- Recent JSONL event files in `automation/logs/events/`
- Correlation between task execution and log file creation

### 4. Environment Context
- Script file existence
- Python interpreter accessibility
- Working directory validity

### 5. Failure Mode Identification
- Exit codes and their meanings
- Task execution history
- Error patterns

### 6. Test Execution
- Attempts to manually trigger the task
- Verifies task can be started

## Interpreting Results

### Task Result Codes

| Code | Meaning |
|------|---------|
| `0` | Success |
| `267014` | Task has never run (SCHED_S_TASK_READY) |
| `267015` | Task is currently running |
| Other | Error occurred (check history) |

### Common Issues

#### Task Not Found
- **Symptom**: "Task does not exist"
- **Fix**: Run `batch\SETUP_WINDOWS_SCHEDULER.bat` as Administrator

#### Task Disabled
- **Symptom**: `Enabled: False`
- **Fix**: Enable via dashboard or Task Scheduler GUI

#### Task Never Runs
- **Symptom**: `LastTaskResult: 267014`, `LastRunTime: Never`
- **Possible Causes**:
  - Triggers not configured correctly
  - Task conditions preventing execution
  - User account permissions
  - Task is disabled

#### Task Fails
- **Symptom**: `LastTaskResult: <non-zero>`
- **Check**:
  - Task history for error details
  - Script path is correct
  - Python is accessible
  - Working directory exists

#### No Logs Generated
- **Symptom**: Task runs but no log files appear
- **Possible Causes**:
  - Script path incorrect
  - Working directory wrong
  - Python environment issues
  - Script exits before logging

## Evidence-Based Conclusions

The diagnostic will state definitively:

1. **Task never fires** - No execution evidence, result code 267014
2. **Task fires but fails before logging** - Execution evidence but no logs, non-zero exit code
3. **Task runs but pipeline exits early** - Logs exist but incomplete, early exit code
4. **Task works correctly** - Execution evidence, logs, exit code 0

## Minimum Fix Required

Based on diagnostic results, the tool will recommend the minimum fix needed.



