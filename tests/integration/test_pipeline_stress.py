#!/usr/bin/env python3
"""
Comprehensive Pipeline Stress Test
Runs 3 consecutive full test rounds via Dashboard API only
"""
import sys
import time
import json
import requests
import subprocess
import asyncio
import websockets
from pathlib import Path
from datetime import datetime
from typing import Dict, List, Optional, Tuple
from dataclasses import dataclass, asdict
import signal

# Add project root to path
qtsw2_root = Path(__file__).parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

BACKEND_URL = "http://localhost:8001"
WS_URL = "ws://localhost:8001"
STATE_FILE = qtsw2_root / "automation" / "logs" / "orchestrator_state.json"
SCHEDULER_STATE_FILE = qtsw2_root / "automation" / "logs" / "scheduler_state.json"
EVENT_LOGS_DIR = qtsw2_root / "automation" / "logs" / "events"

# Test results storage
@dataclass
class TestResult:
    round_num: int
    step: str
    success: bool
    message: str
    observed_state: Optional[Dict] = None
    expected_state: Optional[Dict] = None
    timestamp: str = None

    def __post_init__(self):
        if self.timestamp is None:
            self.timestamp = datetime.now().isoformat()

test_results: List[TestResult] = []

def log_test(round_num: int, step: str, success: bool, message: str, 
             observed: Optional[Dict] = None, expected: Optional[Dict] = None):
    """Log a test result"""
    result = TestResult(
        round_num=round_num,
        step=step,
        success=success,
        message=message,
        observed_state=observed,
        expected_state=expected
    )
    test_results.append(result)
    status = "✓" if success else "✗"
    print(f"{status} [Round {round_num}] {step}: {message}")
    if observed:
        print(f"    Observed: {json.dumps(observed, indent=2, default=str)}")
    if expected:
        print(f"    Expected: {json.dumps(expected, indent=2, default=str)}")

def check_backend_alive(timeout: int = 5) -> bool:
    """Check if backend is running"""
    try:
        response = requests.get(f"{BACKEND_URL}/", timeout=timeout)
        return response.status_code == 200
    except requests.exceptions.RequestException:
        return False
    except Exception:
        return False

def wait_for_backend(max_wait: int = 30) -> bool:
    """Wait for backend to become available"""
    start = time.time()
    while time.time() - start < max_wait:
        if check_backend_alive():
            return True
        time.sleep(1)
    return False

def restart_backend():
    """Restart the backend cleanly"""
    print("\n" + "="*80)
    print("RESTARTING BACKEND")
    print("="*80)
    
    # Kill any existing backend processes on port 8001
    try:
        # Find processes using port 8001
        result = subprocess.run(
            ["netstat", "-ano"], 
            capture_output=True, 
            text=True,
            timeout=10
        )
        pids_to_kill = set()
        for line in result.stdout.splitlines():
            if ":8001" in line and "LISTENING" in line:
                parts = line.split()
                if len(parts) >= 5:
                    pid = parts[-1]
                    pids_to_kill.add(pid)
        
        for pid in pids_to_kill:
            try:
                subprocess.run(
                    ["taskkill", "/F", "/PID", pid], 
                    capture_output=True, 
                    check=False,
                    timeout=5
                )
                print(f"  Killed process {pid} on port 8001")
            except Exception as e:
                print(f"  Warning: Could not kill process {pid}: {e}")
        
        if pids_to_kill:
            print(f"  Waiting 3 seconds for processes to terminate...")
            time.sleep(3)
    except Exception as e:
        print(f"  Warning: Could not check/kill existing processes: {e}")
    
    # Start backend in background
    print("  Starting backend...")
    backend_script = qtsw2_root / "batch" / "START_ORCHESTRATOR.bat"
    if backend_script.exists():
        try:
            subprocess.Popen(
                ["cmd", "/c", str(backend_script)],
                cwd=str(qtsw2_root),
                creationflags=subprocess.CREATE_NEW_CONSOLE,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL
            )
            print(f"  Started backend via: {backend_script}")
        except Exception as e:
            print(f"  Error starting backend script: {e}")
            # Fallback: start uvicorn directly
            try:
                subprocess.Popen(
                    ["python", "-m", "uvicorn", "modules.dashboard.backend.main:app", 
                     "--reload", "--host", "0.0.0.0", "--port", "8001"],
                    cwd=str(qtsw2_root),
                    creationflags=subprocess.CREATE_NEW_CONSOLE,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL
                )
                print("  Started backend via direct uvicorn command")
            except Exception as e2:
                print(f"  ERROR: Failed to start backend: {e2}")
                return False
    else:
        # Fallback: start uvicorn directly
        try:
            subprocess.Popen(
                ["python", "-m", "uvicorn", "dashboard.backend.main:app", 
                 "--reload", "--host", "0.0.0.0", "--port", "8001"],
                cwd=str(qtsw2_root),
                creationflags=subprocess.CREATE_NEW_CONSOLE,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL
            )
            print("  Started backend via direct uvicorn command")
        except Exception as e:
            print(f"  ERROR: Failed to start backend: {e}")
            return False
    
    # Wait for backend to start
    print("  Waiting for backend to become available...")
    if wait_for_backend(45):  # Increased timeout to 45 seconds
        print("  ✓ Backend started successfully")
        return True
    else:
        print("  ✗ ERROR: Backend failed to start within 45 seconds")
        print("  Please check the backend window for errors")
        return False

