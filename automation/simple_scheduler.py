"""
Simple In-App Scheduler

Just runs the pipeline every 15 minutes. That's it.
- Checks time every minute
- Runs pipeline at :00, :15, :30, :45
- Handles errors gracefully
- Logs everything clearly
"""

import sys
import time
import logging
from pathlib import Path
from datetime import datetime
import pytz

from automation.config import PipelineConfig, SCHEDULE_TIMES
from automation.logging_setup import create_logger
from automation.pipeline_runner import run_pipeline_once


def main():
    """Simple scheduler - just runs the pipeline on schedule"""
    
    # Setup
    config = PipelineConfig.from_environment()
    log_file = config.logs_dir / f"scheduler_{datetime.now().strftime('%Y%m%d')}.log"
    logger = create_logger("Scheduler", log_file, level=logging.INFO)
    
    chicago_tz = pytz.timezone("America/Chicago")
    
    logger.info("=" * 80)
    logger.info("Simple Scheduler Starting")
    logger.info(f"Will run pipeline at :{', :'.join(map(str, SCHEDULE_TIMES))} every hour")
    logger.info("=" * 80)
    
    last_run_minute = None
    
    try:
        while True:
            current_time = datetime.now(chicago_tz)
            current_minute = current_time.minute
            
            # Check if it's time to run (and we haven't run this minute already)
            if current_minute in SCHEDULE_TIMES and current_minute != last_run_minute:
                logger.info("=" * 80)
                logger.info(f"Starting scheduled pipeline run at {current_time.strftime('%Y-%m-%d %H:%M:%S')}")
                logger.info("=" * 80)
                
                try:
                    # Run the pipeline
                    run_pipeline_once(config)
                    logger.info("Pipeline run completed successfully")
                    
                except KeyboardInterrupt:
                    logger.info("Pipeline interrupted by user")
                    raise
                    
                except Exception as e:
                    # Log error but keep scheduler running
                    logger.error(f"Pipeline run failed: {e}", exc_info=True)
                    logger.info("Scheduler will continue and retry at next scheduled time")
                
                last_run_minute = current_minute
                logger.info(f"Next run will be at the next scheduled time (:00, :15, :30, or :45)")
            
            # Sleep for 1 minute, then check again
            time.sleep(60)
            
    except KeyboardInterrupt:
        logger.info("Scheduler stopped by user")
    except Exception as e:
        logger.critical(f"Fatal error in scheduler: {e}", exc_info=True)
        raise


if __name__ == "__main__":
    main()

