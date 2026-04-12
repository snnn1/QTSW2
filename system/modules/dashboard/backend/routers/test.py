"""
Pipeline test endpoints - Sequential test runs with verification
"""
import asyncio
import logging
import uuid
import os
import subprocess
from datetime import datetime
from typing import Dict, List, Optional
from pathlib import Path
from fastapi import APIRouter, HTTPException

# Get project root
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent

# Import verification function
try:
    from .test_verification import verify_run_requirements
except ImportError:
    # Fallback for when running as script
    from test_verification import verify_run_requirements

# Import orchestrator_instance dynamically
try:
    from modules.orchestrator import PipelineRunState, PipelineStage
    def get_orchestrator():
        from ..main import orchestrator_instance
        return orchestrator_instance
except ImportError:
    import sys
    from pathlib import Path
    backend_path = Path(__file__).parent.parent
    if str(backend_path) not in sys.path:
        sys.path.insert(0, str(backend_path))
    from orchestrator import PipelineRunState, PipelineStage
    def get_orchestrator():
        from main import orchestrator_instance
        return orchestrator_instance

router = APIRouter(prefix="/api/test", tags=["test"])
logger = logging.getLogger(__name__)

# Active test sessions
_active_test_sessions: Dict[str, Dict] = {}


async def wait_for_pipeline_completion(orchestrator, run_id: str, max_wait_seconds: int = 600) -> Dict:
    """
    Wait for pipeline to complete and return final status.
    
    Returns:
        Dict with: state, run_id, error (if any), completed_at
    """
    check_interval = 2  # Check every 2 seconds
    elapsed = 0
    
    while elapsed < max_wait_seconds:
        await asyncio.sleep(check_interval)
        elapsed += check_interval
        
        status = await orchestrator.get_status()
        if status:
            if status.run_id == run_id:
                current_state = status.state
                
                # Check if completed
                if current_state in [PipelineRunState.SUCCESS, PipelineRunState.FAILED, PipelineRunState.STOPPED]:
                    return {
                        "run_id": run_id,
                        "state": current_state.value,
                        "error": status.error,
                        "completed_at": datetime.now().isoformat(),
                        "duration_seconds": elapsed
                    }
            else:
                # Different run started - our run must have completed
                logger.warning(f"Different run detected ({status.run_id} vs {run_id}), assuming previous completed")
                return {
                    "run_id": run_id,
                    "state": "unknown",
                    "error": "Run superseded by new run",
                    "completed_at": datetime.now().isoformat(),
                    "duration_seconds": elapsed
                }
        else:
            # No active run - must have completed
            return {
                "run_id": run_id,
                "state": "idle",
                "error": None,
                "completed_at": datetime.now().isoformat(),
                "duration_seconds": elapsed
            }
    
    # Timeout
    return {
        "run_id": run_id,
        "state": "timeout",
        "error": f"Pipeline did not complete within {max_wait_seconds} seconds",
        "completed_at": datetime.now().isoformat(),
        "duration_seconds": elapsed
    }




@router.post("/pipeline/sequential")
async def run_sequential_tests(count: int = 3):
    """
    Run pipeline N times sequentially with full verification.
    
    Args:
        count: Number of test runs (default: 3)
    
    Returns:
        Test session info
    """
    orchestrator = get_orchestrator()
    if orchestrator is None:
        raise HTTPException(
            status_code=503,
            detail="Pipeline orchestrator not available"
        )
    
    if count < 1 or count > 10:
        raise HTTPException(
            status_code=400,
            detail="Count must be between 1 and 10"
        )
    
    # Create test session
    session_id = str(uuid.uuid4())
    session = {
        "session_id": session_id,
        "count": count,
        "started_at": datetime.now().isoformat(),
        "runs": [],
        "status": "running",
        "current_run": 0
    }
    _active_test_sessions[session_id] = session
    
    # Start test runs in background
    asyncio.create_task(run_test_session(orchestrator, session_id, count))
    
    return {
        "session_id": session_id,
        "count": count,
        "status": "started",
        "message": f"Test session started: {count} sequential runs"
    }


