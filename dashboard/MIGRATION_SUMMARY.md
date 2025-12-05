# Migration Summary

## Quick Start

### 1. Test the New System
```bash
python -m automation.test_dry_run
```

### 2. Run Pipeline Manually (Optional)
```bash
python -m automation.pipeline_runner
```

### 3. Set Up Task Scheduler

**Option A: PowerShell Script (Recommended)**
```powershell
# Run as Administrator
.\automation\setup_task_scheduler.ps1
```

**Option B: Manual Setup**
- Open Task Scheduler
- Create task: "Pipeline Runner"
- Trigger: Every 15 minutes
- Action: `python -m automation.pipeline_runner`
- Working Directory: `C:\Users\jakej\QTSW2`

### 4. Stop Old Scheduler
- Via dashboard or Task Manager

### 5. Monitor
- Dashboard "Live Events" panel
- Task Scheduler history
- Log files in `automation/logs/`

## Key Differences

| Aspect | Old System | New System |
|--------|-----------|------------|
| **Architecture** | Monolithic (1,565 lines) | Modular (11 modules) |
| **State Management** | Global variables | Dependency injection |
| **Scheduling** | Infinite loop | OS Task Scheduler |
| **Subprocess** | Embedded logic | Reusable ProcessSupervisor |
| **File Operations** | Scattered | FileManager service |
| **Event Logging** | Global EVENT_LOG_PATH | Per-instance EventLogger |
| **GUI Logic** | Mixed in | Headless-only |
| **Testability** | Difficult | Easy (unit testable) |

## File Locations

- **Event Logs**: `automation/logs/events/pipeline_{run_id}.jsonl`
- **Pipeline Logs**: `automation/logs/pipeline_YYYYMMDD_HHMMSS.log`
- **Audit Reports**: `automation/logs/pipeline_report_YYYYMMDD_HHMMSS_{run_id}.json`

## Compatibility

✅ **Dashboard**: Fully compatible (same event log format)  
✅ **Event Format**: Identical JSONL structure  
✅ **File Naming**: Same convention (`pipeline_{run_id}.jsonl`)  
✅ **Directory**: Same location (`automation/logs/events/`)  

## Rollback

If needed, stop Task Scheduler task and restart old scheduler:
```bash
python automation/daily_data_pipeline_scheduler.py --wait-for-schedule
```

## Support

- Check logs: `automation/logs/`
- Check Task Scheduler history
- Review dashboard "Live Events"



