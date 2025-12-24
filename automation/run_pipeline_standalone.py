"""
Standalone pipeline runner for Windows Task Scheduler.
Runs the pipeline directly without requiring the backend API.

Phase 3: Uses unified orchestrator entrypoint - same code path as dashboard.
Publishes scheduler events directly to backend EventBus for real-time visibility.
"""

import sys
import asyncio
import logging
from pathlib import Path
from datetime import datetime, timezone

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


async def _subscribe_and_republish_events(event_bus, run_id: str):
    """
    Subscribe to the standalone orchestrator's EventBus and republish events to backend.
    
    This ensures stage events (translator, analyzer, merger) appear in real-time
    in the dashboard's Live Events panel.
    """
    try:
        async for event in event_bus.subscribe():
            # Only republish events for this run
            event_run_id = event.get("run_id")
            if event_run_id and event_run_id == run_id:
                stage = event.get("stage", "")
                event_type = event.get("event", "")
                
                # Skip scheduler events (already published directly)
                if stage == "scheduler":
                    continue
                
                # Republish to backend
                await publish_event_to_backend(
                    run_id=event_run_id,
                    stage=stage,
                    event_type=event_type,
                    msg=event.get("msg"),
                    data=event.get("data")
                )
    except asyncio.CancelledError:
        # Normal cancellation when pipeline completes
        logger.debug(f"Event subscription cancelled for run {run_id[:8]}")
    except Exception as e:
        logger.warning(f"Error in event subscription/republish loop: {e}")


def check_httpx_available():
    """
    Check if httpx is available for publishing scheduler events to backend.
    
    Returns:
        bool: True if httpx is available, False otherwise
    """
    try:
        import httpx
        return True
    except ImportError:
        return False


# Check httpx availability at startup
_httpx_available = check_httpx_available()
if not _httpx_available:
    logger.warning(
        "[WARNING] httpx is not installed - scheduler events will NOT appear in Live Events panel. "
        "Install with: pip install httpx>=0.24.0"
    )
    logger.warning(
        "   Events will still be logged to JSONL files and will appear after backend restart."
    )


async def publish_event_to_backend(run_id: str, stage: str, event_type: str, msg: str = None, data: dict = None):
    """
    Publish any pipeline event directly to the backend's EventBus via HTTP.
    
    This ensures events appear immediately in the Live Events panel,
    without waiting for JSONL monitor ingestion.
    
    Args:
        run_id: Pipeline run ID
        stage: Event stage (e.g., "scheduler", "translator", "analyzer", "merger")
        event_type: Event type (e.g., "start", "success", "failed")
        msg: Optional event message
        data: Optional additional event data
    """
    # Check if httpx is available (checked at module load)
    if not _httpx_available:
        logger.debug(f"httpx not available - skipping {stage}/{event_type} event publication")
        return  # Early return if httpx is not available
    
    try:
        import httpx
    except ImportError:
        # This should not happen if _httpx_available was set correctly, but handle it anyway
        logger.warning(f"[WARNING] httpx import failed unexpectedly - cannot publish {stage}/{event_type} events")
        return
    
    try:
        event_data = {
            "run_id": run_id,
            "stage": stage,
            "event": event_type,
            "timestamp": datetime.now(timezone.utc).isoformat(),
        }
        if msg is not None:
            event_data["msg"] = msg
        if data is not None:
            event_data["data"] = data
        
        # Use the generic publish-event endpoint for all events
        endpoint = "http://localhost:8001/api/pipeline/publish-event"
        
        async with httpx.AsyncClient(timeout=5.0) as client:
            response = await client.post(
                endpoint,
                json=event_data,
                timeout=5.0
            )
            if response.status_code == 200:
                result = response.json()
                if result.get("published"):
                    logger.info(f"[SUCCESS] Published {stage}/{event_type} event to backend (run: {run_id[:8]})")
                else:
                    logger.warning(f"[WARNING] Backend received {stage}/{event_type} event but did not publish: {result.get('message', 'unknown error')}")
            elif response.status_code == 405:
                logger.warning(f"[WARNING] Backend endpoint {endpoint} returned 405 Method Not Allowed - endpoint may not be registered. Restart backend to enable real-time events.")
            else:
                logger.warning(f"[WARNING] Backend returned status {response.status_code} for {stage}/{event_type} event: {response.text[:200]}")
    except httpx.ConnectError as e:
        logger.debug(f"[WARNING] Cannot connect to backend at http://localhost:8001 - {stage}/{event_type} event will appear after backend restart ({e})")
    except Exception as e:
        # Don't fail if backend is not available - scheduled runs should work independently
        # Log at DEBUG level since this is expected when backend is not running
        logger.debug(f"[WARNING] Could not publish {stage}/{event_type} event to backend: {type(e).__name__}: {e}")


