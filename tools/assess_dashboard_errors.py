"""
Comprehensive Dashboard Pipeline Error Assessment
Checks for general errors across all dashboard components
"""

import sys
import json
import subprocess
from pathlib import Path
from datetime import datetime, timedelta
from typing import Dict, List, Any, Optional

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def print_section(title: str):
    print("\n" + "="*80)
    print(title)
    print("="*80)

def print_result(test_name: str, status: str, details: str = "", error: str = ""):
    """Print test result with color coding"""
    status_symbol = "[PASS]" if status == "PASS" else "[FAIL]" if status == "FAIL" else "[WARN]"
    print(f"{status_symbol} {test_name}")
    if details:
        print(f"   {details}")
    if error:
        print(f"   ERROR: {error}")

def check_error_log() -> tuple:
    """Check for recent errors in error log"""
    error_log = qtsw2_root / "logs" / "error_log.jsonl"
    
    if not error_log.exists():
        return True, "No error log file found (no errors logged yet)"
    
    try:
        errors = []
        with open(error_log, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        error = json.loads(line)
                        errors.append(error)
                    except:
                        continue
        
        if not errors:
            return True, "Error log exists but is empty (no errors)"
        
        # Get recent errors (last 24 hours)
        recent_errors = []
        cutoff = datetime.now() - timedelta(hours=24)
        
        for error in errors[-50:]:  # Check last 50 errors
            try:
                ts_str = error.get('timestamp', '')
                if ts_str:
                    ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                    if ts.replace(tzinfo=None) >= cutoff:
                        recent_errors.append(error)
            except:
                recent_errors.append(error)  # Include if timestamp parsing fails
        
        if recent_errors:
            error_summary = {}
            for err in recent_errors:
                err_type = err.get('error_type', 'Unknown')
                component = err.get('component', 'unknown')
                key = f"{component}:{err_type}"
                error_summary[key] = error_summary.get(key, 0) + 1
            
            details = f"Found {len(recent_errors)} error(s) in last 24 hours"
            if error_summary:
                details += "\n   Error breakdown:"
                for key, count in sorted(error_summary.items(), key=lambda x: x[1], reverse=True)[:5]:
                    details += f"\n     - {key}: {count} occurrence(s)"
            
            return False, details
        else:
            return True, f"Error log has {len(errors)} historical error(s), but none in last 24 hours"
    
    except Exception as e:
        return False, f"Failed to read error log: {e}"

def check_pipeline_runs() -> tuple:
    """Check recent pipeline runs for failures"""
    events_dir = qtsw2_root / "automation" / "logs" / "events"
    
    if not events_dir.exists():
        return False, "Events directory not found"
    
    pipeline_files = sorted(
        events_dir.glob("pipeline_*.jsonl"),
        key=lambda p: p.stat().st_mtime,
        reverse=True
    )
    
    if not pipeline_files:
        return False, "No pipeline runs found"
    
    # Check last 5 runs
    recent_runs = []
    for pf in pipeline_files[:5]:
        try:
            events = []
            with open(pf, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            events.append(json.loads(line))
                        except:
                            continue
            
            if events:
                # Check final state
                final_state = None
                for event in reversed(events):
                    if event.get('stage') == 'pipeline' and event.get('event') == 'state_change':
                        data = event.get('data', {})
                        final_state = data.get('new_state', 'unknown')
                        break
                
                # If no state change found, check for success/failure events
                if not final_state:
                    if any(e.get('event') == 'success' for e in events if e.get('stage') == 'pipeline'):
                        final_state = 'success'
                    elif any(e.get('event') == 'failure' for e in events if e.get('stage') == 'pipeline'):
                        final_state = 'failed'
                    elif any(e.get('event') == 'success' for e in events if e.get('stage') == 'merger'):
                        final_state = 'success'  # If merger succeeded, pipeline succeeded
                
                # Check for failures
                failures = [e for e in events if e.get('event') == 'failure']
                
                run_id = events[0].get('run_id', 'unknown')[:8]
                recent_runs.append({
                    'run_id': run_id,
                    'state': final_state or 'unknown',
                    'failures': len(failures),
                    'file': pf.name
                })
        except Exception as e:
            continue
    
    if not recent_runs:
        return False, "Could not parse any pipeline runs"
    
    failed_runs = [r for r in recent_runs if r['state'] == 'failed']
    total_runs = len(recent_runs)
    
    if failed_runs:
        details = f"{len(failed_runs)}/{total_runs} recent run(s) failed"
        details += "\n   Failed runs:"
        for run in failed_runs:
            details += f"\n     - {run['run_id']}... ({run['failures']} failure event(s))"
        return False, details
    else:
        return True, f"All {total_runs} recent pipeline run(s) succeeded"

def check_orchestrator_state() -> tuple:
    """Check orchestrator state file for errors"""
    state_file = qtsw2_root / "automation" / "logs" / "orchestrator_state.json"
    
    if not state_file.exists():
        return False, "Orchestrator state file not found"
    
    try:
        with open(state_file, 'r', encoding='utf-8') as f:
            state = json.load(f)
        
        current_state = state.get('state', 'unknown')
        error = state.get('error')
        
        if current_state == 'failed':
            error_msg = error or "Unknown error"
            return False, f"Orchestrator is in failed state: {error_msg}"
        elif error:
            return False, f"Orchestrator has error: {error}"
        elif current_state == 'success':
            return True, f"Orchestrator state: {current_state}"
        else:
            return True, f"Orchestrator state: {current_state} (no errors)"
    
    except Exception as e:
        return False, f"Failed to read orchestrator state: {e}"

def check_backend_health() -> tuple:
    """Check if backend is running and healthy"""
    try:
        import requests
        response = requests.get("http://localhost:8000/health", timeout=2)
        if response.status_code == 200:
            return True, "Backend is running and healthy"
        else:
            return False, f"Backend returned status {response.status_code}"
    except requests.exceptions.ConnectionError:
        return False, "Backend is not running (connection refused) - this is OK if backend is not started"
    except requests.exceptions.Timeout:
        return False, "Backend health check timed out"
    except ImportError:
        return False, "requests library not available (install with: pip install requests)"
    except Exception as e:
        return False, f"Backend health check failed: {e}"

def check_syntax_errors() -> tuple:
    """Check for Python syntax errors in key files"""
    key_files = [
        qtsw2_root / "modules" / "analyzer" / "breakout_core" / "engine.py",
        qtsw2_root / "modules" / "orchestrator" / "service.py",
        qtsw2_root / "modules" / "dashboard" / "backend" / "main.py",
    ]
    
    errors = []
    for file_path in key_files:
        if not file_path.exists():
            continue
        try:
            compile(open(file_path, 'r', encoding='utf-8').read(), str(file_path), 'exec')
        except SyntaxError as e:
            errors.append(f"{file_path.name}: {e.msg} at line {e.lineno}")
        except Exception as e:
            errors.append(f"{file_path.name}: {e}")
    
    if errors:
        return False, f"Syntax errors found:\n   " + "\n   ".join(errors)
    else:
        return True, "No syntax errors in key files"

def check_file_permissions() -> tuple:
    """Check if critical directories are writable"""
    critical_dirs = [
        qtsw2_root / "automation" / "logs" / "events",
        qtsw2_root / "data" / "analyzer_temp",
        qtsw2_root / "logs",
    ]
    
    issues = []
    for dir_path in critical_dirs:
        if not dir_path.exists():
            try:
                dir_path.mkdir(parents=True, exist_ok=True)
            except Exception as e:
                issues.append(f"Cannot create {dir_path}: {e}")
        else:
            # Check if writable
            test_file = dir_path / ".write_test"
            try:
                test_file.write_text("test")
                test_file.unlink()
            except Exception as e:
                issues.append(f"{dir_path} is not writable: {e}")
    
    if issues:
        return False, "File permission issues:\n   " + "\n   ".join(issues)
    else:
        return True, "All critical directories are writable"

def check_config_files() -> tuple:
    """Check if required config files exist and are valid"""
    config_files = [
        (qtsw2_root / "configs" / "schedule.json", "schedule.json"),
    ]
    
    issues = []
    for file_path, name in config_files:
        if not file_path.exists():
            issues.append(f"{name} not found")
        else:
            try:
                with open(file_path, 'r', encoding='utf-8') as f:
                    json.load(f)
            except json.JSONDecodeError as e:
                issues.append(f"{name} has invalid JSON: {e}")
            except Exception as e:
                issues.append(f"{name} read error: {e}")
    
    if issues:
        return False, "Config file issues:\n   " + "\n   ".join(issues)
    else:
        return True, "All config files are valid"

def main():
    print("="*80)
    print("DASHBOARD PIPELINE ERROR ASSESSMENT")
    print("="*80)
    print(f"Timestamp: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"QTSW2 Root: {qtsw2_root}")
    
    results = {}
    
    # Run all checks
    print_section("1. SYNTAX ERRORS")
    passed, details = check_syntax_errors()
    results['Syntax Errors'] = passed
    print_result("Python Syntax Check", "PASS" if passed else "FAIL", details)
    
    print_section("2. ERROR LOG")
    passed, details = check_error_log()
    results['Error Log'] = passed
    print_result("Recent Errors", "PASS" if passed else "FAIL", details)
    
    print_section("3. PIPELINE RUNS")
    passed, details = check_pipeline_runs()
    results['Pipeline Runs'] = passed
    print_result("Recent Pipeline Runs", "PASS" if passed else "FAIL", details)
    
    print_section("4. ORCHESTRATOR STATE")
    passed, details = check_orchestrator_state()
    results['Orchestrator State'] = passed
    print_result("Orchestrator Status", "PASS" if passed else "FAIL", details)
    
    print_section("5. BACKEND HEALTH")
    passed, details = check_backend_health()
    results['Backend Health'] = passed
    print_result("Backend Service", "PASS" if passed else "WARN", details)
    
    print_section("6. FILE PERMISSIONS")
    passed, details = check_file_permissions()
    results['File Permissions'] = passed
    print_result("Directory Write Access", "PASS" if passed else "FAIL", details)
    
    print_section("7. CONFIG FILES")
    passed, details = check_config_files()
    results['Config Files'] = passed
    print_result("Configuration Files", "PASS" if passed else "FAIL", details)
    
    # Summary
    print_section("SUMMARY")
    total = len(results)
    passed_count = sum(1 for v in results.values() if v)
    failed_count = total - passed_count
    
    print(f"Total Checks: {total}")
    print(f"[PASS] Passed: {passed_count}")
    print(f"[FAIL] Failed: {failed_count}")
    
    if failed_count > 0:
        print("\n[WARN] ISSUES FOUND:")
        for check, result in results.items():
            if not result:
                print(f"   - {check}")
        return False
    else:
        print("\n[PASS] ALL CHECKS PASSED - No general errors detected!")
        return True

if __name__ == "__main__":
    success = main()
    sys.exit(0 if success else 1)