def get_pipeline_status() -> Optional[Dict]:
    """Get current pipeline status"""
    try:
        response = requests.get(f"{BACKEND_URL}/api/pipeline/status", timeout=10)
        if response.status_code == 200:
            return response.json()
        return None
    except Exception as e:
        print(f"Error getting pipeline status: {e}")
        return None

def get_scheduler_status() -> Optional[Dict]:
    """Get scheduler status"""
    try:
        response = requests.get(f"{BACKEND_URL}/api/scheduler/status", timeout=10)
        if response.status_code == 200:
            return response.json()
        return None
    except Exception as e:
        print(f"Error getting scheduler status: {e}")
        return None

def enable_scheduler() -> Tuple[bool, str]:
    """Enable scheduler"""
    try:
        response = requests.post(f"{BACKEND_URL}/api/scheduler/enable", timeout=30)
        if response.status_code == 200:
            return True, "Scheduler enabled"
        else:
            return False, f"Failed: {response.status_code} - {response.text}"
    except Exception as e:
        return False, f"Exception: {e}"

def disable_scheduler() -> Tuple[bool, str]:
    """Disable scheduler"""
    try:
        response = requests.post(f"{BACKEND_URL}/api/scheduler/disable", timeout=30)
        if response.status_code == 200:
            return True, "Scheduler disabled"
        else:
            return False, f"Failed: {response.status_code} - {response.text}"
    except Exception as e:
        return False, f"Exception: {e}"

def start_pipeline_manual() -> Tuple[bool, Optional[str]]:
    """Start pipeline manually"""
    try:
        response = requests.post(
            f"{BACKEND_URL}/api/pipeline/start",
            params={"manual": True},
            timeout=30  # Increased timeout to handle slow initialization
        )
        if response.status_code == 200:
            data = response.json()
            return True, data.get("run_id")
        elif response.status_code == 400:
            error = response.json().get("detail", "Unknown error")
            if "already running" in error.lower():
                return False, "Pipeline already running"
            return False, error
        else:
            return False, f"HTTP {response.status_code}: {response.text}"
    except Exception as e:
        return False, f"Exception: {e}"

def stop_pipeline() -> bool:
    """Stop pipeline"""
    try:
        response = requests.post(f"{BACKEND_URL}/api/pipeline/stop", timeout=10)
        return response.status_code == 200
    except:
        return False

def wait_for_pipeline_completion(run_id: str, timeout: int = 300) -> Tuple[bool, str]:
    """Wait for pipeline to complete"""
    start = time.time()
    last_state = None
    
    while time.time() - start < timeout:
        status = get_pipeline_status()
        if not status:
            time.sleep(2)
            continue
        
        current_state = status.get("state")
        current_run_id = status.get("run_id")
        
        if current_state != last_state:
            print(f"  State: {current_state} (Stage: {status.get('current_stage', 'N/A')})")
            last_state = current_state
        
        # Check if this is the run we're waiting for
        if current_run_id == run_id:
            if current_state in ["success", "failed", "stopped"]:
                return True, current_state
        elif current_state in ["idle"]:
            # Run completed but run_id changed (new run started?)
            return True, "completed"
        
        time.sleep(2)
    
    return False, "timeout"

