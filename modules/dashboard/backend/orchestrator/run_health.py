"""
Run Health Model - Pure function to compute pipeline health from run history

No I/O. No logging. No side effects.
Deterministic and explainable.
"""

from enum import Enum
from typing import List, Tuple, Optional
from .run_history import RunHistory, RunSummary, RunResult


class RunHealth(Enum):
    """Pipeline run health states"""
    HEALTHY = "healthy"
    DEGRADED = "degraded"
    UNSTABLE = "unstable"
    BLOCKED = "blocked"


def compute_run_health(run_history: RunHistory) -> Tuple[RunHealth, List[str]]:
    """
    Compute current pipeline health from run history.
    
    Pure function - no I/O, no logging, no side effects.
    Deterministic and explainable.
    
    Args:
        run_history: RunHistory instance to analyze
        
    Returns:
        Tuple of (health, reasons)
        - health: RunHealth enum value
        - reasons: List of explanation strings
    """
    reasons: List[str] = []
    
    # Get recent runs (last 10 for analysis)
    recent_runs = run_history.list_runs(limit=10)
    
    if not recent_runs:
        # No history - assume healthy
        return (RunHealth.HEALTHY, ["No run history - assuming healthy"])
    
    # Get most recent run
    last_run = recent_runs[0]
    
    # Rule 1: BLOCKED - Last run ended with force lock clear
    # Check metadata for force lock clear indicator
    if last_run.metadata and last_run.metadata.get("force_lock_clear"):
        reasons.append("Last run ended with force lock clear")
        return (RunHealth.BLOCKED, reasons)
    
    # Rule 2: BLOCKED - Last run failed due to infrastructure error
    # Infrastructure errors are indicated by specific failure reasons
    if last_run.result == RunResult.FAILED.value:
        failure_reason = last_run.failure_reason or ""
        infrastructure_errors = [
            "lock",
            "lock file",
            "permission",
            "disk",
            "memory",
            "connection",
            "timeout",
            "infrastructure"
        ]
        if any(err in failure_reason.lower() for err in infrastructure_errors):
            reasons.append(f"Last run failed due to infrastructure error: {failure_reason}")
            return (RunHealth.BLOCKED, reasons)
    
    # Rule 3: UNSTABLE - ≥ 3 failures in last 5 runs
    last_5_runs = recent_runs[:5]
    failure_count = sum(1 for run in last_5_runs if run.result == RunResult.FAILED.value)
    if failure_count >= 3:
        reasons.append(f"{failure_count} failures in last 5 runs")
        return (RunHealth.UNSTABLE, reasons)
    
    # Rule 4: DEGRADED - Same stage failed in 2 consecutive runs
    if len(recent_runs) >= 2:
        last_two = recent_runs[:2]
        # Check if both failed and have failed stages
        if (last_two[0].result == RunResult.FAILED.value and 
            last_two[1].result == RunResult.FAILED.value):
            
            last_failed_stages = set(last_two[0].stages_failed or [])
            prev_failed_stages = set(last_two[1].stages_failed or [])
            
            # Check for overlap in failed stages
            common_failed = last_failed_stages & prev_failed_stages
            if common_failed:
                stage_names = ", ".join(sorted(common_failed))
                reasons.append(f"Same stage(s) failed in 2 consecutive runs: {stage_names}")
                return (RunHealth.DEGRADED, reasons)
    
    # Rule 5: HEALTHY - None of the above conditions
    if not reasons:
        reasons.append("No health issues detected")
    
    return (RunHealth.HEALTHY, reasons)

