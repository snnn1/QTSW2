"""
Unified Orchestrator Entrypoint - Phase 3

This module provides a single entrypoint for creating and using the PipelineOrchestrator,
ensuring both dashboard and standalone scripts use identical initialization and execution logic.
"""

import asyncio
import logging
from pathlib import Path
from typing import Optional, Tuple, Callable, Any

from . import PipelineOrchestrator, OrchestratorConfig
from .state import PipelineRunState


def create_orchestrator(
    qtsw2_root: Path,
    logger: Optional[logging.Logger] = None
) -> PipelineOrchestrator:
    """
    Create and initialize a PipelineOrchestrator instance.
    
    This is the single source of truth for orchestrator creation.
    Both dashboard backend and standalone scripts use this function.
    
    Args:
        qtsw2_root: Project root directory
        logger: Optional logger instance
    
    Returns:
        Initialized PipelineOrchestrator (not started)
    """
    if logger is None:
        logger = logging.getLogger(__name__)
    
    # Create config (single source of truth)
    config = OrchestratorConfig.from_environment(qtsw2_root=qtsw2_root)
    
    # Schedule config path
    schedule_config_path = qtsw2_root / "configs" / "schedule.json"
    
    # Create orchestrator instance
    orchestrator = PipelineOrchestrator(
        config=config,
        schedule_config_path=schedule_config_path,
        logger=logger
    )
    
    return orchestrator


async def run_pipeline_once(
    orchestrator: PipelineOrchestrator,
    manual: bool = False,
    max_wait_time: int = 600,
    check_interval: int = 5,
    notify_callback: Optional[Callable[[str], Any]] = None
) -> Tuple[int, Optional[str]]:
    """
    Run a single pipeline execution and wait for completion.
    
    This is the unified execution path used by both:
    - Dashboard (via API, but same logic)
    - Standalone script (direct call)
    
    Args:
        orchestrator: Initialized and started PipelineOrchestrator
        manual: Whether this is a manual run (True) or scheduled (False)
        max_wait_time: Maximum time to wait for completion (seconds)
        check_interval: How often to check status (seconds)
        notify_callback: Optional callback function(run_id) called after pipeline starts
    
    Returns:
        Tuple of (exit_code, final_state)
        - exit_code: 0 for success, 1 for failure
        - final_state: "success", "failed", or None if timeout
    """
    logger = orchestrator.logger
    
    try:
        # Start pipeline run
        logger.info(f"Starting {'manual' if manual else 'scheduled'} pipeline run...")
        try:
            run_ctx = await orchestrator.start_pipeline(manual=manual)
            logger.info(f"Pipeline started: {run_ctx.run_id}")
        except ValueError as e:
            # Handle "Pipeline already running" error gracefully
            if "already running" in str(e).lower():
                logger.warning(f"Cannot start pipeline: {e}. This may indicate a stale state from a previous run.")
                # Try to reset state if it's stuck
                status = await orchestrator.get_status()
                if status and status.state not in {
                    PipelineRunState.IDLE,
                    PipelineRunState.SUCCESS,
                    PipelineRunState.FAILED,
                    PipelineRunState.STOPPED,
                }:
                    logger.warning(f"Pipeline state appears stuck in: {status.state.value}. Attempting to reset...")
                    # Force clear the stuck run
                    await orchestrator.state_manager.clear_run()
                    logger.info("Pipeline state cleared. Retrying start...")
                    run_ctx = await orchestrator.start_pipeline(manual=manual)
                    logger.info(f"Pipeline started after reset: {run_ctx.run_id}")
                else:
                    # Re-raise if we can't fix it
                    raise
            else:
                raise
        
        # Notify callback if provided (e.g., for dashboard notification)
        if notify_callback:
            try:
                await notify_callback(run_ctx.run_id)
            except Exception as e:
                logger.debug(f"Notification callback failed (non-fatal): {e}")
        
        # Wait for pipeline to complete
        elapsed = 0
        while elapsed < max_wait_time:
            await asyncio.sleep(check_interval)
            elapsed += check_interval
            
            status = await orchestrator.get_status()
            if status:
                # Phase 2: status.state is a PipelineRunState enum, use .value to get string
                current_state = status.state.value if hasattr(status.state, 'value') else str(status.state)
                logger.debug(f"Pipeline status: {current_state}")
                
                # Phase 2: Check if completed (only success or failed, no stopped state)
                if current_state in ["success", "failed"]:
                    logger.info(f"Pipeline completed with state: {current_state}")
                    return (0 if current_state == "success" else 1, current_state)
        
        # Timeout
        logger.warning(f"Pipeline did not complete within {max_wait_time}s")
        final_status = await orchestrator.get_status()
        if final_status:
            final_state = final_status.state.value if hasattr(final_status.state, 'value') else str(final_status.state)
            return (1, final_state)
        return (1, None)
        
    except Exception as e:
        logger.error(f"Error running pipeline: {e}", exc_info=True)
        return (1, None)


async def run_standalone_pipeline(
    qtsw2_root: Path,
    logger: Optional[logging.Logger] = None
) -> int:
    """
    Complete standalone pipeline execution (create, start, run, stop).
    
    This is the entrypoint for Windows Task Scheduler.
    It creates an orchestrator, starts it, runs one pipeline execution, and stops it.
    
    Args:
        qtsw2_root: Project root directory
        logger: Optional logger instance
    
    Returns:
        Exit code: 0 for success, 1 for failure
    """
    if logger is None:
        logger = logging.getLogger(__name__)
    
    try:
        # Create orchestrator (unified entrypoint)
        logger.info("Initializing pipeline orchestrator...")
        orchestrator = create_orchestrator(qtsw2_root=qtsw2_root, logger=logger)
        
        # Start orchestrator (scheduler and watchdog)
        await orchestrator.start()
        logger.info("Orchestrator started")
        
        try:
            # Run pipeline once (unified execution path)
            exit_code, final_state = await run_pipeline_once(
                orchestrator=orchestrator,
                manual=False,  # Scheduled run
                max_wait_time=600,  # 10 minutes
                check_interval=5  # Check every 5 seconds
            )
            
            if exit_code == 0:
                logger.info("Pipeline completed successfully")
            else:
                logger.warning(f"Pipeline completed with state: {final_state}")
            
            return exit_code
            
        finally:
            # Stop orchestrator
            await orchestrator.stop()
            logger.info("Orchestrator stopped")
            
    except ImportError as e:
        logger.error(f"Failed to import orchestrator: {e}")
        logger.error("Make sure you're running from the project root and all dependencies are installed")
        return 1
    except Exception as e:
        logger.error(f"Error running pipeline: {e}", exc_info=True)
        return 1