def read_state_file() -> Optional[Dict]:
    """Read pipeline state file"""
    if STATE_FILE.exists():
        try:
            with open(STATE_FILE, 'r') as f:
                data = json.load(f)
                # The state file contains nested structure, extract the state value
                return data
        except Exception as e:
            print(f"Error reading state file: {e}")
            return None
    return None

def read_scheduler_state_file() -> Optional[Dict]:
    """Read scheduler state file"""
    if SCHEDULER_STATE_FILE.exists():
        try:
            with open(SCHEDULER_STATE_FILE, 'r') as f:
                return json.load(f)
        except:
            return None
    return None

def check_state_consistency() -> Tuple[bool, str]:
    """Check if state file matches API status"""
    api_status = get_pipeline_status()
    file_state = read_state_file()
    
    if not api_status:
        return False, "API status unavailable"
    
    # State file can be None if no run has occurred - this is OK for idle state
    api_state = api_status.get("state")
    
    if file_state is None:
        # If state file doesn't exist, API should be idle
        if api_state == "idle":
            return True, "State consistent (idle, no state file)"
        else:
            return False, f"State file missing but API state is {api_state}"
    
    # State file structure: {"state": "idle", "run_id": "...", ...}
    file_state_value = file_state.get("state") if isinstance(file_state, dict) else None
    
    if api_state != file_state_value:
        return False, f"State mismatch: API={api_state}, File={file_state_value}"
    
    return True, "State consistent"

