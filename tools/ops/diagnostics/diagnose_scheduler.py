"""
Windows Task Scheduler Diagnostic Tool

Determines definitively whether the "Pipeline Runner" task is executing
and why scheduled runs may not be occurring.
"""

import subprocess
import json
import sys
from pathlib import Path
from datetime import datetime
from typing import Dict, Any, Optional


def run_powershell(command: str, timeout: int = 10) -> tuple[bool, str, str]:
    """Run PowerShell command and return (success, stdout, stderr)"""
    try:
        result = subprocess.run(
            ["powershell", "-Command", command],
            capture_output=True,
            text=True,
            timeout=timeout
        )
        return result.returncode == 0, result.stdout.strip(), result.stderr.strip()
    except subprocess.TimeoutExpired:
        return False, "", "Command timed out"
    except Exception as e:
        return False, "", str(e)


def get_task_info(task_name: str) -> Optional[Dict[str, Any]]:
    """Get detailed information about a scheduled task"""
    ps_command = f"""
    $task = Get-ScheduledTask -TaskName '{task_name}' -ErrorAction SilentlyContinue
    if ($task) {{
        $info = Get-ScheduledTaskInfo -TaskName '{task_name}'
        $action = $task.Actions[0]
        $principal = $task.Principal
        
        @{{
            Exists = $true
            State = $task.State.ToString()
            Enabled = $task.Settings.Enabled
            LastRunTime = $info.LastRunTime.ToString()
            LastTaskResult = $info.LastTaskResult
            NextRunTime = $info.NextRunTime.ToString()
            NumberOfMissedRuns = $info.NumberOfMissedRuns
            Executable = $action.Execute
            Arguments = $action.Arguments
            WorkingDirectory = $action.WorkingDirectory
            RunLevel = $principal.RunLevel.ToString()
            UserId = $principal.UserId
            LogonType = $principal.LogonType.ToString()
        }} | ConvertTo-Json -Depth 10
    }} else {{
        @{{ Exists = $false }} | ConvertTo-Json
    }}
    """
    
    success, stdout, stderr = run_powershell(ps_command)
    if not success:
        print(f"❌ Failed to get task info: {stderr}")
        return None
    
    try:
        return json.loads(stdout)
    except json.JSONDecodeError as e:
        print(f"❌ Failed to parse task info JSON: {e}")
        print(f"   Raw output: {stdout}")
        return None


def get_task_history(task_name: str, limit: int = 10) -> list[Dict[str, Any]]:
    """Get task execution history"""
    ps_command = f"""
    Get-WinEvent -FilterHashtable @{{
        LogName = 'Microsoft-Windows-TaskScheduler/Operational'
        Id = 200, 201, 202, 203
    }} -MaxEvents {limit} | Where-Object {{
        $_.Message -like '*{task_name}*'
    }} | Select-Object TimeCreated, Id, LevelDisplayName, Message | 
    Sort-Object TimeCreated -Descending |
    ConvertTo-Json -Depth 5
    """
    
    success, stdout, stderr = run_powershell(ps_command)
    if not success:
        # Try alternative method
        ps_command2 = f"""
        Get-WinEvent -LogName 'Microsoft-Windows-TaskScheduler/Operational' -MaxEvents 50 |
        Where-Object {{ $_.Message -like '*{task_name}*' }} |
        Select-Object TimeCreated, Id, LevelDisplayName, Message |
        Sort-Object TimeCreated -Descending |
        ConvertTo-Json -Depth 5
        """
        success, stdout, stderr = run_powershell(ps_command2)
    
    if not success:
        print(f"⚠️  Could not get task history: {stderr}")
        return []
    
    try:
        if stdout:
            events = json.loads(stdout)
            if isinstance(events, dict):
                return [events]
            return events
        return []
    except json.JSONDecodeError:
        print(f"⚠️  Could not parse history JSON")
        return []


def check_file_exists(file_path: Path) -> tuple[bool, Optional[Dict[str, Any]]]:
    """Check if file exists and get metadata"""
    if not file_path.exists():
        return False, None
    
    stat = file_path.stat()
    return True, {
        "exists": True,
        "size_bytes": stat.st_size,
        "modified": datetime.fromtimestamp(stat.st_mtime).isoformat(),
        "created": datetime.fromtimestamp(stat.st_ctime).isoformat()
    }


def check_recent_logs(log_dir: Path, pattern: str = "pipeline_*.log", hours: int = 24) -> list[Dict[str, Any]]:
    """Check for recent log files"""
    if not log_dir.exists():
        return []
    
    cutoff = datetime.now().timestamp() - (hours * 3600)
    logs = []
    
    for log_file in log_dir.glob(pattern):
        if log_file.stat().st_mtime > cutoff:
            logs.append({
                "file": str(log_file.name),
                "modified": datetime.fromtimestamp(log_file.stat().st_mtime).isoformat(),
                "size_bytes": log_file.stat().st_size
            })
    
    return sorted(logs, key=lambda x: x["modified"], reverse=True)


