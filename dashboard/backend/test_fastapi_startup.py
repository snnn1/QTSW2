"""
Test FastAPI startup exactly as it happens in main.py
"""
import asyncio
import logging
from pathlib import Path
import sys

# Setup logging exactly like main.py
LOG_FILE_PATH = Path(__file__).parent.parent.parent / "logs" / "backend_debug.log"
LOG_FILE_PATH.parent.mkdir(parents=True, exist_ok=True)

file_handler = logging.FileHandler(LOG_FILE_PATH, mode='a', encoding='utf-8')
file_handler.setLevel(logging.INFO)
file_formatter = logging.Formatter('%(asctime)s - %(levelname)s - %(message)s')
file_handler.setFormatter(file_formatter)

root_logger = logging.getLogger()
root_logger.addHandler(file_handler)
root_logger.setLevel(logging.INFO)

# Also add console handler
console_handler = logging.StreamHandler()
console_handler.setLevel(logging.INFO)
console_handler.setFormatter(file_formatter)
root_logger.addHandler(console_handler)

logger = logging.getLogger(__name__)

async def test_startup():
    """Test startup exactly as in main.py"""
    QTSW2_ROOT = Path(__file__).parent.parent.parent
    SCHEDULE_CONFIG_FILE = QTSW2_ROOT / "automation" / "schedule_config.json"
    orchestrator_instance = None
    
    logger.info("=" * 60)
    logger.info("Testing FastAPI Startup Sequence")
    logger.info("=" * 60)
    
    # Initialize orchestrator (exact code from main.py)
    try:
        logger.info("Initializing Pipeline Orchestrator...")
        from orchestrator import PipelineOrchestrator, OrchestratorConfig
        
        logger.info("Creating orchestrator config...")
        config = OrchestratorConfig.from_environment(qtsw2_root=QTSW2_ROOT)
        logger.info(f"Config created: event_logs_dir={config.event_logs_dir}")
        
        logger.info("Creating orchestrator instance...")
        orchestrator_instance = PipelineOrchestrator(
            config=config,
            schedule_config_path=SCHEDULE_CONFIG_FILE,
            logger=logger
        )
        logger.info("Orchestrator instance created")
        
        # Start orchestrator (scheduler and watchdog)
        logger.info("Starting orchestrator (scheduler and watchdog)...")
        await orchestrator_instance.start()
        logger.info("Pipeline Orchestrator started successfully")
        
        # Test getting status
        status = await orchestrator_instance.get_status()
        logger.info(f"Status check: {status}")
        
        # Stop
        await orchestrator_instance.stop()
        logger.info("Orchestrator stopped")
        
        logger.info("=" * 60)
        logger.info("✅ SUCCESS - All tests passed!")
        logger.info("=" * 60)
        
    except Exception as e:
        logger.error(f"Failed to start orchestrator: {e}", exc_info=True)
        import traceback
        logger.error(f"Full traceback:\n{traceback.format_exc()}")
        orchestrator_instance = None
        logger.info("=" * 60)
        logger.info("❌ FAILED - Check errors above")
        logger.info("=" * 60)

if __name__ == "__main__":
    asyncio.run(test_startup())