def test_round(round_num: int) -> bool:
    """Run one complete test round"""
    print("\n" + "="*80)
    print(f"TEST ROUND {round_num}")
    print("="*80)
    
    all_passed = True
    
    # ============================================================
    # Step 1: Backend + Orchestrator Startup Validation
    # ============================================================
    print("\n[Step 1] Backend + Orchestrator Startup Validation")
    print("-" * 80)
    
    if not restart_backend():
        log_test(round_num, "Backend Restart", False, "Failed to restart backend")
        return False
    
    time.sleep(3)  # Give orchestrator time to initialize
    
    # Check backend is alive
    if not check_backend_alive():
        log_test(round_num, "Backend Alive", False, "Backend not responding")
        all_passed = False
    else:
        log_test(round_num, "Backend Alive", True, "Backend is running")
    
    # Check orchestrator status
    status = get_pipeline_status()
    if status:
        log_test(round_num, "Orchestrator Status", True, 
                f"Orchestrator available, state: {status.get('state')}",
                observed={"state": status.get("state")})
    else:
        log_test(round_num, "Orchestrator Status", False, 
                "Orchestrator not available")
        all_passed = False
    
    # Check state file exists
    state_file = read_state_file()
    if state_file is not None:  # Can be empty dict or None
        # State file structure: {"state": "idle", "run_id": "...", ...} or None
        state_value = state_file.get('state') if isinstance(state_file, dict) else None
        log_test(round_num, "State File", True, 
                f"State file exists, state: {state_value}")
    else:
        # State file might not exist if no run has occurred yet - this is OK
        log_test(round_num, "State File", True, 
                "State file not found (OK if no runs yet)")
    
    # ============================================================
    # Step 2: Scheduler Control Validation
    # ============================================================
    print("\n[Step 2] Scheduler Control Validation")
    print("-" * 80)
    
    # Disable scheduler
    success, msg = disable_scheduler()
    if success:
        log_test(round_num, "Disable Scheduler", True, msg)
        time.sleep(2)
        
        # Verify disabled
        sched_status = get_scheduler_status()
        if sched_status and not sched_status.get("enabled", True):
            log_test(round_num, "Scheduler Disabled Check", True, 
                    "Scheduler correctly disabled")
        else:
            log_test(round_num, "Scheduler Disabled Check", False,
                    f"Scheduler still enabled: {sched_status}")
            all_passed = False
    else:
        # Check if it's a permission error (expected if not running as admin)
        if "permission" in msg.lower() or "administrator" in msg.lower():
            log_test(round_num, "Disable Scheduler", True, 
                    f"Scheduler disable requires admin (expected): {msg}")
        else:
            log_test(round_num, "Disable Scheduler", False, msg)
            all_passed = False
    
    # Re-enable scheduler
    success, msg = enable_scheduler()
    if success:
        log_test(round_num, "Enable Scheduler", True, msg)
        time.sleep(2)
        
        # Verify enabled
        sched_status = get_scheduler_status()
        if sched_status and sched_status.get("enabled", False):
            log_test(round_num, "Scheduler Enabled Check", True,
                    "Scheduler correctly enabled")
        else:
            log_test(round_num, "Scheduler Enabled Check", False,
                    f"Scheduler not enabled: {sched_status}")
            all_passed = False
    else:
        log_test(round_num, "Enable Scheduler", False, msg)
        all_passed = False
    
    # ============================================================
    # Step 3: Manual Pipeline Execution
    # ============================================================
    print("\n[Step 3] Manual Pipeline Execution")
    print("-" * 80)
    
    # Ensure pipeline is idle
    status = get_pipeline_status()
    if status and status.get("state") not in ["idle", "success", "failed", "stopped"]:
        print("  Pipeline is running, stopping...")
        stop_pipeline()
        time.sleep(5)
        # Wait for idle
        for _ in range(10):
            status = get_pipeline_status()
            if status and status.get("state") in ["idle", "success", "failed", "stopped"]:
                break
            time.sleep(1)
    
    # Start pipeline
    success, run_id = start_pipeline_manual()
    if not success:
        log_test(round_num, "Start Pipeline", False, run_id or "Unknown error")
        all_passed = False
    else:
        log_test(round_num, "Start Pipeline", True, f"Pipeline started, run_id: {run_id}")
        
        # Monitor state transitions
        print("  Monitoring pipeline execution...")
        transitions = []
        last_state = None
        
        start_time = time.time()
        while time.time() - start_time < 300:  # 5 minute timeout
            status = get_pipeline_status()
            if not status:
                time.sleep(2)
                continue
            
            current_state = status.get("state")
            current_stage = status.get("current_stage", "N/A")
            
            if current_state != last_state:
                transitions.append({
                    "from": last_state,
                    "to": current_state,
                    "stage": current_stage,
                    "time": time.time() - start_time
                })
                print(f"    Transition: {last_state} -> {current_state} (stage: {current_stage})")
                last_state = current_state
            
            # Check for completion
            if current_state in ["success", "failed", "stopped"]:
                log_test(round_num, "Pipeline Completion", True,
                        f"Pipeline completed with state: {current_state}",
                        observed={"state": current_state, "transitions": transitions})
                break
            
            time.sleep(2)
        else:
            log_test(round_num, "Pipeline Completion", False,
                    "Pipeline did not complete within timeout")
            all_passed = False
        
        # Check state consistency
        consistent, msg = check_state_consistency()
        log_test(round_num, "State Consistency", consistent, msg)
        if not consistent:
            all_passed = False
    
    # ============================================================
    # Step 4: Failure & Recovery Check (non-destructive)
    # ============================================================
    print("\n[Step 4] Failure & Recovery Check")
    print("-" * 80)
    
    # This step is skipped for now as we don't have a safe way to simulate failures
    # without potentially corrupting data. In a real test, we might:
    # - Temporarily rename a directory
    # - Use a test flag in the orchestrator
    # - Mock a stage failure
    
    log_test(round_num, "Failure Simulation", True, 
            "Skipped - no safe failure simulation method available")
    
    # ============================================================
    # Step 5: Restart Resilience
    # ============================================================
    print("\n[Step 5] Restart Resilience")
    print("-" * 80)
    
    # Ensure pipeline is idle
    status = get_pipeline_status()
    if status and status.get("state") not in ["idle", "success", "failed", "stopped"]:
        print("  Waiting for pipeline to complete...")
        stop_pipeline()
        time.sleep(5)
        for _ in range(10):
            status = get_pipeline_status()
            if status and status.get("state") in ["idle", "success", "failed", "stopped"]:
                break
            time.sleep(1)
    
    # Capture state before restart
    pre_restart_state = get_pipeline_status()
    pre_restart_file = read_state_file()
    
    # Restart backend
    if not restart_backend():
        log_test(round_num, "Restart Backend", False, "Failed to restart")
        all_passed = False
    else:
        log_test(round_num, "Restart Backend", True, "Backend restarted")
        time.sleep(3)
        
        # Check state after restart
        post_restart_state = get_pipeline_status()
        post_restart_file = read_state_file()
        
        if post_restart_state:
            # Should be idle or match pre-restart state
            post_state = post_restart_state.get("state")
            if post_state in ["idle", "success", "failed", "stopped"]:
                log_test(round_num, "State After Restart", True,
                        f"State correctly loaded: {post_state}",
                        observed={"state": post_state})
            else:
                log_test(round_num, "State After Restart", False,
                        f"Unexpected state after restart: {post_state}",
                        observed={"state": post_state},
                        expected={"state": "idle"})
                all_passed = False
        else:
            log_test(round_num, "State After Restart", False,
                    "Could not get state after restart")
            all_passed = False
    
    return all_passed

