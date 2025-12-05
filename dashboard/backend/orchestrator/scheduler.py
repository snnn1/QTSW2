"""
Scheduler - Cron-like schedule executor
"""

import asyncio
import json
import logging
from pathlib import Path
from typing import Optional
from datetime import datetime, timedelta
import pytz


class Scheduler:
    """
    Background scheduler that triggers pipeline runs at scheduled times.
    """
    
    def __init__(
        self,
        config_path: Path,
        orchestrator,
        logger: logging.Logger
    ):
        self.config_path = config_path
        self.orchestrator = orchestrator
        self.logger = logger
        
        self._running = False
        self._task: Optional[asyncio.Task] = None
        self._reload_event = asyncio.Event()
        self._schedule_time: Optional[str] = None
        
        self._load_schedule()
    
    def _load_schedule(self):
        """Load schedule from config file"""
        try:
            if self.config_path.exists():
                with open(self.config_path, "r") as f:
                    data = json.load(f)
                    self._schedule_time = data.get("schedule_time", "07:30")
            else:
                self._schedule_time = "07:30"  # Default
        except Exception as e:
            self.logger.error(f"Failed to load schedule: {e}")
            self._schedule_time = "07:30"
    
    def reload(self):
        """Signal scheduler to reload config"""
        self._load_schedule()
        self._reload_event.set()
        self._reload_event.clear()
    
    async def start(self):
        """Start scheduler background task"""
        if self._running:
            return
        
        self._running = True
        self._task = asyncio.create_task(self._scheduler_loop())
        self.logger.info("Scheduler started")
    
    async def stop(self):
        """Stop scheduler"""
        self._running = False
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
        self.logger.info("Scheduler stopped")
    
    async def _scheduler_loop(self):
        """Main scheduler loop"""
        chicago_tz = pytz.timezone("America/Chicago")
        
        while self._running:
            try:
                # Calculate next run time (every 15 minutes)
                now = datetime.now(chicago_tz)
                next_run = self._calculate_next_run_time(now, self._schedule_time)
                
                wait_seconds = (next_run - now).total_seconds()
                
                # If we're at or past the scheduled time, run immediately
                if wait_seconds <= 0:
                    self.logger.info("At 15-minute mark, running pipeline now")
                else:
                    self.logger.info(f"Next scheduled run: {next_run.strftime('%Y-%m-%d %H:%M:%S %Z')} (in {wait_seconds/60:.1f} minutes) - Running every 15 minutes")
                    
                    # Wait until next run or reload signal
                    try:
                        await asyncio.wait_for(
                            self._reload_event.wait(),
                            timeout=wait_seconds
                        )
                        # Reload signal received, recalculate
                        continue
                    except asyncio.TimeoutError:
                        # Time to run
                        pass
                
                # Check if pipeline can run
                status = await self.orchestrator.get_status()
                if status and status.state.value not in ["idle", "success", "failed", "stopped"]:
                    self.logger.warning("Pipeline already running, skipping scheduled run")
                    # Wait a bit before checking again
                    await asyncio.sleep(60)
                    continue
                
                # Trigger pipeline run
                self.logger.info("Triggering scheduled pipeline run")
                try:
                    await self.orchestrator.start_pipeline(manual=False)
                except Exception as e:
                    self.logger.error(f"Failed to start scheduled pipeline: {e}")
                
                # Wait a bit before calculating next run
                await asyncio.sleep(60)
            
            except asyncio.CancelledError:
                break
            except Exception as e:
                self.logger.error(f"Scheduler error: {e}", exc_info=True)
                await asyncio.sleep(60)
    
    def _calculate_next_run_time(self, current_time: datetime, schedule_time: str) -> datetime:
        """
        Calculate next run time - runs every 15 minutes at :00, :15, :30, :45
        
        Args:
            current_time: Current time
            schedule_time: Ignored (kept for compatibility, but we run every 15 min)
        
        Returns:
            Next run datetime (next 15-minute mark)
        """
        # Get current minute
        current_minute = current_time.minute
        
        # Calculate next 15-minute mark
        # :00, :15, :30, :45
        if current_minute < 15:
            next_minute = 15
        elif current_minute < 30:
            next_minute = 30
        elif current_minute < 45:
            next_minute = 45
        else:
            # Next hour at :00
            next_minute = 0
            current_time += timedelta(hours=1)
        
        # Set to next 15-minute mark
        next_run = current_time.replace(minute=next_minute, second=0, microsecond=0)
        
        return next_run
    
    def get_next_run_time(self) -> Optional[datetime]:
        """Get next scheduled run time"""
        if not self._schedule_time:
            return None
        
        chicago_tz = pytz.timezone("America/Chicago")
        now = datetime.now(chicago_tz)
        return self._calculate_next_run_time(now, self._schedule_time)

