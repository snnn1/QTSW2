# Simple Scheduler

## What It Does

Runs the pipeline every 15 minutes at :00, :15, :30, :45. That's it.

## How to Use

```batch
python -m automation.simple_scheduler
```

Or use the batch file:
```batch
automation\start_simple_scheduler.bat
```

## Features

✅ **Simple**: Just checks time and runs pipeline  
✅ **Reliable**: Catches errors and keeps running  
✅ **Clear Logging**: Everything goes to `automation/logs/scheduler_YYYYMMDD.log`  
✅ **No Complexity**: No extra files, no configuration needed  

## How It Works

1. Checks current time every minute
2. If minute is :00, :15, :30, or :45 → runs pipeline
3. If pipeline fails → logs error, continues running
4. Repeats forever (until you stop it)

## Stopping

Press `Ctrl+C` in the terminal where it's running.

## Dashboard Integration

The dashboard automatically reads the event logs that the pipeline writes, so you'll see:
- Real-time events in the dashboard
- Pipeline status
- All the data you need

No extra setup needed - just run the scheduler and open the dashboard.

## That's It

No configuration. No setup. Just run it.