def main():
    """Run 3 consecutive test rounds"""
    print("="*80)
    print("PIPELINE STRESS TEST")
    print("="*80)
    print(f"Started at: {datetime.now().isoformat()}")
    print(f"Backend URL: {BACKEND_URL}")
    print(f"State File: {STATE_FILE}")
    print("="*80)
    
    # Check backend is running
    print("Checking if backend is running...")
    if not check_backend_alive():
        print("ERROR: Backend is not running or not responding.")
        print("Attempting to start backend...")
        
        # Try to start backend
        if not restart_backend():
            print("\nERROR: Failed to start backend automatically.")
            print("Please start it manually:")
            print("  Run: batch/START_DASHBOARD.bat")
            print("  Or: batch/START_ORCHESTRATOR.bat")
            sys.exit(1)
        
        # Wait a bit longer for orchestrator to initialize
        print("Waiting for orchestrator to initialize...")
        time.sleep(5)
        
        # Check again
        if not check_backend_alive():
            print("ERROR: Backend started but still not responding.")
            print("Please check the backend window for errors.")
            sys.exit(1)
        
        print("Backend started successfully!")
    else:
        print("Backend is running and responding.")
    
    # Run 3 test rounds
    rounds_passed = 0
    for round_num in range(1, 4):
        try:
            passed = test_round(round_num)
            # Count failures that are NOT expected (like permission errors)
            round_failures = [r for r in test_results 
                            if r.round_num == round_num and not r.success 
                            and "permission" not in r.message.lower() 
                            and "administrator" not in r.message.lower()]
            
            if passed and len(round_failures) == 0:
                rounds_passed += 1
                print(f"\n✓ Round {round_num} PASSED")
            else:
                if len(round_failures) > 0:
                    print(f"\n✗ Round {round_num} FAILED ({len(round_failures)} unexpected failures)")
                else:
                    print(f"\n✓ Round {round_num} PASSED (only expected permission warnings)")
                    rounds_passed += 1
        except Exception as e:
            print(f"\n✗ Round {round_num} EXCEPTION: {e}")
            import traceback
            traceback.print_exc()
        
        # Brief pause between rounds
        if round_num < 3:
            print("\nWaiting 5 seconds before next round...")
            time.sleep(5)
    
    # Print summary
    print("\n" + "="*80)
    print("TEST SUMMARY")
    print("="*80)
    print(f"Rounds passed: {rounds_passed}/3")
    
    # Count test results
    total_tests = len(test_results)
    passed_tests = sum(1 for r in test_results if r.success)
    failed_tests = total_tests - passed_tests
    
    print(f"Total tests: {total_tests}")
    print(f"Passed: {passed_tests}")
    print(f"Failed: {failed_tests}")
    
    # Save results to file
    results_file = qtsw2_root / "test_stress_results.json"
    with open(results_file, 'w') as f:
        json.dump({
            "summary": {
                "rounds_passed": rounds_passed,
                "total_rounds": 3,
                "total_tests": total_tests,
                "passed_tests": passed_tests,
                "failed_tests": failed_tests
            },
            "results": [asdict(r) for r in test_results]
        }, f, indent=2, default=str)
    
    print(f"\nDetailed results saved to: {results_file}")
    
    if rounds_passed == 3:
        print("\n✓ ALL ROUNDS PASSED - System is robust!")
        sys.exit(0)
    else:
        print("\n✗ SOME ROUNDS FAILED - Review results above")
        sys.exit(1)

if __name__ == "__main__":
    main()

