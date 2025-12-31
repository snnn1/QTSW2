# Pipeline Health Override Guide

## Problem

You're seeing this error:
```
Pipeline run blocked: Manual run requires override: health is unstable
```

This happens when **3 or more of the last 5 pipeline runs failed**. The system blocks new runs to prevent cascading failures.

## Root Cause

Your recent failures were caused by **RTY instrument support** - the analyzer discovered RTY data but the parallel runner didn't support it. This has been fixed.

## Solutions

### Option 1: Use Dashboard UI (Recommended)

1. Click "Run Pipeline Now" button
2. If you see the error, a dialog will appear asking if you want to override
3. Click "OK" to override and run anyway

The UI will automatically retry with `manual_override: true`.

### Option 2: Command-Line Override (Quick Fix)

Run this script to start pipeline with override:

```bash
python tools/start_pipeline_override.py
```

Or use curl:

```bash
curl -X POST http://localhost:8001/api/pipeline/start \
  -H "Content-Type: application/json" \
  -d '{"manual": true, "manual_override": true}'
```

### Option 3: Wait for Natural Recovery

Health will automatically recover to `HEALTHY` once you have **2 successful runs** that push the failures out of the rolling window.

Current status:
- Last 5 runs: 3 failed, 2 successful
- Need: 2 more successful runs to push failures out

## Understanding Health States

- **HEALTHY**: 0-2 failures in last 5 runs → Runs allowed
- **DEGRADED**: Same stage failed in 2 consecutive runs → Manual runs require override
- **UNSTABLE**: 3+ failures in last 5 runs → Manual runs require override  
- **BLOCKED**: Infrastructure error or force lock clear → Cannot override (must fix root cause)

## When to Override

✅ **Safe to override** if:
- You've fixed the root cause (RTY support is now fixed)
- Failures were due to a known issue that's resolved
- You need to test the fix

❌ **Don't override** if:
- You haven't fixed the root cause
- Failures are due to infrastructure issues (disk, memory, permissions)
- You're seeing BLOCKED health (cannot override anyway)

## Checking Current Health

View the last 5 runs:

```bash
python -c "import json, pathlib; p=pathlib.Path('automation/logs/runs/runs.jsonl'); runs=[json.loads(l) for l in p.read_text(encoding='utf-8').splitlines() if l.strip()]; runs.sort(key=lambda r: r.get('started_at',''), reverse=True); [print(f\"{i+1}. {r.get('started_at','')[:19]} - {r.get('result','unknown')} - {(r.get('failure_reason') or '')[:60]}\") for i, r in enumerate(runs[:5])]"
```

## Fix Applied

The RTY support issue has been fixed:
- ✅ `ops/maintenance/run_analyzer_parallel.py` now supports RTY
- ✅ Analyzer stage filters unsupported instruments gracefully
- ✅ Better error logging for fast-failing processes

After 2 successful runs, health will recover automatically.