async def publish_scheduler_event_to_backend(run_id: str, event_type: str, msg: str, data: dict = None):
    """
    Publish a scheduler event directly to the backend's EventBus via HTTP.
    
    This ensures scheduler events appear immediately in the Live Events panel,
    without waiting for JSONL monitor ingestion.
    
    Args:
        run_id: Pipeline run ID
        event_type: "start", "success", or "failed"
        msg: Event message
        data: Optional additional event data
    """
    # Check if httpx is available (checked at module load)
    if not _httpx_available:
        logger.debug("httpx not available - skipping scheduler event publication")
        return  # Early return if httpx is not available
    
    try:
        import httpx
    except ImportError:
        # This should not happen if _httpx_available was set correctly, but handle it anyway
        logger.warning("[WARNING] httpx import failed unexpectedly - cannot publish scheduler events")
        return
    
    try:
        event_data = {
            "run_id": run_id,
            "stage": "scheduler",
            "event": event_type,
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "msg": msg,
            "data": data or {"manual": False}
        }
        
        logger.info(f"📤 Attempting to publish scheduler/{event_type} event to backend (run: {run_id[:8]})")
        
        async with httpx.AsyncClient(timeout=5.0) as client:
            response = await client.post(
                "http://localhost:8001/api/pipeline/publish-scheduler-event",
                json=event_data,
                timeout=5.0
            )
            if response.status_code == 200:
                result = response.json()
                if result.get("published"):
                    logger.info(f"[SUCCESS] Published scheduler/{event_type} event to backend (run: {run_id[:8]})")
                else:
                    logger.warning(f"[WARNING] Backend received event but did not publish: {result.get('message', 'unknown error')}")
            else:
                logger.warning(f"[WARNING] Backend returned status {response.status_code} for scheduler event: {response.text[:200]}")
    except httpx.ConnectError as e:
        logger.warning(f"[WARNING] Cannot connect to backend at http://localhost:8001 - is the dashboard backend running? ({e})")
    except Exception as e:
        # Don't fail if backend is not available - scheduled runs should work independently
        # Log at WARNING level so we can see connection issues
        logger.warning(f"[WARNING] Could not publish scheduler/{event_type} event to backend: {type(e).__name__}: {e}")


async def notify_dashboard_backend(run_id: str):
    """
    Notify dashboard backend that a scheduled run has started.
    This allows the dashboard to actively monitor this run's JSONL file.
    
    NOTE: This is kept for backward compatibility, but scheduler events should
    now be published via publish_scheduler_event_to_backend() for real-time visibility.
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
    
    CRITICAL: Scheduler events (scheduler/start, scheduler/success, scheduler/failed)
    are published directly to the backend's EventBus via HTTP for real-time visibility.
    JSONL logging remains unchanged for historical records.
    """
    from modules.orchestrator.entrypoint import create_orchestrator, run_pipeline_once
    
    # Warn if httpx is not available (checked at module load)
    if not _httpx_available:
        logger.warning(
            "[WARNING] Real-time scheduler events are disabled - httpx not installed. "
            "Events will only appear after backend restart when JSONL monitor processes them."
        )
    
    # Create orchestrator (it will create its own EventBus for JSONL logging)
    # We publish scheduler events directly to the backend's EventBus via HTTP for real-time visibility
    orchestrator = create_orchestrator(qtsw2_root=qtsw2_root, logger=logger)
    await orchestrator.start()
    
    run_id = None
    
    try:
        # Start pipeline and wait for completion
        # Capture run_id when pipeline starts to publish scheduler events
        async def notify_on_start(run_id: str):
            """Called when pipeline starts - publish scheduler/start event"""
            logger.info(f"📅 Pipeline started with run_id: {run_id[:8]} - publishing scheduler/start event to backend")
            # Publish scheduler/start event directly to backend EventBus
            await publish_scheduler_event_to_backend(
                run_id=run_id,
                event_type="start",
                msg="Scheduled pipeline run started",
                data={"manual": False}
            )
            
            # Subscribe to the orchestrator's EventBus to republish stage events to backend
            # This ensures translator/analyzer/merger events appear in real-time
            asyncio.create_task(_subscribe_and_republish_events(orchestrator.event_bus, run_id))
            # Keep backward compatibility notification (non-critical)
            await notify_dashboard_backend(run_id)
        
        exit_code, final_state = await run_pipeline_once(
            orchestrator=orchestrator,
            manual=False,
            max_wait_time=600,
            check_interval=5,
            notify_callback=notify_on_start
        )
        
        # Get run_id from orchestrator status for publishing completion event
        try:
            status = await orchestrator.get_status()
            if status and status.run_id:
                run_id = status.run_id
        except Exception:
            pass
        
        # Publish scheduler completion event (success or failed)
        if run_id:
            if exit_code == 0:
                event_type = "success"
                msg = "Scheduled pipeline run completed successfully"
            else:
                event_type = "failed"
                msg = f"Scheduled pipeline run failed (state: {final_state})"
            
            await publish_scheduler_event_to_backend(
                run_id=run_id,
                event_type=event_type,
                msg=msg,
                data={"manual": False, "success": exit_code == 0, "final_state": final_state}
            )
        
        if exit_code == 0:
            logger.info("Pipeline completed successfully")
        else:
            logger.warning(f"Pipeline completed with state: {final_state}")
        
        return exit_code
        
    except Exception as e:
        # Publish scheduler/failed event if pipeline start failed
        if run_id:
            await publish_scheduler_event_to_backend(
                run_id=run_id,
                event_type="failed",
                msg=f"Scheduled pipeline run failed: {e}",
                data={"manual": False, "success": False, "error": str(e)}
            )
        raise
        
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