async def run_test_session(orchestrator, session_id: str, count: int):
    """Run sequential test runs"""
    session = _active_test_sessions.get(session_id)
    if not session:
        logger.error(f"Test session {session_id} not found")
        return
    
    # Open test log file
    from pathlib import Path
    log_dir = orchestrator.config.event_logs_dir.parent if orchestrator.config.event_logs_dir else Path("automation/logs")
    log_dir.mkdir(parents=True, exist_ok=True)
    test_log_file_path = log_dir / f"pipeline_test_{session_id}.log"
    
    try:
        with open(test_log_file_path, "w", encoding="utf-8") as test_log_file:
            test_log_file.write("=" * 80 + "\n")
            test_log_file.write(f"PIPELINE SEQUENTIAL TEST SESSION\n")
            test_log_file.write(f"Session ID: {session_id}\n")
            test_log_file.write(f"Count: {count} runs\n")
            test_log_file.write(f"Started: {datetime.now().isoformat()}\n")
            test_log_file.write("=" * 80 + "\n\n")
            test_log_file.flush()
            
            for run_number in range(1, count + 1):
                session["current_run"] = run_number
                test_log_file.write(f"\n{'=' * 80}\n")
                test_log_file.write(f"TEST RUN {run_number}/{count}\n")
                test_log_file.write(f"{'=' * 80}\n")
                test_log_file.write(f"Started: {datetime.now().isoformat()}\n\n")
                test_log_file.flush()
                
                logger.info(f"Test session {session_id}: Starting run {run_number}/{count}")
                
                # Publish test start event
                await orchestrator.event_bus.publish({
                    "run_id": f"test_{session_id}_{run_number}",
                    "stage": "test",
                    "event": "log",
                    "timestamp": datetime.now().isoformat(),
                    "msg": f"TEST RUN {run_number}/{count} STARTING",
                    "data": {
                        "session_id": session_id,
                        "run_number": run_number,
                        "test_mode": True
                    }
                })
                
                test_log_file.write(f"[TEST] Run {run_number}/{count} starting...\n")
                test_log_file.flush()
                
                try:
                    # Start pipeline run
                    test_log_file.write(f"[TEST] Acquiring lock and starting pipeline...\n")
                    test_log_file.flush()
                    run_ctx = await orchestrator.start_pipeline(manual=True)
                    actual_run_id = run_ctx.run_id
                    test_log_file.write(f"[TEST] Pipeline started: {actual_run_id}\n")
                    test_log_file.write(f"[TEST] Waiting for completion (max 10 minutes)...\n")
                    test_log_file.flush()
                    
                    # Wait for completion
                    result = await wait_for_pipeline_completion(orchestrator, actual_run_id)
                    
                    test_log_file.write(f"[TEST] Pipeline completed: {result.get('state')}\n")
                    test_log_file.write(f"[TEST] Duration: {result.get('duration_seconds')} seconds\n")
                    if result.get('error'):
                        test_log_file.write(f"[TEST] Error: {result.get('error')}\n")
                    test_log_file.write(f"\n[TEST] Starting verification...\n")
                    test_log_file.flush()
                    
                    # Verify requirements (with log file)
                    verification = await verify_run_requirements(orchestrator, actual_run_id, run_number, test_log_file)
                    
                    # Record run result
                    run_result = {
                        "run_number": run_number,
                        "run_id": actual_run_id,
                        "started_at": run_ctx.started_at.isoformat() if run_ctx.started_at else None,
                        "completed_at": result.get("completed_at"),
                        "duration_seconds": result.get("duration_seconds"),
                        "final_state": result.get("state"),
                        "error": result.get("error"),
                        "verification": verification
                    }
                    
                    session["runs"].append(run_result)
                    
                    # Log verification summary
                    test_log_file.write(f"\n[TEST] Verification Summary:\n")
                    test_log_file.write(f"  All checks passed: {verification.get('all_checks_passed', False)}\n")
                    for check_name, check_result in verification.get("checks", {}).items():
                        status = "PASS" if check_result.get("valid", False) else "FAIL"
                        test_log_file.write(f"  {check_name}: {status}\n")
                    test_log_file.write(f"\n[TEST] Run {run_number}/{count} completed: {result.get('state')}\n")
                    test_log_file.write(f"{'=' * 80}\n\n")
                    test_log_file.flush()
                    
                    # Publish test result event
                    await orchestrator.event_bus.publish({
                        "run_id": actual_run_id,
                        "stage": "test",
                        "event": "log",
                        "timestamp": datetime.now().isoformat(),
                        "msg": f"TEST RUN {run_number}/{count} COMPLETED: {result.get('state')}",
                        "data": {
                            "session_id": session_id,
                            "run_number": run_number,
                            "result": result,
                            "verification_passed": verification.get("all_checks_passed", False)
                        }
                    })
                    
                    # Check if failed
                    if result.get("state") not in ["success", "idle"]:
                        logger.error(f"Test run {run_number} failed: {result.get('state')} - {result.get('error')}")
                        test_log_file.write(f"[TEST] FAILED: Pipeline state {result.get('state')}\n")
                        test_log_file.write(f"[TEST] Error: {result.get('error')}\n")
                        test_log_file.flush()
                        session["status"] = "failed"
                        session["failed_at_run"] = run_number
                        session["failure_reason"] = result.get("error")
                        break
                    
                    # Verify checks passed
                    if not verification.get("all_checks_passed", False):
                        logger.warning(f"Test run {run_number} verification failed")
                        test_log_file.write(f"[TEST] FAILED: Verification checks failed\n")
                        test_log_file.write(f"[TEST] See verification details above\n")
                        test_log_file.flush()
                        session["status"] = "verification_failed"
                        session["failed_at_run"] = run_number
                        session["failure_reason"] = "Verification checks failed"
                        break
                    
                    test_log_file.write(f"[TEST] Run {run_number} PASSED all checks\n")
                    test_log_file.flush()
                    
                    # Wait a moment before next run
                    await asyncio.sleep(2)
                    
                except ValueError as e:
                    # Pipeline already running or lock issue
                    error_msg = str(e)
                    logger.error(f"Test run {run_number} failed to start: {error_msg}")
                    test_log_file.write(f"[TEST] FAILED TO START: {error_msg}\n")
                    test_log_file.flush()
                    
                    run_result = {
                        "run_number": run_number,
                        "run_id": None,
                        "error": error_msg,
                        "verification": {"all_checks_passed": False, "error": error_msg}
                    }
                    session["runs"].append(run_result)
                    
                    session["status"] = "failed"
                    session["failed_at_run"] = run_number
                    session["failure_reason"] = error_msg
                    break
                    
                except Exception as e:
                    logger.error(f"Test run {run_number} exception: {e}", exc_info=True)
                    test_log_file.write(f"[TEST] EXCEPTION: {str(e)}\n")
                    test_log_file.flush()
                    
                    run_result = {
                        "run_number": run_number,
                        "run_id": None,
                        "error": str(e),
                        "verification": {"all_checks_passed": False, "error": str(e)}
                    }
                    session["runs"].append(run_result)
                    
                    session["status"] = "failed"
                    session["failed_at_run"] = run_number
                    session["failure_reason"] = str(e)
                    break
        
        # Mark session complete
        if session["status"] == "running":
            session["status"] = "completed"
        
        session["completed_at"] = datetime.now().isoformat()
        session["current_run"] = 0
        
        logger.info(f"Test session {session_id} completed: {session['status']}")
        
    except Exception as e:
        logger.error(f"Test session {session_id} error: {e}", exc_info=True)
        session["status"] = "error"
        session["error"] = str(e)
        session["completed_at"] = datetime.now().isoformat()


@router.get("/pipeline/session/{session_id}")
async def get_test_session(session_id: str):
    """Get test session status"""
    session = _active_test_sessions.get(session_id)
    if not session:
        raise HTTPException(status_code=404, detail="Test session not found")
    
    return session


@router.get("/pipeline/sessions")
async def list_test_sessions():
    """List all test sessions"""
    return {
        "sessions": list(_active_test_sessions.values()),
        "count": len(_active_test_sessions)
    }

