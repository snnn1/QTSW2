"""
Watchdog - Monitoring and self-healing
"""

import asyncio
import logging
from typing import Optional
from datetime import datetime, timedelta
import pytz


class Watchdog:
    """
    Periodic health check and monitoring.
    Detects hung runs and emits heartbeat events.
    """
    
    def __init__(
        self,
        orchestrator,
        event_bus,
        state_manager,
        config,
        logger: logging.Logger
    ):
        self.orchestrator = orchestrator
        self.event_bus = event_bus
        self.state_manager = state_manager
        self.config = config
        self.logger = logger
        
        self._running = False
        self._task: Optional[asyncio.Task] = None
        self._last_heartbeat: Optional[datetime] = None
    
    async def start(self):
        """Start watchdog background task"""
        if self._running:
            return
        
        self._running = True
        self._task = asyncio.create_task(self._watchdog_loop())
        self.logger.info("Watchdog started")
    
    async def stop(self):
        """Stop watchdog"""
        self._running = False
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
        self.logger.info("Watchdog stopped")
    
    async def _watchdog_loop(self):
        """Main watchdog loop"""
        chicago_tz = pytz.timezone("America/Chicago")
        
        while self._running:
            try:
                await asyncio.sleep(self.config.watchdog_interval_sec)
                
                # Emit heartbeat
                await self._emit_heartbeat()
                
                # Check pipeline health
                await self._check_pipeline_health()
            
            except asyncio.CancelledError:
                break
            except Exception as e:
                self.logger.error(f"Watchdog error: {e}", exc_info=True)
                await asyncio.sleep(self.config.watchdog_interval_sec)
    
    async def _emit_heartbeat(self):
        """Emit periodic heartbeat event"""
        chicago_tz = pytz.timezone("America/Chicago")
        now = datetime.now(chicago_tz)
        self._last_heartbeat = now
        
        status = await self.state_manager.get_state()
        
        await self.event_bus.publish({
            "run_id": status.run_id if status else "system",
            "stage": "system",
            "event": "heartbeat",
            "timestamp": now.isoformat(),
            "msg": "System heartbeat",
            "data": {
                "state": status.state.value if status else "idle",
                "current_stage": status.current_stage.value if status and status.current_stage else None,
            }
        })
    
    async def _check_pipeline_health(self):
        """Check pipeline health and detect hung runs"""
        status = await self.state_manager.get_state()
        
        if not status:
            return
        
        # Check if run is stuck
        from .state import PipelineRunState
        if status.state in [
            PipelineRunState.RUNNING_TRANSLATOR,
            PipelineRunState.RUNNING_ANALYZER,
            PipelineRunState.RUNNING_MERGER
        ]:
            # Check timeout
            stage_name = status.current_stage.value if status.current_stage else "unknown"
            stage_config = self.config.stages.get(stage_name)
            
            if stage_config:
                timeout_sec = stage_config.timeout_sec
                if status.started_at:
                    elapsed = (datetime.now(pytz.timezone("America/Chicago")) - status.started_at).total_seconds()
                    
                    if elapsed > timeout_sec:
                        self.logger.warning(
                            f"Pipeline stage {stage_name} exceeded timeout "
                            f"({elapsed:.0f}s > {timeout_sec}s)"
                        )
                        
                        await self.event_bus.publish({
                            "run_id": status.run_id,
                            "stage": "pipeline",
                            "event": "error",
                            "timestamp": datetime.now(pytz.timezone("America/Chicago")).isoformat(),
                            "msg": f"Stage {stage_name} exceeded timeout",
                            "data": {
                                "stage": stage_name,
                                "elapsed_seconds": elapsed,
                                "timeout_seconds": timeout_sec
                            }
                        })
                        
                        # Transition to failed
                        try:
                            from .state import PipelineRunState
                            await self.state_manager.transition(
                                PipelineRunState.FAILED,
                                error=f"Stage {stage_name} exceeded timeout ({elapsed:.0f}s)"
                            )
                        except Exception as e:
                            self.logger.error(f"Failed to transition to failed: {e}")
            
            # Check heartbeat timeout
            if status.updated_at:
                time_since_update = (
                    datetime.now(pytz.timezone("America/Chicago")) - status.updated_at
                ).total_seconds()
                
                if time_since_update > self.config.heartbeat_timeout_sec:
                    self.logger.warning(
                        f"Pipeline run {status.run_id} has no updates for {time_since_update:.0f}s"
                    )
                    
                    await self.event_bus.publish({
                        "run_id": status.run_id,
                        "stage": "pipeline",
                        "event": "error",
                        "timestamp": datetime.now(pytz.timezone("America/Chicago")).isoformat(),
                        "msg": "Pipeline run appears hung (no updates)",
                        "data": {
                            "time_since_update_seconds": time_since_update,
                            "heartbeat_timeout_seconds": self.config.heartbeat_timeout_sec
                        }
                    })

