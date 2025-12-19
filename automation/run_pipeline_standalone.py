"""
Standalone pipeline runner for Windows Task Scheduler.
Runs the pipeline directly without requiring the backend API.

Phase 3: Uses unified orchestrator entrypoint - same code path as dashboard.
"""

import sys
import asyncio
import logging
from pathlib import Path

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

# Setup logging
log_dir = qtsw2_root / "automation" / "logs"
log_dir.mkdir(parents=True, exist_ok=True)

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s',
    handlers=[
        logging.FileHandler(log_dir / "pipeline_standalone.log"),
        logging.StreamHandler()
    ]
)

logger = logging.getLogger(__name__)


async def notify_dashboard_backend(run_id: str):
    """
    Notify dashboard backend that a scheduled run has started.
    This allows the dashboard to actively monitor this run's JSONL file.
    """
    try:
        import httpx
        async with httpx.AsyncClient(timeout=2.0) as client:
            await client.post(
                "http://localhost:8001/api/pipeline/notify-scheduled-run",
                json={"run_id": run_id, "source": "windows_scheduler"},
                timeout=2.0
            )
    except Exception as e:
        # Don't fail if backend is not available - scheduled runs should work independently
        logger.debug(f"Could not notify dashboard backend: {e}")


async def run_pipeline_standalone():
    """
    Run pipeline using unified orchestrator entrypoint.
    
    Phase 3: This now uses the same entrypoint as the dashboard backend,
    ensuring identical behavior regardless of invocation source.
    """
    from modules.dashboard.backend.orchestrator.entrypoint import run_standalone_pipeline, create_orchestrator, run_pipeline_once
    from automation.services.event_logger import EventLogger
    import asyncio
    
    # Emit scheduler/start event at the very start of execution
    # This ensures scheduler health shows as active even if orchestrator fails to start
    try:
        # Write to a scheduler log file that the JSONL monitor will pick up
        event_logs_dir = qtsw2_root / "automation" / "logs" / "events"
        event_logs_dir.mkdir(parents=True, exist_ok=True)
        
        # Use fixed run_id for scheduler events - all scheduled runs append to same file
        scheduler_log_file = event_logs_dir / "pipeline___scheduled__.jsonl"
        
        event_logger = EventLogger(log_file=scheduler_log_file, logger=logger)
        event_logger.emit(
            run_id="__scheduled__",
            stage="scheduler",
            event="start",
            msg="Scheduled pipeline run started"
        )
        logger.info("Emitted scheduler/start event at entrypoint")
    except Exception as e:
        # Don't fail if event emission fails - this is just for health monitoring
        logger.warning(f"Failed to emit initial scheduler/start event: {e}")
    
    # Create orchestrator first
    orchestrator = create_orchestrator(qtsw2_root=qtsw2_root, logger=logger)
    await orchestrator.start()
    
    try:
        # Start pipeline and wait for completion (run_pipeline_once handles starting)
        # Notify dashboard when pipeline starts
        async def notify_on_start(run_id: str):
            try:
                await notify_dashboard_backend(run_id)
                logger.info(f"Notified dashboard backend of scheduled run: {run_id[:8]}")
            except Exception as e:
                logger.debug(f"Could not notify dashboard (non-fatal): {e}")
        
        exit_code, final_state = await run_pipeline_once(
            orchestrator=orchestrator,
            manual=False,
            max_wait_time=600,
            check_interval=5,
            notify_callback=notify_on_start
        )
        
        if exit_code == 0:
            logger.info("Pipeline completed successfully")
        else:
            logger.warning(f"Pipeline completed with state: {final_state}")
        
        return exit_code
        
    finally:
        # Stop orchestrator and ensure all tasks are cleaned up
        logger.info("Stopping orchestrator and cleaning up...")
        try:
            await orchestrator.stop()
        except Exception as e:
            logger.warning(f"Error stopping orchestrator: {e}")
        
        # Give a small delay to ensure all cleanup completes
        await asyncio.sleep(0.5)
        
        # Cancel any remaining tasks (except the current one)
        try:
            current_task = asyncio.current_task()
            if current_task:
                pending = [task for task in asyncio.all_tasks() if task != current_task and not task.done()]
                if pending:
                    logger.debug(f"Cancelling {len(pending)} pending tasks")
                    for task in pending:
                        task.cancel()
                    # Wait for cancellations to complete (with timeout)
                    try:
                        await asyncio.wait_for(
                            asyncio.gather(*pending, return_exceptions=True),
                            timeout=2.0
                        )
                    except asyncio.TimeoutError:
                        logger.warning("Timeout waiting for tasks to cancel")
        except Exception as e:
            logger.debug(f"Error cancelling tasks: {e}")
        
        logger.info("Cleanup complete")


def main():
    """Main entry point for Windows Task Scheduler"""
    try:
        exit_code = asyncio.run(run_pipeline_standalone())
        # Explicitly flush logs and ensure clean exit
        logging.shutdown()
        sys.exit(exit_code)
    except KeyboardInterrupt:
        logger.info("Pipeline run interrupted")
        logging.shutdown()
        sys.exit(1)
    except Exception as e:
        logger.error(f"Fatal error: {e}", exc_info=True)
        logging.shutdown()
        sys.exit(1)


if __name__ == "__main__":
    main()

