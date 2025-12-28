"""
Scheduler - Windows Task Scheduler observer and control layer.

This scheduler does NOT execute the pipeline.
It only observes and controls the external Windows Task Scheduler task.

All pipeline execution originates from:
- Windows Task Scheduler (production) - runs automation/run_pipeline_standalone.py directly
- Dashboard UI start_pipeline(manual=True) - triggers orchestrator.run_pipeline()

This module provides:
- Observation of Windows Task Scheduler task status
- Enable/disable control of the Windows Task Scheduler task
- Status inference from observed scheduler/start events in JSONL files
"""

import json
import logging
import subprocess
import os
from pathlib import Path
from typing import Optional
from datetime import datetime, timedelta
import pytz


class Scheduler:
    """
    Observer and advisory control layer for Windows Task Scheduler.
    
    Execution Authority:
    - Windows Task Scheduler is the execution authority for scheduled runs
    - This Python Scheduler is advisory only (calendar + expectations)
    - Backend never assumes it controls execution timing
    
    This class does NOT:
    - Run the pipeline itself
    - Contain timing logic or loops
    - Execute scheduled runs
    - Control when runs occur (Windows Task Scheduler does this)
    
    This class ONLY:
    - Observes Windows Task Scheduler task status
    - Enables/disables the Windows scheduled task (advisory control)
    - Provides status information inferred from observed events
    - Acts as a calendar/expectation layer (not execution authority)
    
    The pipeline runs independently via Windows Task Scheduler (external system).
    Scheduled runs publish events directly to the backend EventBus via HTTP API.
    """
    
    TASK_NAME = "Pipeline Runner"
    
    def __init__(
        self,
        config_path: Path,
        orchestrator,
        logger: logging.Logger
    ):
        self.config_path = config_path
        self.orchestrator = orchestrator
        self.logger = logger
        
        # Derive STATE_FILE from orchestrator config's known root (not fragile path traversal)
        # This ensures it works regardless of module location or packaging
        # Use orchestrator.config.qtsw2_root (preferred) or fall back to environment variable
        try:
            qtsw2_root = orchestrator.config.qtsw2_root
        except (AttributeError, KeyError):
            # Fallback to environment variable if orchestrator config is not available
            qtsw2_root_str = os.getenv("QTSW2_ROOT", r"C:\Users\jakej\QTSW2")
            qtsw2_root = Path(qtsw2_root_str)
            self.logger.warning(f"Using QTSW2_ROOT from environment: {qtsw2_root}")
        
        self.STATE_FILE = qtsw2_root / "automation" / "logs" / "scheduler_state.json"
        
        # Ensure state file directory exists
        self.STATE_FILE.parent.mkdir(parents=True, exist_ok=True)
    
    def _load_state(self) -> dict:
        """Load scheduler state from persistent file"""
        try:
            if self.STATE_FILE.exists():
                self.logger.debug(f"[SCHEDULER] Loading state from: {self.STATE_FILE}")
                with open(self.STATE_FILE, "r") as f:
                    state = json.load(f)
                    self.logger.debug(f"[SCHEDULER] Loaded state: {state}")
                    return state
            else:
                # Default state: disabled
                self.logger.debug(f"[SCHEDULER] State file does not exist, using default (disabled): {self.STATE_FILE}")
                return {
                    "scheduler_enabled": False,
                    "last_changed_timestamp": None,
                    "last_changed_by": None
                }
        except Exception as e:
            self.logger.error(f"[SCHEDULER] Failed to load scheduler state from {self.STATE_FILE}: {e}", exc_info=True)
            return {
                "scheduler_enabled": False,
                "last_changed_timestamp": None,
                "last_changed_by": None
            }
    
    def _save_state(self, enabled: bool, changed_by: str = "system"):
        """Save scheduler state to persistent file"""
        try:
            state = {
                "scheduler_enabled": enabled,
                "last_changed_timestamp": datetime.now().isoformat(),
                "last_changed_by": changed_by
            }
            self.logger.debug(f"[SCHEDULER] Saving state to: {self.STATE_FILE}")
            with open(self.STATE_FILE, "w") as f:
                json.dump(state, f, indent=2)
            self.logger.info(f"[SCHEDULER] State saved successfully: enabled={enabled}, changed_by={changed_by}, file={self.STATE_FILE}")
        except Exception as e:
            self.logger.error(f"[SCHEDULER] Failed to save scheduler state to {self.STATE_FILE}: {e}", exc_info=True)
    
    def is_enabled(self) -> bool:
        """Check if Windows Task Scheduler is enabled"""
        state = self._load_state()
        return state.get("scheduler_enabled", False)
    
    def get_state(self) -> dict:
        """
        Get full scheduler state.
        
        This method observes the current state and reports mismatches.
        It does NOT automatically re-enable the scheduler - user must explicitly enable via dashboard.
        """
        self.logger.debug(f"[SCHEDULER] Getting scheduler state...")
        state = self._load_state()
        self.logger.debug(f"[SCHEDULER] Loaded state from file: scheduler_enabled={state.get('scheduler_enabled')}, last_changed_by={state.get('last_changed_by')}")
        
        # Check actual Windows Task Scheduler status
        windows_status = self._check_windows_task_status()
        state["windows_task_status"] = windows_status
        self.logger.debug(f"[SCHEDULER] Windows Task Scheduler status: exists={windows_status.get('exists')}, enabled={windows_status.get('enabled')}, state={windows_status.get('state')}")
        
        # Log mismatch if detected (but do NOT auto-re-enable - user must explicitly enable)
        # Windows Task Scheduler can auto-disable tasks after failures, but we respect that decision
        if (state.get("scheduler_enabled", False) and 
            windows_status.get("exists", False) and 
            not windows_status.get("enabled", False)):
            # State file says enabled, but Windows shows disabled
            # This happens when Windows auto-disables the task after failures
            # Log warning but DO NOT auto-re-enable - user must explicitly enable via dashboard
            self.logger.warning(
                "[SCHEDULER] State mismatch detected: scheduler is enabled in state file but Windows Task Scheduler shows it as disabled. "
                "Windows may have auto-disabled the task after failures. "
                "Use the dashboard to explicitly re-enable the scheduler if desired."
            )
        
        return state
    
    def _check_windows_task_status(self) -> dict:
        """Check Windows Task Scheduler task status"""
        try:
            self.logger.debug(f"[SCHEDULER] Checking Windows Task Scheduler status for task: {self.TASK_NAME}")
            
            # Also check task action/arguments to validate configuration
            try:
                ps_check_action = f"(Get-ScheduledTask -TaskName '{self.TASK_NAME}' -ErrorAction SilentlyContinue).Actions[0] | Select-Object Execute, Arguments | ConvertTo-Json"
                action_result = subprocess.run(
                    ["powershell", "-Command", ps_check_action],
                    capture_output=True,
                    text=True,
                    timeout=5
                )
                if action_result.returncode == 0 and action_result.stdout.strip():
                    import json
                    action_info = json.loads(action_result.stdout.strip())
                    args = action_info.get("Arguments", "")
                    # Check if arguments are using old format (should be -m automation.run_pipeline_standalone)
                    if args and "run_pipeline_standalone.py" in args and "-m" not in args:
                        self.logger.warning(
                            f"[SCHEDULER] Task arguments appear incorrect (old format detected): '{args}'. "
                            f"Expected: '-m automation.run_pipeline_standalone'. "
                            f"Please re-run batch\\SETUP_WINDOWS_SCHEDULER.bat as Administrator to fix."
                        )
            except Exception as e:
                self.logger.debug(f"[SCHEDULER] Could not validate task arguments: {e}")
            
            # Use schtasks.exe which is more reliable for checking enabled status
            # PowerShell Get-ScheduledTask can return None/empty for Enabled property
            result = subprocess.run(
                ["schtasks", "/query", "/tn", self.TASK_NAME, "/fo", "list", "/v"],
                capture_output=True,
                text=True,
                timeout=5
            )
            
            if result.returncode == 0:
                output = result.stdout
                self.logger.debug(f"[SCHEDULER] Task query successful, parsing status...")
                # Parse output to find status
                enabled = None
                state = "Unknown"
                
                for line in output.split("\n"):
                    line = line.strip()
                    if "Scheduled Task State:" in line and enabled is None:
                        # This line contains the enabled/disabled state (highest priority)
                        state_line = line.split(":", 1)[1].strip()
                        enabled = "Enabled" in state_line and "Disabled" not in state_line
                    elif "Status:" in line and ":" in line and enabled is None:
                        # Extract state (e.g., "Ready", "Running")
                        state = line.split(":", 1)[1].strip()
                        # Status "Ready" typically means enabled
                        if "Ready" in state:
                            enabled = True
                    elif "Task Status:" in line and enabled is None:
                        # Alternative format
                        status_line = line.split(":", 1)[1].strip()
                        enabled = "Ready" in status_line or "Enabled" in status_line
                
                # If we couldn't parse from schtasks, try PowerShell as fallback
                if enabled is None:
                    ps_command = f"$task = Get-ScheduledTask -TaskName '{self.TASK_NAME}' -ErrorAction SilentlyContinue; if ($task) {{ @{{Enabled=$($task.Settings.Enabled); State='$($task.State)'}} | ConvertTo-Json }}"
                    ps_result = subprocess.run(
                        ["powershell", "-Command", ps_command],
                        capture_output=True,
                        text=True,
                        timeout=5
                    )
                    if ps_result.returncode == 0 and ps_result.stdout.strip():
                        try:
                            task_info = json.loads(ps_result.stdout.strip())
                            enabled = task_info.get("Enabled", False)
                            state = task_info.get("State", "Unknown")
                        except json.JSONDecodeError:
                            pass
                
                # Default to enabled if we still can't determine (safer default)
                if enabled is None:
                    enabled = True
                    self.logger.debug(f"[SCHEDULER] Could not determine task enabled status, defaulting to True")
                
                self.logger.debug(f"[SCHEDULER] Task status check result: exists=True, enabled={enabled}, state={state}")
                return {
                    "exists": True,
                    "enabled": enabled,
                    "state": state
                }
            else:
                # Try variations
                variations = ["Pipeline Run Task", "PipelineRunner", "pipeline_runner"]
                for variant in variations:
                    ps_command_var = f"Get-ScheduledTask -TaskName '{variant}' -ErrorAction SilentlyContinue | Select-Object -Property State, Enabled | ConvertTo-Json"
                    result_var = subprocess.run(
                        ["powershell", "-Command", ps_command_var],
                        capture_output=True,
                        text=True,
                        timeout=5
                    )
                    if result_var.returncode == 0 and result_var.stdout.strip():
                        task_info = json.loads(result_var.stdout.strip())
                        return {
                            "exists": True,
                            "enabled": task_info.get("Enabled", False),
                            "state": task_info.get("State", "Unknown"),
                            "task_name": variant
                        }
                
                self.logger.warning(f"[SCHEDULER] Task not found: {self.TASK_NAME}")
                return {
                    "exists": False,
                    "enabled": False,
                    "state": "NotFound"
                }
        except Exception as e:
            self.logger.warning(f"[SCHEDULER] Failed to check Windows Task Scheduler status: {e}", exc_info=True)
            return {
                "exists": None,
                "enabled": None,
                "state": "Error"
            }
    
    def enable(self, changed_by: str = "system") -> tuple:
        """
        Enable Windows Task Scheduler task.
        Returns (success: bool, error_message: str)
        """
        self.logger.info(f"[SCHEDULER] Enable requested (changed_by={changed_by})")
        try:
            # First check if task exists (try exact name and variations)
            task_found = False
            actual_task_name = self.TASK_NAME
            self.logger.debug(f"[SCHEDULER] Looking for task: {self.TASK_NAME}")
            
            # Try exact name first
            ps_check = f"Get-ScheduledTask -TaskName '{self.TASK_NAME}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty TaskName"
            check_result = subprocess.run(
                ["powershell", "-Command", ps_check],
                capture_output=True,
                text=True,
                timeout=5
            )
            
            if check_result.returncode == 0 and check_result.stdout.strip():
                task_found = True
                actual_task_name = check_result.stdout.strip()
                self.logger.debug(f"[SCHEDULER] Found task with exact name: {actual_task_name}")
            else:
                # Try common variations
                self.logger.debug(f"[SCHEDULER] Task not found with exact name, trying variations...")
                variations = ["Pipeline Run Task", "PipelineRunner", "pipeline_runner"]
                for variant in variations:
                    ps_check_var = f"Get-ScheduledTask -TaskName '{variant}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty TaskName"
                    check_var_result = subprocess.run(
                        ["powershell", "-Command", ps_check_var],
                        capture_output=True,
                        text=True,
                        timeout=5
                    )
                    if check_var_result.returncode == 0 and check_var_result.stdout.strip():
                        task_found = True
                        actual_task_name = check_var_result.stdout.strip()
                        self.logger.info(f"[SCHEDULER] Found task with variant name '{actual_task_name}' (expected '{self.TASK_NAME}')")
                        break
            
            if not task_found:
                error_msg = f"Task '{self.TASK_NAME}' not found. Please run: batch\\SETUP_WINDOWS_SCHEDULER.bat"
                self.logger.error(f"[SCHEDULER] Task not found: {error_msg}")
                return (False, error_msg)
            
            # Check current status before enabling
            current_status = self._check_windows_task_status()
            self.logger.info(f"[SCHEDULER] Current task status: enabled={current_status.get('enabled')}, state={current_status.get('state')}")
            
            # Use schtasks.exe /change (more reliable than PowerShell Enable-ScheduledTask)
            # schtasks.exe works better when running as admin via subprocess
            self.logger.info(f"[SCHEDULER] Executing: schtasks /change /tn \"{actual_task_name}\" /enable")
            result = subprocess.run(
                ["schtasks", "/change", "/tn", actual_task_name, "/enable"],
                capture_output=True,
                text=True,
                timeout=10
            )
            
            self.logger.debug(f"[SCHEDULER] schtasks return code: {result.returncode}")
            if result.stdout:
                self.logger.debug(f"[SCHEDULER] schtasks stdout: {result.stdout.strip()}")
            if result.stderr:
                self.logger.debug(f"[SCHEDULER] schtasks stderr: {result.stderr.strip()}")
            
            if result.returncode == 0:
                self._save_state(True, changed_by)
                self.logger.info(f"[SCHEDULER] [OK] Windows Task Scheduler ENABLED (task: {actual_task_name}, changed_by: {changed_by})")
                # Verify the change
                verify_status = self._check_windows_task_status()
                self.logger.info(f"[SCHEDULER] Verification after enable: enabled={verify_status.get('enabled')}, state={verify_status.get('state')}")
                return (True, "")
            else:
                error_msg = result.stderr.strip() or result.stdout.strip() or "Unknown error"
                # Check if it's a permission error
                if "Access is denied" in error_msg or "denied" in error_msg.lower() or "unauthorized" in error_msg.lower() or "0x80070005" in error_msg:
                    full_error = f"Permission denied. Windows Task Scheduler requires administrator privileges to enable/disable tasks. If you're already running as admin, try: schtasks /change /tn \"{actual_task_name}\" /enable (in an admin PowerShell window). Alternatively, manually enable/disable the task in Task Scheduler (taskschd.msc)."
                    self.logger.error(f"[SCHEDULER] Permission denied when enabling task '{actual_task_name}': {error_msg}")
                else:
                    full_error = f"schtasks error: {error_msg}"
                    self.logger.error(f"[SCHEDULER] Failed to enable Windows Task Scheduler '{actual_task_name}': {error_msg}")
                return (False, full_error)
        except subprocess.TimeoutExpired:
            error_msg = "Timeout enabling Windows Task Scheduler (took longer than 10 seconds)"
            self.logger.error(f"[SCHEDULER] {error_msg}")
            return (False, error_msg)
        except Exception as e:
            error_msg = f"Exception: {str(e)}"
            self.logger.error(f"[SCHEDULER] Error enabling Windows Task Scheduler: {e}", exc_info=True)
            return (False, error_msg)
    
    def disable(self, changed_by: str = "system") -> tuple:
        """
        Disable Windows Task Scheduler task.
        Returns (success: bool, error_message: str)
        """
        self.logger.info(f"[SCHEDULER] Disable requested (changed_by={changed_by})")
        try:
            # First check if task exists (try exact name and variations)
            task_found = False
            actual_task_name = self.TASK_NAME
            self.logger.debug(f"[SCHEDULER] Looking for task: {self.TASK_NAME}")
            
            # Try exact name first using schtasks
            check_result = subprocess.run(
                ["schtasks", "/query", "/tn", self.TASK_NAME],
                capture_output=True,
                text=True,
                timeout=5
            )
            
            if check_result.returncode == 0:
                task_found = True
                actual_task_name = self.TASK_NAME
                self.logger.debug(f"[SCHEDULER] Found task with exact name: {actual_task_name}")
            else:
                # Try common variations
                self.logger.debug(f"[SCHEDULER] Task not found with exact name, trying variations...")
                variations = ["Pipeline Run Task", "PipelineRunner", "pipeline_runner"]
                for variant in variations:
                    check_var_result = subprocess.run(
                        ["schtasks", "/query", "/tn", variant],
                        capture_output=True,
                        text=True,
                        timeout=5
                    )
                    if check_var_result.returncode == 0:
                        task_found = True
                        actual_task_name = variant
                        self.logger.info(f"[SCHEDULER] Found task with variant name '{actual_task_name}' (expected '{self.TASK_NAME}')")
                        break
            
            if not task_found:
                error_msg = f"Task '{self.TASK_NAME}' not found. Please run: batch\\SETUP_WINDOWS_SCHEDULER.bat"
                self.logger.error(f"[SCHEDULER] Task not found: {error_msg}")
                return (False, error_msg)
            
            # Check current status before disabling
            current_status = self._check_windows_task_status()
            self.logger.info(f"[SCHEDULER] Current task status: enabled={current_status.get('enabled')}, state={current_status.get('state')}")
            
            # Use schtasks.exe /change (more reliable than PowerShell Disable-ScheduledTask)
            # schtasks.exe works better when running as admin via subprocess
            self.logger.info(f"[SCHEDULER] Executing: schtasks /change /tn \"{actual_task_name}\" /disable")
            result = subprocess.run(
                ["schtasks", "/change", "/tn", actual_task_name, "/disable"],
                capture_output=True,
                text=True,
                timeout=10
            )
            
            self.logger.debug(f"[SCHEDULER] schtasks return code: {result.returncode}")
            if result.stdout:
                self.logger.debug(f"[SCHEDULER] schtasks stdout: {result.stdout.strip()}")
            if result.stderr:
                self.logger.debug(f"[SCHEDULER] schtasks stderr: {result.stderr.strip()}")
            
            if result.returncode == 0:
                self._save_state(False, changed_by)
                self.logger.info(f"[SCHEDULER] [OK] Windows Task Scheduler DISABLED (task: {actual_task_name}, changed_by: {changed_by})")
                # Verify the change
                verify_status = self._check_windows_task_status()
                self.logger.info(f"[SCHEDULER] Verification after disable: enabled={verify_status.get('enabled')}, state={verify_status.get('state')}")
                return (True, "")
            else:
                error_msg = result.stderr.strip() or result.stdout.strip() or "Unknown error"
                # Check if it's a permission error
                if "Access is denied" in error_msg or "denied" in error_msg.lower() or "unauthorized" in error_msg.lower() or "0x80070005" in error_msg:
                    full_error = f"Permission denied. Windows Task Scheduler requires administrator privileges to enable/disable tasks. If you're already running as admin, try: schtasks /change /tn \"{actual_task_name}\" /disable (in an admin PowerShell window). Alternatively, manually enable/disable the task in Task Scheduler (taskschd.msc)."
                    self.logger.error(f"[SCHEDULER] Permission denied when disabling task '{actual_task_name}': {error_msg}")
                else:
                    full_error = f"schtasks error: {error_msg}"
                    self.logger.error(f"[SCHEDULER] Failed to disable Windows Task Scheduler '{actual_task_name}': {error_msg}")
                return (False, full_error)
        except subprocess.TimeoutExpired:
            error_msg = "Timeout disabling Windows Task Scheduler (took longer than 10 seconds)"
            self.logger.error(f"[SCHEDULER] {error_msg}")
            return (False, error_msg)
        except Exception as e:
            error_msg = f"Exception: {str(e)}"
            self.logger.error(f"[SCHEDULER] Error disabling Windows Task Scheduler: {e}", exc_info=True)
            return (False, error_msg)
    
    async def start(self):
        """
        Initialize scheduler observer (does NOT trigger execution).
        
        This method does NOT execute the pipeline or schedule runs.
        It only initializes the observer/controller for Windows Task Scheduler.
        """
        self.logger.info("[SCHEDULER] Initializing scheduler observer (Windows Task Scheduler observation/control only, no execution)")
        # Check initial state
        initial_state = self.get_state()
        self.logger.info(f"[SCHEDULER] Initial state: scheduler_enabled={initial_state.get('scheduler_enabled')}, windows_task_enabled={initial_state.get('windows_task_status', {}).get('enabled')}")
    
    async def stop(self):
        """No-op: Scheduler no longer has a background loop"""
        self.logger.info("Scheduler control layer stopped")
    
    def get_next_run_time(self) -> Optional[datetime]:
        """
        Calculate next 15-minute run time (for display only).
        This does NOT trigger any runs - Windows Task Scheduler handles that.
        """
        chicago_tz = pytz.timezone("America/Chicago")
        now = datetime.now(chicago_tz)
        
        # Calculate next 15-minute mark
        current_minute = now.minute
        if current_minute < 15:
            next_minute = 15
        elif current_minute < 30:
            next_minute = 30
        elif current_minute < 45:
            next_minute = 45
        else:
            next_minute = 0
            now += timedelta(hours=1)
        
        next_run = now.replace(minute=next_minute, second=0, microsecond=0)
        return next_run