def check_recent_jsonl_events(event_logs_dir: Path, hours: int = 24) -> list[Dict[str, Any]]:
    """Check for recent JSONL event files"""
    if not event_logs_dir.exists():
        return []
    
    cutoff = datetime.now().timestamp() - (hours * 3600)
    events = []
    
    for jsonl_file in event_logs_dir.glob("pipeline_*.jsonl"):
        if jsonl_file.stat().st_mtime > cutoff:
            # Count lines
            try:
                with open(jsonl_file, 'r') as f:
                    line_count = sum(1 for _ in f)
            except:
                line_count = 0
            
            events.append({
                "file": jsonl_file.name,
                "modified": datetime.fromtimestamp(jsonl_file.stat().st_mtime).isoformat(),
                "size_bytes": jsonl_file.stat().st_size,
                "event_count": line_count
            })
    
    return sorted(events, key=lambda x: x["modified"], reverse=True)


def test_task_execution(task_name: str) -> Dict[str, Any]:
    """Test executing the task manually"""
    ps_command = f"Start-ScheduledTask -TaskName '{task_name}' -ErrorAction Stop"
    success, stdout, stderr = run_powershell(ps_command, timeout=5)
    
    return {
        "success": success,
        "stdout": stdout,
        "stderr": stderr
    }


