# Simple Scheduler

## What It Does

Runs the pipeline every 15 minutes at :00, :15, :30, :45. That's it.

## How to Use

### Option 1: Run Directly
```batch
python -m automation.simple_scheduler
```

### Option 2: Use Batch File
```batch
automation\start_simple_scheduler.bat
```

### Option 3: Run in Background (Windows)
```batch
start /B python -m automation.simple_scheduler
```

## Features

✅ **Simple**: Just checks time and runs pipeline  
✅ **Reliable**: Catches errors and keeps running  
✅ **Clear Logging**: Everything goes to `automation/logs/scheduler_YYYYMMDD.log`  
✅ **No Complexity**: No heartbeat files, no status JSON, no extra files  

## How It Works

1. Checks current time every minute
2. If minute is :00, :15, :30, or :45 → runs pipeline
3. If pipeline fails → logs error, continues running
4. Repeats forever (until you stop it)

## Stopping

Press `Ctrl+C` in the terminal where it's running.

## Logs

All logs go to: `automation/logs/scheduler_YYYYMMDD.log`

You'll see:
- When scheduler starts
- When each pipeline run starts
- When pipeline completes
- Any errors (scheduler keeps running)

## That's It

No configuration needed. No setup. Just run it.

