"""
Quick diagnostic script to check why scheduled runs don't appear in dashboard.

Run this while the backend is running and after clicking "Run" on Task Scheduler.
"""

import sys
from pathlib import Path
from datetime import datetime, timedelta

# Add project root to path
qtsw2_root = Path(__file__).parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

print("=" * 80)
print("SCHEDULER TO DASHBOARD DIAGNOSTIC")
print("=" * 80)
print()

# Expected event logs directory
event_logs_dir = qtsw2_root / "automation" / "logs" / "events"
print(f"1. EXPECTED EVENT LOGS DIRECTORY:")
print(f"   {event_logs_dir}")
print(f"   Exists: {event_logs_dir.exists()}")
print()

if not event_logs_dir.exists():
    print("   [X] Directory does not exist! This is a problem.")
    print("   -> Standalone script creates it, but if it doesn't exist, events aren't being written.")
else:
    print("   [OK] Directory exists")
print()

# Check for JSONL files
print("2. JSONL FILES IN DIRECTORY:")
jsonl_files = list(event_logs_dir.glob("pipeline_*.jsonl"))
print(f"   Found {len(jsonl_files)} files matching 'pipeline_*.jsonl'")
print()

if len(jsonl_files) == 0:
    print("   [X] No JSONL files found!")
    print("   -> Either Task Scheduler never ran, or events aren't being written.")
else:
    print("   [OK] Files found:")
    recent_files = sorted(jsonl_files, key=lambda f: f.stat().st_mtime, reverse=True)[:10]
    for f in recent_files:
        mtime = datetime.fromtimestamp(f.stat().st_mtime)
        age = datetime.now() - mtime
        size = f.stat().st_size
        print(f"      - {f.name}")
        print(f"        Modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')} ({age.total_seconds():.0f} seconds ago)")
        print(f"        Size: {size:,} bytes")
        
        # Check if file is recent (within last 5 minutes)
        if age < timedelta(minutes=5):
            print(f"        [RECENT] (likely from last run)")
        else:
            print(f"        [OLD] (may not be from current run)")
        print()
print()

# Check for scheduler start event file
scheduler_file = event_logs_dir / "pipeline___scheduled__.jsonl"
print("3. SCHEDULER START EVENT FILE:")
print(f"   {scheduler_file.name}")
print(f"   Exists: {scheduler_file.exists()}")
if scheduler_file.exists():
    mtime = datetime.fromtimestamp(scheduler_file.stat().st_mtime)
    age = datetime.now() - mtime
    size = scheduler_file.stat().st_size
    print(f"   Modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')} ({age.total_seconds():.0f} seconds ago)")
    print(f"   Size: {size:,} bytes")
    
    if age < timedelta(minutes=5):
        print(f"   [RECENT]")
    else:
        print(f"   [OLD]")
    
    # Try to read first few lines
    try:
        with open(scheduler_file, 'r', encoding='utf-8') as f:
            lines = f.readlines()[:3]
            print(f"   First {len(lines)} line(s):")
            for i, line in enumerate(lines, 1):
                print(f"      {i}: {line.strip()[:100]}")
    except Exception as e:
        print(f"   [ERROR] Error reading file: {e}")
else:
    print("   [X] File does not exist!")
    print("   -> Standalone script should create this at startup.")
print()
print()

# Check config path (what backend monitor uses)
print("4. BACKEND CONFIG (what monitor uses):")
try:
    from modules.dashboard.backend.orchestrator.config import OrchestratorConfig
    config = OrchestratorConfig.from_environment(qtsw2_root=qtsw2_root)
    print(f"   event_logs_dir: {config.event_logs_dir}")
    print(f"   Matches expected: {config.event_logs_dir == event_logs_dir}")
    
    if config.event_logs_dir != event_logs_dir:
        print(f"   [X] MISMATCH!")
        print(f"   -> Backend monitor looks in: {config.event_logs_dir}")
        print(f"   -> Standalone script writes to: {event_logs_dir}")
        print(f"   -> These must match for events to appear!")
    else:
        print(f"   [OK] Paths match")
except Exception as e:
    print(f"   [ERROR] Error loading config: {e}")
print()
print()

# Check if backend is running
print("5. BACKEND AVAILABILITY:")
try:
    import httpx
    import asyncio
    
    async def check_backend():
        try:
            async with httpx.AsyncClient(timeout=2.0) as client:
                response = await client.get("http://localhost:8001/api/status")
                if response.status_code == 200:
                    print(f"   [OK] Backend is running (status: {response.status_code})")
                    return True
                else:
                    print(f"   [WARNING] Backend responded but status: {response.status_code}")
                    return False
        except httpx.ConnectError:
            print(f"   [X] Backend is not running (connection refused)")
            return False
        except Exception as e:
            print(f"   [WARNING] Error checking backend: {e}")
            return False
    
    backend_running = asyncio.run(check_backend())
except Exception as e:
    print(f"   [WARNING] Could not check backend: {e}")
    backend_running = False
print()
print()

# Summary
print("=" * 80)
print("SUMMARY:")
print("=" * 80)

issues = []

if not event_logs_dir.exists():
    issues.append("Event logs directory does not exist")

if len(jsonl_files) == 0:
    issues.append("No JSONL files found (Task Scheduler may not have run, or events not written)")

if not scheduler_file.exists():
    issues.append("Scheduler start event file missing (standalone script may not have run)")

try:
    config = OrchestratorConfig.from_environment(qtsw2_root=qtsw2_root)
    if config.event_logs_dir != event_logs_dir:
        issues.append("Directory mismatch between backend monitor and standalone script")
except:
    pass

if not backend_running:
    issues.append("Backend is not running (JSONL monitor won't process files)")

if len(issues) == 0:
    print("[OK] No obvious issues found!")
    print()
    print("If events still don't appear:")
    print("  1. Check backend logs for '[JSONL Monitor]' messages")
    print("  2. Verify monitor is scanning files (should log every ~60 seconds)")
    print("  3. Check WebSocket connection in browser console")
    print("  4. Verify Task Scheduler task actually executed (check Last Run Result)")
else:
    print(f"[X] Found {len(issues)} potential issue(s):")
    for i, issue in enumerate(issues, 1):
        print(f"  {i}. {issue}")

print()
print("=" * 80)