def main():
    """Main diagnostic routine"""
    print("=" * 70)
    print("Windows Task Scheduler Diagnostic - Pipeline Runner")
    print("=" * 70)
    print()
    
    # Determine project root
    script_dir = Path(__file__).parent
    qtsw2_root = script_dir.parent
    expected_script = qtsw2_root / "automation" / "run_pipeline_standalone.py"
    
    print(f"Project root: {qtsw2_root}")
    print(f"Expected script: {expected_script}")
    print()
    
    # Task names to check
    task_names = ["Pipeline Runner", "Pipeline Run Task", "PipelineRunner", "pipeline_runner"]
    
    task_info = None
    actual_task_name = None
    
    for task_name in task_names:
        print(f"Checking task: '{task_name}'...")
        info = get_task_info(task_name)
        if info and info.get("Exists"):
            task_info = info
            actual_task_name = task_name
            print(f"✅ Found task: '{task_name}'")
            break
        else:
            print(f"   Not found")
    
    print()
    
    if not task_info:
        print("❌ TASK NOT FOUND")
        print("   The scheduled task does not exist.")
        print("   Run: batch\\SETUP_WINDOWS_SCHEDULER.bat")
        return
    
    # 1. Task Definition
    print("=" * 70)
    print("1. TASK DEFINITION")
    print("=" * 70)
    print(f"Task Name: {actual_task_name}")
    print(f"State: {task_info.get('State', 'Unknown')}")
    print(f"Enabled: {task_info.get('Enabled', 'Unknown')}")
    print(f"Executable: {task_info.get('Executable', 'Unknown')}")
    print(f"Arguments: {task_info.get('Arguments', 'Unknown')}")
    print(f"Working Directory: {task_info.get('WorkingDirectory', 'Unknown')}")
    print(f"Run Level: {task_info.get('RunLevel', 'Unknown')}")
    print(f"User ID: {task_info.get('UserId', 'Unknown')}")
    print(f"Logon Type: {task_info.get('LogonType', 'Unknown')}")
    print()
    
    # Verify script path
    executable = task_info.get('Executable', '')
    arguments = task_info.get('Arguments', '')
    working_dir = task_info.get('WorkingDirectory', '')
    
    print("Verification:")
    if 'python' in executable.lower() or 'pythonw' in executable.lower():
        print(f"  ✅ Executable is Python: {executable}")
    else:
        print(f"  ⚠️  Executable may not be Python: {executable}")
    
    if str(expected_script) in arguments or expected_script.name in arguments:
        print(f"  ✅ Arguments contain expected script")
    else:
        print(f"  ⚠️  Arguments: {arguments}")
        print(f"     Expected: {expected_script} or {expected_script.name}")
    
    if working_dir:
        working_path = Path(working_dir)
        if working_path.exists() and (working_path / "automation" / "run_pipeline_standalone.py").exists():
            print(f"  ✅ Working directory is valid: {working_dir}")
        else:
            print(f"  ⚠️  Working directory may be incorrect: {working_dir}")
    else:
        print(f"  ⚠️  No working directory specified")
    
    print()
    
    # 2. Task Execution Evidence
    print("=" * 70)
    print("2. TASK EXECUTION EVIDENCE")
    print("=" * 70)
    print(f"Last Run Time: {task_info.get('LastRunTime', 'Never')}")
    print(f"Last Task Result: {task_info.get('LastTaskResult', 'Unknown')}")
    print(f"Next Run Time: {task_info.get('NextRunTime', 'Unknown')}")
    print(f"Number of Missed Runs: {task_info.get('NumberOfMissedRuns', 'Unknown')}")
    print()
    
    # Interpret LastTaskResult
    result_code = task_info.get('LastTaskResult', 0)
    if result_code == 0:
        print("  ✅ Last run succeeded (exit code 0)")
    elif result_code == 267014:  # Task is ready to run
        print("  ⚠️  Task has never run (267014 = SCHED_S_TASK_READY)")
    elif result_code == 267015:  # Task is running
        print("  ℹ️  Task is currently running")
    else:
        print(f"  ❌ Last run failed with code: {result_code}")
        print(f"     This indicates an error occurred")
    
    print()
    
    # Get task history
    print("Recent Task History:")
    history = get_task_history(actual_task_name, limit=20)
    if history:
        for event in history[:5]:  # Show last 5 events
            time_str = event.get('TimeCreated', {}).get('DateTime', 'Unknown')
            level = event.get('LevelDisplayName', 'Unknown')
            msg = event.get('Message', '')[:100]  # Truncate
            print(f"  [{time_str}] {level}: {msg}")
    else:
        print("  No recent history found")
    
    print()
    
    # 3. Backend Correlation
    print("=" * 70)
    print("3. BACKEND CORRELATION")
    print("=" * 70)
    
    log_dir = qtsw2_root / "automation" / "logs"
    event_logs_dir = log_dir / "events"
    
    print(f"Checking for recent pipeline logs in: {log_dir}")
    recent_logs = check_recent_logs(log_dir, "pipeline_*.log", hours=24)
    if recent_logs:
        print(f"  Found {len(recent_logs)} recent log file(s):")
        for log in recent_logs[:5]:
            print(f"    - {log['file']} ({log['modified']}, {log['size_bytes']} bytes)")
    else:
        print("  ⚠️  No recent pipeline log files found")
    
    print()
    print(f"Checking for recent event files in: {event_logs_dir}")
    recent_events = check_recent_jsonl_events(event_logs_dir, hours=24)
    if recent_events:
        print(f"  Found {len(recent_events)} recent event file(s):")
        for event_file in recent_events[:5]:
            print(f"    - {event_file['file']} ({event_file['modified']}, {event_file['event_count']} events)")
    else:
        print("  ⚠️  No recent event files found")
    
    print()
    
    # 4. Environment Context
    print("=" * 70)
    print("4. ENVIRONMENT CONTEXT")
    print("=" * 70)
    
    # Check if script exists
    script_exists, script_info = check_file_exists(expected_script)
    if script_exists:
        print(f"✅ Script exists: {expected_script}")
        if script_info:
            print(f"   Modified: {script_info['modified']}")
    else:
        print(f"❌ Script missing: {expected_script}")
    
    # Check Python
    python_cmd = executable if executable else "python"
    ps_check_python = f"& '{python_cmd}' --version"
    success, stdout, stderr = run_powershell(ps_check_python)
    if success:
        print(f"✅ Python accessible: {stdout}")
    else:
        print(f"❌ Python not accessible: {stderr}")
    
    print()
    
    # 5. Failure Mode Identification
    print("=" * 70)
    print("5. FAILURE MODE IDENTIFICATION")
    print("=" * 70)
    
    if task_info.get('Enabled'):
        print("Task is ENABLED")
        if result_code == 267014:
            print("  → Task has NEVER RUN")
            print("  → Possible causes:")
            print("     - Triggers not configured correctly")
            print("     - Task conditions preventing execution")
            print("     - User account permissions")
        elif result_code != 0 and result_code != 267015:
            print(f"  → Task FAILED with code: {result_code}")
            print("  → Check task history above for error details")
        else:
            print("  → Task appears to be working")
    else:
        print("❌ Task is DISABLED")
        print("  → Enable via dashboard or Task Scheduler")
    
    print()
    
    # 6. Test Execution
    print("=" * 70)
    print("6. TEST EXECUTION")
    print("=" * 70)
    print("Attempting to run task manually...")
    test_result = test_task_execution(actual_task_name)
    if test_result["success"]:
        print("✅ Task started successfully")
        print("   Check logs in 10-30 seconds for execution evidence")
    else:
        print(f"❌ Failed to start task: {test_result['stderr']}")
    
    print()
    
    # 7. Conclusion
    print("=" * 70)
    print("7. CONCLUSION")
    print("=" * 70)
    
    if not task_info.get('Enabled'):
        print("❌ TASK IS DISABLED")
        print("   Fix: Enable the task via dashboard or Task Scheduler")
    elif result_code == 267014:
        print("❌ TASK HAS NEVER RUN")
        print("   Fix: Check triggers and conditions in Task Scheduler")
    elif result_code != 0 and result_code != 267015:
        print(f"❌ TASK FAILS WITH CODE: {result_code}")
        print("   Fix: Check task history and script execution")
    elif not recent_logs and not recent_events:
        print("⚠️  TASK MAY BE RUNNING BUT NOT PRODUCING LOGS")
        print("   Fix: Check script path and working directory")
    else:
        print("✅ TASK APPEARS TO BE WORKING")
        print("   Scheduled runs should be occurring")
    
    print()
    print("=" * 70)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\n\nDiagnostic interrupted")
        sys.exit(1)
    except Exception as e:
        print(f"\n\n❌ Diagnostic error: {e}", file=sys.stderr)
        import traceback
        traceback.print_exc()
        sys.exit(1)



