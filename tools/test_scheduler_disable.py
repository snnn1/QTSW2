"""
Test Scheduler Disable - Test if we can actually disable the scheduler
"""

import sys
from pathlib import Path
import subprocess
import json

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def main():
    print("="*80)
    print("TEST SCHEDULER DISABLE")
    print("="*80)
    
    task_name = "Pipeline Runner"
    
    # Check current status
    print(f"\n[1. CHECKING CURRENT STATUS]")
    try:
        ps_check = f"Get-ScheduledTask -TaskName '{task_name}' -ErrorAction SilentlyContinue | Select-Object -Property TaskName, State, @{{Name='Enabled';Expression={{$_.Settings.Enabled}}}} | ConvertTo-Json -Depth 3"
        result = subprocess.run(
            ["powershell", "-Command", ps_check],
            capture_output=True,
            text=True,
            timeout=10
        )
        
        if result.returncode == 0 and result.stdout.strip():
            task_data = json.loads(result.stdout.strip())
            if isinstance(task_data, list):
                task_data = task_data[0] if task_data else {}
            
            current_enabled = task_data.get('Enabled', False)
            print(f"  Current status: {'ENABLED' if current_enabled else 'DISABLED'}")
        else:
            print(f"  [ERROR] Could not check status: {result.stderr}")
            return
    except Exception as e:
        print(f"  [ERROR] Failed to check status: {e}")
        return
    
    # Try to disable
    print(f"\n[2. ATTEMPTING TO DISABLE]")
    try:
        ps_disable = f"Disable-ScheduledTask -TaskName '{task_name}' -ErrorAction Stop"
        result = subprocess.run(
            ["powershell", "-Command", ps_disable],
            capture_output=True,
            text=True,
            timeout=10
        )
        
        if result.returncode == 0:
            print(f"  [SUCCESS] Disable command executed successfully")
            print(f"  Output: {result.stdout.strip()}")
        else:
            print(f"  [ERROR] Disable command failed")
            print(f"  Return code: {result.returncode}")
            print(f"  Error: {result.stderr.strip()}")
            print(f"  Output: {result.stdout.strip()}")
            
            if "Access is denied" in result.stderr or "denied" in result.stderr.lower():
                print(f"\n  [PERMISSION ISSUE]")
                print(f"    The backend needs to run as Administrator to disable the scheduler.")
                print(f"    Solution: Run the backend as Administrator")
    except subprocess.TimeoutExpired:
        print(f"  [ERROR] Command timed out")
    except Exception as e:
        print(f"  [ERROR] Exception: {e}")
    
    # Check status again
    print(f"\n[3. CHECKING STATUS AFTER DISABLE]")
    try:
        ps_check = f"Get-ScheduledTask -TaskName '{task_name}' -ErrorAction SilentlyContinue | Select-Object -Property TaskName, State, @{{Name='Enabled';Expression={{$_.Settings.Enabled}}}} | ConvertTo-Json -Depth 3"
        result = subprocess.run(
            ["powershell", "-Command", ps_check],
            capture_output=True,
            text=True,
            timeout=10
        )
        
        if result.returncode == 0 and result.stdout.strip():
            task_data = json.loads(result.stdout.strip())
            if isinstance(task_data, list):
                task_data = task_data[0] if task_data else {}
            
            new_enabled = task_data.get('Enabled', False)
            print(f"  New status: {'ENABLED' if new_enabled else 'DISABLED'}")
            
            if new_enabled != current_enabled:
                print(f"  [SUCCESS] Status changed from {current_enabled} to {new_enabled}")
            else:
                print(f"  [WARNING] Status did not change - disable may have failed")
    except Exception as e:
        print(f"  [ERROR] Failed to check status: {e}")

if __name__ == "__main__":
    main()









