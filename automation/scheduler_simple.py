"""
Simple Scheduler - Triggers pipeline runs every 15 minutes

Single Responsibility: Decide when to call the orchestrator
No GUI logic, no business logic, just scheduling
"""

import time
import logging
from datetime import datetime
import pytz

from automation.config import PipelineConfig, SCHEDULE_INTERVAL_MINUTES, SCHEDULE_TIMES
from automation.logging_setup import create_logger
from automation.pipeline_runner import run_pipeline_once


def wait_for_next_run_time(current_time: datetime, tz=pytz.timezone("America/Chicago")) -> datetime:
    """
    Calculate next run time based on schedule.
    
    Runs every 15 minutes at :00, :15, :30, :45
    
    Args:
        current_time: Current datetime
        tz: Timezone
    
    Returns:
        Next run time
    """
    # Get current time in timezone
    if current_time.tzinfo is None:
        current_time = tz.localize(current_time)
    else:
        current_time = current_time.astimezone(tz)
    
    current_minute = current_time.minute
    current_hour = current_time.hour
    
    # Find next scheduled time
    next_minute = None
    for scheduled_minute in SCHEDULE_TIMES:
        if scheduled_minute > current_minute:
            next_minute = scheduled_minute
            break
    
    if next_minute is None:
        # Next run is at :00 of next hour
        next_hour = (current_hour + 1) % 24
        next_minute = 0
    else:
        next_hour = current_hour
    
    # Create next run time
    next_run = current_time.replace(hour=next_hour, minute=next_minute, second=0, microsecond=0)
    
    # If we're past the last scheduled time today, move to next day
    if next_run <= current_time:
        next_run = next_run.replace(hour=0, minute=0) + time.timedelta(days=1)
    
    return next_run


def run_scheduler_loop(config: PipelineConfig = None):
    """
    Run scheduler in a loop (every 15 minutes).
    
    Note: This is a fallback. Prefer using OS scheduler (Windows Task Scheduler)
    to call pipeline_runner.py directly every 15 minutes.
    
    Args:
        config: Optional PipelineConfig
    """
    if config is None:
        config = PipelineConfig.from_environment()
    
    # Setup logging
    log_file = config.logs_dir / f"scheduler_{datetime.now().strftime('%Y%m%d_%H%M%S')}.log"
    logger = create_logger("Scheduler", log_file, level=logging.INFO)
    
    logger.info("=" * 60)
    logger.info("PIPELINE SCHEDULER STARTED")
    logger.info("=" * 60)
    logger.info("Note: Consider using OS Task Scheduler instead of this loop")
    logger.info("=" * 60)
    
    tz = pytz.timezone("America/Chicago")
    
    while True:
        try:
            # Calculate next run time
            current_time = datetime.now(tz)
            next_run = wait_for_next_run_time(current_time, tz)
            
            wait_seconds = (next_run - current_time).total_seconds()
            wait_minutes = int(wait_seconds / 60)
            
            logger.info(f"Next pipeline run scheduled for: {next_run.strftime('%Y-%m-%d %H:%M:%S %Z')}")
            logger.info(f"Waiting {wait_minutes} minutes...")
            
            # Wait until next run time
            time.sleep(wait_seconds)
            
            # Run pipeline
            logger.info("=" * 60)
            logger.info("TRIGGERING PIPELINE RUN")
            logger.info("=" * 60)
            
            run_pipeline_once(config)
            
            logger.info("Pipeline run completed")
            logger.info("")
        
        except KeyboardInterrupt:
            logger.info("Scheduler stopped by user")
            break
        except Exception as e:
            logger.error(f"Scheduler error: {e}", exc_info=True)
            # Wait a bit before retrying
            time.sleep(60)


if __name__ == "__main__":
    run_scheduler_loop()



