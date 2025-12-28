"""
Watchdog - Monitors pipeline runs and handles timeouts/stale states
"""

import asyncio
import logging
from typing import Optional
from datetime import datetime, timedelta
from .state import PipelineRunState


class Watchdog:
    """
    Watchdog that monitors pipeline runs for timeouts and stale states.
    Detects hung runs and transitions them to FAILED state.
    """
    
    def __init__(
        self,
        orchestrator,
        event_bus,
        state_manager,
        config,
        logger: Optional[logging.Logger] = None
    ):
        self.orchestrator = orchestrator
        self.event_bus = event_bus
        self.state_manager = state_manager
        self.config = config
        self.logger = logger or logging.getLogger(__name__)
        
        self._running = False
        self._monitor_task: Optional[asyncio.Task] = None
    
    async def start(self):
        """Start the watchdog monitor"""
        if self._running:
            return
        
        self._running = True
        self._monitor_task = asyncio.create_task(self._monitor_loop())
        self.logger.info("Watchdog started")
    
    async def stop(self):
        """Stop the watchdog monitor"""
        self._running = False
        
        if self._monitor_task:
            self._monitor_task.cancel()
            try:
                await self._monitor_task
            except asyncio.CancelledError:
                pass
        
        self.logger.info("Watchdog stopped")
    
    async def _monitor_loop(self):
        """
        Main monitoring loop - checks for hung runs and stale locks.
        
        CRITICAL: No background task is allowed to bring down the WebSocket or backend process.
        All exceptions are caught, logged, error events emitted, and the loop continues.
        """
        while self._running:
            try:
                await asyncio.sleep(self.config.watchdog_interval_sec)
                
                # Check current pipeline state
                status = await self.state_manager.get_state()
                if not status:
                    continue
                
                # Only monitor active runs
                if status.state in {
                    PipelineRunState.IDLE,
                    PipelineRunState.SUCCESS,
                    PipelineRunState.FAILED,
                    PipelineRunState.STOPPED,
                }:
                    continue
                
                # Check if run has exceeded maximum runtime
                if status.started_at:
                    runtime_sec = (datetime.now(status.started_at.tzinfo) - status.started_at).total_seconds()
                    max_runtime_sec = self.config.lock_timeout_sec
                    
                    if runtime_sec > max_runtime_sec:
                        self.logger.warning(
                            f"Watchdog: Run {status.run_id[:8]} has exceeded max runtime "
                            f"({runtime_sec:.0f}s > {max_runtime_sec}s). Transitioning to FAILED."
                        )
                        
                        # Transition to FAILED
                        try:
                            await self.state_manager.transition(
                                PipelineRunState.FAILED,
                                error=f"Run exceeded maximum runtime ({max_runtime_sec}s)"
                            )
                            
                            # Emit watchdog event
                            await self.event_bus.publish({
                                "run_id": status.run_id,
                                "stage": "watchdog",
                                "event": "timeout",
                                "timestamp": datetime.now().isoformat(),
                                "msg": f"Run exceeded maximum runtime ({max_runtime_sec}s)",
                                "data": {
                                    "runtime_seconds": runtime_sec,
                                    "max_runtime_seconds": max_runtime_sec
                                }
                            })
                            
                            # Release lock
                            try:
                                await self.orchestrator.lock_manager.release(status.run_id)
                            except Exception as e:
                                self.logger.error(f"Watchdog: Failed to release lock: {e}")
                                # Try force clear as fallback
                                await self.orchestrator.lock_manager.force_clear_all()
                        except Exception as e:
                            self.logger.error(f"Watchdog: Failed to transition hung run to FAILED: {e}", exc_info=True)
                
                # Check if lock is stale (additional safety check)
                try:
                    is_locked = await self.orchestrator.lock_manager.is_locked()
                    if is_locked:
                        lock_info = await self.orchestrator.lock_manager.get_lock_info()
                        if lock_info and lock_info.get("run_id") == status.run_id:
                            # Lock matches current run - check if it's stale
                            if await self.orchestrator.lock_manager._is_stale():
                                self.logger.warning(
                                    f"Watchdog: Lock for run {status.run_id[:8]} is stale. "
                                    f"Run may be hung. Lock will be reclaimed on next acquisition attempt."
                                )
                except Exception as e:
                    self.logger.debug(f"Watchdog: Error checking lock status: {e}")
                
            except asyncio.CancelledError:
                break
            except Exception as e:
                # CRITICAL: Catch all exceptions, log, emit error event, continue
                self.logger.error(f"[Watchdog] Monitor loop error: {e}", exc_info=True)
                try:
                    await self.event_bus.publish({
                        "run_id": "__system__",
                        "stage": "watchdog",
                        "event": "error",
                        "timestamp": datetime.now().isoformat(),
                        "msg": f"Watchdog monitor loop error: {e}",
                        "data": {"component": "watchdog", "error": str(e)}
                    })
                except Exception as publish_error:
                    self.logger.error(f"[Watchdog] Failed to emit error event: {publish_error}")
                # Continue monitoring even if one check fails - never crash
                await asyncio.sleep(5)
