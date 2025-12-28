"""
Scheduler - Windows Task Scheduler control layer (no timing logic)
"""

import json
import logging
import os
import subprocess
from pathlib import Path
from typing import Optional
from datetime import datetime, timedelta
import pytz


class Scheduler:
    """
    Control layer for Windows Task Scheduler.
    This class does NOT contain timing logic or loops.
    It only enables/disables the Windows scheduled task.
    """
    
    TASK_NAME = "Pipeline Runner"
    
    def __init__(
        self,
        logger: logging.Logger
    ):
        """
        Initialize scheduler control layer.
        
        This class provides task control only (enable/disable Windows Task Scheduler task).
        It does NOT contain timing logic, loops, or orchestration responsibilities.
        """
        self.logger = logger
        
        # Derive state file path from QTSW2_ROOT environment variable or fallback
        # This is more robust than relative path traversal which breaks if module is moved/packaged
        qtsw2_root_str = os.getenv("QTSW2_ROOT", r"C:\Users\jakej\QTSW2")
        qtsw2_root = Path(qtsw2_root_str)
        self.STATE_FILE = qtsw2_root / "automation" / "logs" / "scheduler_state.json"
        
        # Ensure state file directory exists
        self.STATE_FILE.parent.mkdir(parents=True, exist_ok=True)
    
    def _load_state(self) -> dict:
        """Load scheduler audit metadata from persistent file (not authoritative state)"""
        try:
            if self.STATE_FILE.exists():
                state = json.load(open(self.STATE_FILE, "r"))
                # Migrate old field name if present
                if "scheduler_enabled" in state and "last_requested_enabled" not in state:
                    state["last_requested_enabled"] = state.pop("scheduler_enabled")
                    # Save migrated state
                    with open(self.STATE_FILE, "w") as f:
                        json.dump(state, f, indent=2)
                return state
            else:
                # Default audit metadata
                return {
                    "last_requested_enabled": None,
                    "last_changed_timestamp": None,
                    "last_changed_by": None
                }
        except Exception as e:
            self.logger.error(f"Failed to load scheduler audit metadata: {e}")
            return {
                "last_requested_enabled": None,
                "last_changed_timestamp": None,
                "last_changed_by": None
            }
    
    def _save_state(self, enabled: bool, changed_by: str = "system"):
        """Save scheduler audit metadata to persistent file (not authoritative state)"""
        try:
            state = {
                "last_requested_enabled": enabled,
                "last_changed_timestamp": datetime.now().isoformat(),
                "last_changed_by": changed_by
            }
            with open(self.STATE_FILE, "w") as f:
                json.dump(state, f, indent=2)
            self.logger.info(f"Scheduler audit metadata saved: last_requested_enabled={enabled}, changed_by={changed_by}")
        except Exception as e:
            self.logger.error(f"Failed to save scheduler audit metadata: {e}")
    
    def is_enabled(self) -> bool:
        """
        Check if Windows Task Scheduler is enabled.
        
        Windows Task Scheduler is the source of truth.
        Returns windows_task_status["enabled"] when exists=True, else False.
        """
        windows_status = self._check_windows_task_status()
        if windows_status.get("exists") is True:
            return bool(windows_status.get("enabled", False))
        else:
            # Task doesn't exist or status check failed - treat as disabled
            return False
    
    def get_state(self) -> dict:
        """Get full scheduler state"""
        state = self._load_state()
        # Also check actual Windows Task Scheduler status
        state["windows_task_status"] = self._check_windows_task_status()
        return state
    
    def _check_windows_task_status(self) -> dict:
        """
        Check Windows Task Scheduler task status.
        
        Uses canonical task name 'Pipeline Runner' only.
        Fails loudly if task is not found (configuration error).
        """
        try:
            # Use canonical task name only - no variations
            # Use Settings.Enabled for reliable detection (Enabled property may be null)
            ps_command = f"Get-ScheduledTask -TaskName '{self.TASK_NAME}' -ErrorAction Stop | Select-Object -Property State, @{{Name='Enabled';Expression={{$_.Settings.Enabled}}}}, LastRunTime, NextRunTime | ConvertTo-Json"
            result = subprocess.run(
                ["powershell", "-Command", ps_command],
                capture_output=True,
                text=True,
                timeout=5
            )
            
            if result.returncode == 0 and result.stdout.strip():
                task_data = json.loads(result.stdout.strip())
                
                # Parse defensively: PowerShell may return an array if multiple tasks match
                # (shouldn't happen with strict TaskName, but handle it)
                if isinstance(task_data, list):
                    if len(task_data) == 0:
                        # Empty array - task not found
                        self.logger.error(f"Task '{self.TASK_NAME}' not found (empty result). Run: batch\\SETUP_WINDOWS_SCHEDULER.bat")
                        return {
                            "exists": False,
                            "enabled": False,
                            "state": "NotFound",
                            "error": f"Task '{self.TASK_NAME}' not found. Run: batch\\SETUP_WINDOWS_SCHEDULER.bat"
                        }
                    # Use first task if multiple (shouldn't happen, but be defensive)
                    if len(task_data) > 1:
                        self.logger.warning(f"Multiple tasks matched '{self.TASK_NAME}', using first result")
                    task_info = task_data[0]
                else:
                    task_info = task_data
                
                # Parse Enabled property (boolean or string representation)
                # Settings.Enabled is the authoritative source
                enabled = task_info.get("Enabled", False)
                if isinstance(enabled, str):
                    enabled = enabled.lower() in ("true", "1", "yes", "enabled")
                enabled = bool(enabled)
                
                # Parse state
                state = task_info.get("State", "Unknown")
                
                # Parse timestamps (may be None or ISO format strings)
                last_run_time = task_info.get("LastRunTime")
                next_run_time = task_info.get("NextRunTime")
                
                return {
                    "exists": True,
                    "enabled": enabled,
                    "state": state,
                    "last_run_time": last_run_time,
                    "next_run_time": next_run_time
                }
            else:
                # Task not found - configuration error
                self.logger.error(f"Task '{self.TASK_NAME}' not found. Run: batch\\SETUP_WINDOWS_SCHEDULER.bat")
                return {
                    "exists": False,
                    "enabled": False,
                    "state": "NotFound",
                    "error": f"Task '{self.TASK_NAME}' not found. Run: batch\\SETUP_WINDOWS_SCHEDULER.bat"
                }
        except subprocess.CalledProcessError:
            # Task not found - PowerShell returned error
            self.logger.error(f"Task '{self.TASK_NAME}' not found. Run: batch\\SETUP_WINDOWS_SCHEDULER.bat")
            return {
                "exists": False,
                "enabled": False,
                "state": "NotFound",
                "error": f"Task '{self.TASK_NAME}' not found. Run: batch\\SETUP_WINDOWS_SCHEDULER.bat"
            }
        except json.JSONDecodeError as e:
            self.logger.warning(f"Failed to parse Windows Task Scheduler status JSON: {e}")
            return {
                "exists": None,
                "enabled": None,
                "state": "Error",
                "error": f"JSON parse error: {str(e)}"
            }
        except Exception as e:
            self.logger.warning(f"Failed to check Windows Task Scheduler status: {e}")
            return {
                "exists": None,
                "enabled": None,
                "state": "Error",
                "error": str(e)
            }
    
    def enable(self, changed_by: str = "system") -> tuple:
        """
        Enable Windows Task Scheduler task.
        
        Uses canonical task name 'Pipeline Runner' only.
        Fails loudly if task is not found (configuration error).
        
        Returns (success: bool, error_message: str)
        """
        try:
            # Verify task exists using canonical name only
            ps_check = f"Get-ScheduledTask -TaskName '{self.TASK_NAME}' -ErrorAction Stop | Select-Object -ExpandProperty TaskName"
            check_result = subprocess.run(
                ["powershell", "-Command", ps_check],
                capture_output=True,
                text=True,
                timeout=5
            )
            
            if check_result.returncode != 0 or not check_result.stdout.strip():
                error_msg = f"Task '{self.TASK_NAME}' not found. Run: batch\\SETUP_WINDOWS_SCHEDULER.bat"
                self.logger.error(error_msg)
                return (False, error_msg)
            
            # Use PowerShell Enable-ScheduledTask (consistent toolchain with disable())
            ps_command = f"Enable-ScheduledTask -TaskName '{self.TASK_NAME}' -ErrorAction Stop"
            result = subprocess.run(
                ["powershell", "-Command", ps_command],
                capture_output=True,
                text=True,
                timeout=10
            )
            
            if result.returncode == 0:
                self._save_state(True, changed_by)
                self.logger.info(f"Windows Task Scheduler enabled (changed_by={changed_by}, task={self.TASK_NAME})")
                return (True, "")
            else:
                error_msg = result.stderr.strip() or result.stdout.strip() or "Unknown error"
                # Trim error message for display (keep last 200 chars to avoid huge messages)
                error_snippet = error_msg[-200:] if len(error_msg) > 200 else error_msg
                
                # Check if it's a permission error
                if "Access is denied" in error_msg or "denied" in error_msg.lower() or "unauthorized" in error_msg.lower():
                    full_error = (
                        f"This operation is blocked by task permissions (ACL) or insufficient privileges. "
                        f"Task ownership, principal, and ACL settings determine if enable/disable is allowed. "
                        f"Error details: {error_snippet} "
                        f"To resolve: Run the backend as administrator, or manually enable/disable the task in Task Scheduler (taskschd.msc)."
                    )
                else:
                    full_error = f"PowerShell error: {error_snippet}"
                self.logger.error(f"Failed to enable Windows Task Scheduler '{self.TASK_NAME}': {error_msg}")
                return (False, full_error)
        except subprocess.CalledProcessError:
            # Task not found
            error_msg = f"Task '{self.TASK_NAME}' not found. Run: batch\\SETUP_WINDOWS_SCHEDULER.bat"
            self.logger.error(error_msg)
            return (False, error_msg)
        except subprocess.TimeoutExpired:
            error_msg = "Timeout enabling Windows Task Scheduler (took longer than 10 seconds)"
            self.logger.error(error_msg)
            return (False, error_msg)
        except Exception as e:
            error_msg = f"Exception: {str(e)}"
            self.logger.error(f"Error enabling Windows Task Scheduler: {e}", exc_info=True)
            return (False, error_msg)
    
    def disable(self, changed_by: str = "system") -> tuple:
        """
        Disable Windows Task Scheduler task.
        
        Uses canonical task name 'Pipeline Runner' only.
        Fails loudly if task is not found (configuration error).
        Uses PowerShell consistently for both existence check and disable action.
        
        Returns (success: bool, error_message: str)
        """
        try:
            # Verify task exists using canonical name only (PowerShell, same as enable())
            ps_check = f"Get-ScheduledTask -TaskName '{self.TASK_NAME}' -ErrorAction Stop | Select-Object -ExpandProperty TaskName"
            check_result = subprocess.run(
                ["powershell", "-Command", ps_check],
                capture_output=True,
                text=True,
                timeout=5
            )
            
            if check_result.returncode != 0 or not check_result.stdout.strip():
                error_msg = f"Task '{self.TASK_NAME}' not found. Run: batch\\SETUP_WINDOWS_SCHEDULER.bat"
                self.logger.error(error_msg)
                return (False, error_msg)
            
            # Use PowerShell Disable-ScheduledTask (consistent with enable() using PowerShell)
            ps_command = f"Disable-ScheduledTask -TaskName '{self.TASK_NAME}' -ErrorAction Stop"
            result = subprocess.run(
                ["powershell", "-Command", ps_command],
                capture_output=True,
                text=True,
                timeout=10
            )
            
            if result.returncode == 0:
                self._save_state(False, changed_by)
                self.logger.info(f"Windows Task Scheduler disabled (changed_by={changed_by}, task={self.TASK_NAME})")
                return (True, "")
            else:
                error_msg = result.stderr.strip() or result.stdout.strip() or "Unknown error"
                # Trim error message for display (keep last 200 chars to avoid huge messages)
                error_snippet = error_msg[-200:] if len(error_msg) > 200 else error_msg
                
                # Check if it's a permission error
                if "Access is denied" in error_msg or "denied" in error_msg.lower() or "unauthorized" in error_msg.lower():
                    full_error = (
                        f"This operation is blocked by task permissions (ACL) or insufficient privileges. "
                        f"Task ownership, principal, and ACL settings determine if enable/disable is allowed. "
                        f"Error details: {error_snippet} "
                        f"To resolve: Run the backend as administrator, or manually enable/disable the task in Task Scheduler (taskschd.msc)."
                    )
                else:
                    full_error = f"PowerShell error: {error_snippet}"
                self.logger.error(f"Failed to disable Windows Task Scheduler '{self.TASK_NAME}': {error_msg}")
                return (False, full_error)
        except subprocess.CalledProcessError:
            # Task not found
            error_msg = f"Task '{self.TASK_NAME}' not found. Run: batch\\SETUP_WINDOWS_SCHEDULER.bat"
            self.logger.error(error_msg)
            return (False, error_msg)
        except subprocess.TimeoutExpired:
            error_msg = "Timeout disabling Windows Task Scheduler (took longer than 10 seconds)"
            self.logger.error(error_msg)
            return (False, error_msg)
        except Exception as e:
            error_msg = f"Exception: {str(e)}"
            self.logger.error(f"Error disabling Windows Task Scheduler: {e}", exc_info=True)
            return (False, error_msg)
    
    async def start(self):
        """No-op: Scheduler no longer has a background loop"""
        self.logger.info("Scheduler control layer initialized (Windows Task Scheduler control only)")
    
    async def stop(self):
        """No-op: Scheduler no longer has a background loop"""
        self.logger.info("Scheduler control layer stopped")
    
    def get_windows_schedule_info(self) -> dict:
        """
        Get actual schedule information from Windows Task Scheduler.
        
        Returns:
            dict with:
            - last_run_time: LastRunTime from Windows (or None)
            - next_run_time: NextRunTime from Windows (or None)
            - error: Error message if fetch failed (or None)
        """
        try:
            ps_command = f"Get-ScheduledTask -TaskName '{self.TASK_NAME}' -ErrorAction Stop | Get-ScheduledTaskInfo | Select-Object LastRunTime, NextRunTime | ConvertTo-Json"
            result = subprocess.run(
                ["powershell", "-Command", ps_command],
                capture_output=True,
                text=True,
                timeout=5
            )
            
            if result.returncode == 0 and result.stdout.strip():
                task_info = json.loads(result.stdout.strip())
                
                # Parse defensively: PowerShell may return an array
                if isinstance(task_info, list):
                    if len(task_info) == 0:
                        return {
                            "last_run_time": None,
                            "next_run_time": None,
                            "error": f"Task '{self.TASK_NAME}' not found"
                        }
                    task_info = task_info[0]
                
                return {
                    "last_run_time": task_info.get("LastRunTime"),
                    "next_run_time": task_info.get("NextRunTime"),
                    "error": None
                }
            else:
                return {
                    "last_run_time": None,
                    "next_run_time": None,
                    "error": f"Task '{self.TASK_NAME}' not found or command failed"
                }
        except subprocess.CalledProcessError:
            return {
                "last_run_time": None,
                "next_run_time": None,
                "error": f"Task '{self.TASK_NAME}' not found"
            }
        except json.JSONDecodeError as e:
            self.logger.warning(f"Failed to parse Windows schedule info JSON: {e}")
            return {
                "last_run_time": None,
                "next_run_time": None,
                "error": f"JSON parse error: {str(e)}"
            }
        except Exception as e:
            self.logger.warning(f"Failed to get Windows schedule info: {e}")
            return {
                "last_run_time": None,
                "next_run_time": None,
                "error": str(e)
            }
    
    def get_next_run_time(self) -> Optional[datetime]:
        """
        Get next run time (approximation if Windows info unavailable).
        
        First tries to get actual NextRunTime from Windows Task Scheduler.
        If that fails, falls back to calculating next quarter-hour in Chicago time.
        
        This is an approximation - Windows Task Scheduler may be configured
        differently (daily at specific times, repeat intervals, etc.).
        For accurate schedule info, use get_windows_schedule_info().
        
        Returns:
            Next run time (datetime) or None if unavailable
        """
        # Try to get actual schedule info from Windows first
        schedule_info = self.get_windows_schedule_info()
        next_run_time = schedule_info.get("next_run_time")
        
        if next_run_time:
            # Parse the Windows timestamp (may be ISO string or None)
            try:
                if isinstance(next_run_time, str):
                    # Parse ISO format timestamp
                    return datetime.fromisoformat(next_run_time.replace("Z", "+00:00"))
                elif next_run_time is None:
                    # Windows returned None (task may not be scheduled)
                    pass
                else:
                    # Already a datetime-like object
                    return next_run_time
            except (ValueError, AttributeError) as e:
                self.logger.debug(f"Failed to parse NextRunTime from Windows: {e}")
        
        # Fallback: Calculate approximation (next quarter-hour in Chicago time)
        # This is only an approximation - actual schedule may differ
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

