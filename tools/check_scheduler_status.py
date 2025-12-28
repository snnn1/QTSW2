"""
Check Scheduler Status - Diagnose scheduler enable/disable issues
"""

import sys
from pathlib import Path
import json
import subprocess

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def main():
    print("="*80)
    print("SCHEDULER STATUS DIAGNOSTIC")
    print("="*80)
    
    # Check scheduler state file
    state_file = qtsw2_root / "automation" / "logs" / "scheduler_state.json"
    print(f"\n[SCHEDULER STATE FILE]")
    print(f"  Path: {state_file}")
    print(f"  Exists: {state_file.exists()}")
    
    if state_file.exists():
        try:
            with open(state_file, 'r') as f:
                state = json.load(f)
            print(f"  State data: {json.dumps(state, indent=2)}")
        except Exception as e:
            print(f"  [ERROR] Failed to read state file: {e}")
    
    # Check Windows Task Scheduler
    print(f"\n[WINDOWS TASK SCHEDULER]")
    task_name = "Pipeline Runner"
    
    try:
        # Check if task exists
        ps_check = f"Get-ScheduledTask -TaskName '{task_name}' -ErrorAction SilentlyContinue | Select-Object -Property TaskName, State, @{{Name='Enabled';Expression={{$_.Settings.Enabled}}}} | ConvertTo-Json -Depth 3"
        result = subprocess.run(
            ["powershell", "-Command", ps_check],
            capture_output=True,
            text=True,
            timeout=10
        )
        
        if result.returncode == 0 and result.stdout.strip():
            try:
                task_data = json.loads(result.stdout.strip())
                if isinstance(task_data, list):
                    task_data = task_data[0] if task_data else {}
                
                print(f"  Task Name: {task_data.get('TaskName', 'N/A')}")
                print(f"  State: {task_data.get('State', 'N/A')}")
                enabled = task_data.get('Enabled', False)
                print(f"  Enabled: {enabled}")
                
                if enabled:
                    print(f"  [STATUS] Task is ENABLED - automation is ON")
                else:
                    print(f"  [STATUS] Task is DISABLED - automation is OFF")
                    
            except json.JSONDecodeError as e:
                print(f"  [ERROR] Failed to parse PowerShell output: {e}")
                print(f"  Raw output: {result.stdout[:200]}")
        else:
            print(f"  [ERROR] Task '{task_name}' not found or command failed")
            print(f"  Return code: {result.returncode}")
            print(f"  Error: {result.stderr}")
            print(f"  Output: {result.stdout}")
    except Exception as e:
        print(f"  [ERROR] Failed to check Windows Task Scheduler: {e}")
    
    # Try to get status via API (if backend is running)
    print(f"\n[API STATUS CHECK]")
    try:
        import httpx
        try:
            response = httpx.get("http://localhost:8001/api/scheduler/status", timeout=5.0)
            if response.status_code == 200:
                api_status = response.json()
                print(f"  API Status: {json.dumps(api_status, indent=2)}")
            else:
                print(f"  API returned status {response.status_code}: {response.text[:200]}")
        except httpx.ConnectError:
            print(f"  [INFO] Backend not running or not accessible at http://localhost:8001")
        except Exception as e:
            print(f"  [ERROR] API check failed: {e}")
    except ImportError:
        print(f"  [INFO] httpx not available - skipping API check")
    
    # Recommendations
    print(f"\n[RECOMMENDATIONS]")
    print(f"  1. Check if backend is running as administrator (required for enable/disable)")
    print(f"  2. Verify task name is exactly: '{task_name}'")
    print(f"  3. Try manually enabling/disabling in Task Scheduler (taskschd.msc)")
    print(f"  4. Check backend logs for permission errors")

if __name__ == "__main__":
    main()









