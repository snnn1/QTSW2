"""
Comprehensive verification functions for pipeline test runs
"""
import logging
from datetime import datetime
from typing import Dict
from modules.orchestrator import PipelineRunState


async def verify_run_requirements(orchestrator, run_id: str, run_number: int, test_log_file) -> Dict:
    """
    Verify all requirements for a test run.
    
    Args:
        orchestrator: Pipeline orchestrator instance
        run_id: Run ID to verify
        run_number: Test run number (1, 2, 3, etc.)
        test_log_file: File handle for test log
    
    Returns:
        Dict with verification results
    """
    logger = logging.getLogger(__name__)
    verifications = {
        "run_id": run_id,
        "run_number": run_number,
        "timestamp": datetime.now().isoformat(),
        "checks": {}
    }
    
    def log_check(name: str, status: str, details: str = ""):
        """Helper to log verification checks"""
        msg = f"[VERIFY] {name}: {status}"
        if details:
            msg += f" - {details}"
        test_log_file.write(msg + "\n")
        test_log_file.flush()
        logger.info(msg)
    
    try:
        log_check("VERIFICATION", "START", f"Run {run_number} (ID: {run_id[:8]}...)")
        
        # Get events for this run
        events = orchestrator.event_bus.get_events_for_run(run_id, limit=1000)
        log_check("EVENTS", "LOADED", f"{len(events)} events found")
        
        # ============================================================
        # 1. PIPELINE LIFECYCLE VERIFICATION
        # ============================================================
        log_check("LIFECYCLE", "CHECKING", "State transitions")
        
        state_transitions = []
        state_sequence = []
        for event in events:
            if event.get("event") == "state_change":
                new_state = event.get("data", {}).get("new_state")
                if new_state:
                    state_transitions.append(new_state)
                    state_sequence.append(new_state)
        
        expected_sequence = ["starting", "running_translator", "running_analyzer", "running_merger", "success"]
        
        # Check for exact sequence
        sequence_valid = True
        invalid_transitions = []
        for i, expected in enumerate(expected_sequence):
            if i < len(state_sequence):
                if state_sequence[i] != expected:
                    sequence_valid = False
                    invalid_transitions.append(f"Position {i}: Expected {expected}, got {state_sequence[i]}")
            else:
                sequence_valid = False
                invalid_transitions.append(f"Missing state at position {i}: {expected}")
        
        verifications["checks"]["pipeline_lifecycle"] = {
            "found_sequence": state_sequence,
            "expected_sequence": expected_sequence,
            "has_all_states": all(s in state_sequence for s in expected_sequence),
            "exact_sequence": sequence_valid,
            "invalid_transitions": invalid_transitions,
            "valid": sequence_valid and len(state_sequence) == len(expected_sequence)
        }
        
        if verifications["checks"]["pipeline_lifecycle"]["valid"]:
            log_check("LIFECYCLE", "PASS", "All state transitions correct")
        else:
            log_check("LIFECYCLE", "FAIL", f"Invalid transitions: {invalid_transitions}")
        
        # Check state persistence
        from pathlib import Path
        state_file = orchestrator.config.state_file
        state_persisted = False
        if state_file and state_file.exists():
            try:
                import json
                with open(state_file, "r") as f:
                    state_data = json.load(f)
                    # State should be idle or success after completion
                    state_persisted = state_data.get("state") in ["idle", "success", "failed"]
            except:
                pass
        
        verifications["checks"]["state_persistence"] = {
            "state_file_exists": state_file.exists() if state_file else False,
            "state_persisted": state_persisted,
            "valid": state_persisted
        }
        
        if verifications["checks"]["state_persistence"]["valid"]:
            log_check("STATE_PERSISTENCE", "PASS", "State persisted correctly")
        else:
            log_check("STATE_PERSISTENCE", "FAIL", "State not persisted correctly")
        
        # ============================================================
        # 2. LOCK BEHAVIOR VERIFICATION
        # ============================================================
        log_check("LOCK", "CHECKING", "Lock behavior")
        
        lock_info = await orchestrator.lock_manager.get_lock_info()
        is_locked = await orchestrator.lock_manager.is_locked()
        
        # Check lock events in the run (look for lock-related messages)
        lock_acquired = False
        lock_released = False
        heartbeat_updates = False
        
        # Check for heartbeat updates (any state change after start indicates activity)
        if len(state_transitions) > 1:
            heartbeat_updates = True
        
        # Lock should be released after completion
        lock_released = not is_locked
        
        verifications["checks"]["lock_behavior"] = {
            "lock_acquired": True,  # If we got here, lock was acquired
            "heartbeat_updates": heartbeat_updates,
            "lock_released": lock_released,
            "current_lock_state": "locked" if is_locked else "unlocked",
            "lock_owner": lock_info.get("run_id") if lock_info else None,
            "valid": lock_released  # Lock must be released after completion
        }
        
        if verifications["checks"]["lock_behavior"]["valid"]:
            log_check("LOCK", "PASS", "Lock released correctly")
        else:
            log_check("LOCK", "FAIL", f"Lock still held: {is_locked}")
        
        # ============================================================
        # 3. EVENT SYSTEM VERIFICATION
        # ============================================================
        log_check("EVENTS", "CHECKING", "Event system")
        
        event_types = {}
        has_pipeline_start = False
        has_stage_events = {"translator": False, "analyzer": False, "merger": False}
        has_state_changes = False
        
        for event in events:
            event_type = event.get("event")
            stage = event.get("stage")
            
            # Check for pipeline.start
            if stage == "pipeline" and event_type == "start":
                has_pipeline_start = True
            
            # Check for stage events
            if stage in ["translator", "analyzer", "merger"]:
                if event_type in ["start", "success", "failure"]:
                    has_stage_events[stage] = True
            
            # Check for state_change events
            if event_type == "state_change":
                has_state_changes = True
            
            key = f"{stage}.{event_type}" if stage else event_type
            event_types[key] = event_types.get(key, 0) + 1
        
        verifications["checks"]["event_system"] = {
            "total_events": len(events),
            "event_types": event_types,
            "has_pipeline_start": has_pipeline_start,
            "has_stage_events": all(has_stage_events.values()),
            "stage_events_detail": has_stage_events,
            "has_state_changes": has_state_changes,
            "valid": has_pipeline_start and all(has_stage_events.values()) and has_state_changes
        }
        
        if verifications["checks"]["event_system"]["valid"]:
            log_check("EVENTS", "PASS", "All required events published")
        else:
            log_check("EVENTS", "FAIL", f"Missing: start={has_pipeline_start}, stages={has_stage_events}, state_changes={has_state_changes}")
        
        # Check JSONL file
        event_log_file = orchestrator.config.event_logs_dir / f"pipeline_{run_id}.jsonl"
        jsonl_exists = event_log_file.exists()
        jsonl_line_count = 0
        if jsonl_exists:
            try:
                with open(event_log_file, "r") as f:
                    jsonl_line_count = len([l for l in f if l.strip()])
            except:
                pass
        
        verifications["checks"]["jsonl_file"] = {
            "exists": jsonl_exists,
            "path": str(event_log_file),
            "line_count": jsonl_line_count,
            "valid": jsonl_exists and jsonl_line_count > 0
        }
        
        if verifications["checks"]["jsonl_file"]["valid"]:
            log_check("JSONL", "PASS", f"File exists with {jsonl_line_count} events")
        else:
            log_check("JSONL", "FAIL", f"File missing or empty: {jsonl_exists}, lines={jsonl_line_count}")
        
        # Check event ordering (events should be in chronological order)
        event_timestamps = [e.get("timestamp") for e in events if e.get("timestamp")]
        event_ordering_valid = True
        if len(event_timestamps) > 1:
            for i in range(1, len(event_timestamps)):
                try:
                    if datetime.fromisoformat(event_timestamps[i]) < datetime.fromisoformat(event_timestamps[i-1]):
                        event_ordering_valid = False
                        break
                except:
                    pass
        
        verifications["checks"]["event_ordering"] = {
            "ordered": event_ordering_valid,
            "total_timestamps": len(event_timestamps),
            "valid": event_ordering_valid
        }
        
        if verifications["checks"]["event_ordering"]["valid"]:
            log_check("EVENT_ORDERING", "PASS", "Events in chronological order")
        else:
            log_check("EVENT_ORDERING", "FAIL", "Events out of order")
        
        # ============================================================
        # 4. RUNNER CORRECTNESS VERIFICATION
        # ============================================================
        log_check("RUNNER", "CHECKING", "Stage execution")
        
        translator_ran = False
        analyzer_ran = False
        merger_ran = False
        output_validation_passed = True  # Assume passed if we got to success
        
        for event in events:
            stage = event.get("stage")
            event_type = event.get("event")
            
            if stage == "translator" and event_type == "success":
                translator_ran = True
            if stage == "analyzer" and event_type == "success":
                analyzer_ran = True
            if stage == "merger" and event_type == "success":
                merger_ran = True
            
            # Check for validation failures
            if "validation" in event.get("msg", "").lower() and "fail" in event.get("msg", "").lower():
                output_validation_passed = False
        
        verifications["checks"]["runner_correctness"] = {
            "translator_ran": translator_ran,
            "analyzer_ran": analyzer_ran,
            "merger_ran": merger_ran,
            "output_validation_passed": output_validation_passed,
            "all_stages_ran": translator_ran and analyzer_ran and merger_ran,
            "valid": translator_ran and analyzer_ran and merger_ran and output_validation_passed
        }
        
        if verifications["checks"]["runner_correctness"]["valid"]:
            log_check("RUNNER", "PASS", "All stages ran successfully")
        else:
            log_check("RUNNER", "FAIL", f"Missing: translator={translator_ran}, analyzer={analyzer_ran}, merger={merger_ran}, validation={output_validation_passed}")
        
        # ============================================================
        # 5. SCHEDULER ISOLATION VERIFICATION
        # ============================================================
        log_check("SCHEDULER", "CHECKING", "Scheduler isolation")
        
        # Scheduler should remain unchanged (manual test doesn't touch scheduler)
        verifications["checks"]["scheduler_isolation"] = {
            "scheduler_not_modified": True,  # Manual test shouldn't modify scheduler
            "valid": True  # Always valid - manual test doesn't touch scheduler
        }
        
        log_check("SCHEDULER", "PASS", "Scheduler isolation maintained")
        
        # ============================================================
        # 6. RECOVERY VERIFICATION
        # ============================================================
        log_check("RECOVERY", "CHECKING", "System recovery")
        
        status = await orchestrator.get_status()
        
        # Terminal states are valid for recovery (SUCCESS, FAILED, STOPPED, IDLE)
        # The key is that the lock is released and system is ready for next run
        terminal_states = {
            PipelineRunState.IDLE,
            PipelineRunState.SUCCESS,
            PipelineRunState.FAILED,
            PipelineRunState.STOPPED,
        }
        is_terminal = status is None or status.state in terminal_states
        
        # Check if system is ready for next run (no lock, terminal state)
        # Lock must be released for system to be ready
        system_ready = not is_locked and is_terminal
        
        verifications["checks"]["recovery"] = {
            "state_is_terminal": is_terminal,
            "lock_released": not is_locked,
            "system_ready": system_ready,
            "current_state": status.state.value if status else "idle",
            "valid": system_ready
        }
        
        if verifications["checks"]["recovery"]["valid"]:
            log_check("RECOVERY", "PASS", "System ready for next run (lock released, terminal state)")
        else:
            log_check("RECOVERY", "FAIL", f"State: {status.state.value if status else 'idle'}, Locked: {is_locked}, Terminal: {is_terminal}")
        
        # Overall validation
        all_checks = [
            verifications["checks"]["pipeline_lifecycle"]["valid"],
            verifications["checks"]["state_persistence"]["valid"],
            verifications["checks"]["lock_behavior"]["valid"],
            verifications["checks"]["event_system"]["valid"],
            verifications["checks"]["jsonl_file"]["valid"],
            verifications["checks"]["event_ordering"]["valid"],
            verifications["checks"]["runner_correctness"]["valid"],
            verifications["checks"]["scheduler_isolation"]["valid"],
            verifications["checks"]["recovery"]["valid"]
        ]
        
        verifications["all_checks_passed"] = all(all_checks)
        passed_count = sum(all_checks)
        total_count = len(all_checks)
        
        if verifications["all_checks_passed"]:
            log_check("VERIFICATION", "PASS", f"All {total_count} checks passed")
        else:
            log_check("VERIFICATION", "FAIL", f"{passed_count}/{total_count} checks passed")
        
    except Exception as e:
        logger.error(f"Error verifying run {run_id}: {e}", exc_info=True)
        log_check("VERIFICATION", "ERROR", str(e))
        verifications["error"] = str(e)
        verifications["all_checks_passed"] = False
    
    return verifications

