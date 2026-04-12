"""
Run Policy Gate - Determines if pipeline can run based on health

This is the single gate. No other part of the system decides this.
"""

from typing import Tuple, Optional, List
from .run_health import RunHealth, compute_run_health
from .run_history import RunHistory


def can_run_pipeline(
    run_history: RunHistory,
    auto_run: bool,
    manual_override: bool = False
) -> Tuple[bool, Optional[str], RunHealth, List[str]]:
    """
    Policy gate: Determine if pipeline can run.
    
    This is the single gate. No other part of the system decides this.
    
    Args:
        run_history: RunHistory instance to analyze
        auto_run: True if this is an auto-run, False if manual
        manual_override: True if user explicitly overrides policy (manual runs only)
        
    Returns:
        Tuple of (allowed, reason, health, reasons)
        - allowed: True if pipeline can run, False otherwise
        - reason: Human-readable explanation (None if allowed)
        - health: Current RunHealth state
        - reasons: List of health determination reasons
    """
    # Always compute current health
    health, health_reasons = compute_run_health(run_history)
    
    # Policy logic
    if auto_run:
        # Auto-runs: allow ONLY if health == healthy
        if health == RunHealth.HEALTHY:
            return (True, None, health, health_reasons)
        else:
            reason = f"Auto-run blocked: health is {health.value} (requires healthy)"
            return (False, reason, health, health_reasons)
    
    else:
        # Manual runs
        if health == RunHealth.BLOCKED:
            # Blocked is never allowed (even manual)
            reason = f"Manual run blocked: health is {health.value} (cannot override)"
            return (False, reason, health, health_reasons)
        
        elif health in {RunHealth.DEGRADED, RunHealth.UNSTABLE}:
            # Degraded/unstable: require explicit override
            if manual_override:
                reason = f"Manual run allowed with override: health is {health.value}"
                return (True, reason, health, health_reasons)
            else:
                reason = f"Manual run requires override: health is {health.value}"
                return (False, reason, health, health_reasons)
        
        else:
            # Healthy: always allowed
            return (True, None, health, health_reasons)

