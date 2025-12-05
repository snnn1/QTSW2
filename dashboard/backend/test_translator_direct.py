"""
Test Translator Service Directly
"""

import asyncio
import sys
import logging
from pathlib import Path
from datetime import datetime

# Add project root to path
qtsw2_root = Path(__file__).parent.parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

from automation.config import PipelineConfig
from automation.logging_setup import create_logger
from automation.services.event_logger import EventLogger
from automation.services.process_supervisor import ProcessSupervisor
from automation.services.file_manager import FileManager
from automation.pipeline.stages.translator import TranslatorService


async def test_translator():
    """Test translator service directly"""
    print("="*60)
    print("  TESTING TRANSLATOR SERVICE DIRECTLY")
    print("="*60)
    print()
    
    # Setup logging
    log_file = qtsw2_root / "automation" / "logs" / f"test_translator_{datetime.now().strftime('%Y%m%d_%H%M%S')}.log"
    logger = create_logger("TestTranslator", log_file, level=logging.DEBUG)
    
    print(f"Log file: {log_file}")
    print()
    
    try:
        # Load config
        print("1. Loading configuration...")
        config = PipelineConfig.from_environment()
        print(f"   ✓ Config loaded")
        print(f"   → Raw data: {config.data_raw}")
        print(f"   → Processed data: {config.data_processed}")
        print(f"   → Translator script: {config.translator_script}")
        print()
        
        # Check raw files
        print("2. Checking for raw files...")
        file_manager = FileManager(logger, lock_timeout=300)
        raw_files = file_manager.scan_directory(config.data_raw, "*.csv")
        print(f"   → Found {len(raw_files)} raw CSV file(s)")
        if raw_files:
            for f in raw_files[:5]:
                print(f"     • {f.name}")
        print()
        
        if not raw_files:
            print("   ❌ No raw files found! Cannot test translator.")
            return
        
        # Setup event logger
        print("3. Setting up event logger...")
        event_log_file = qtsw2_root / "automation" / "logs" / "events" / f"test_translator_{datetime.now().strftime('%Y%m%d_%H%M%S')}.jsonl"
        event_logger = EventLogger(event_log_file, logger=logger)
        print(f"   ✓ Event logger ready: {event_log_file}")
        print()
        
        # Create services
        print("4. Creating services...")
        process_supervisor = ProcessSupervisor(
            logger,
            timeout_seconds=config.translator_timeout
        )
        print(f"   ✓ Process supervisor created (timeout: {config.translator_timeout}s)")
        
        translator_service = TranslatorService(
            config, logger, process_supervisor, file_manager, event_logger
        )
        print(f"   ✓ Translator service created")
        print()
        
        # Run translator
        print("5. Running translator...")
        print("   (This may take a while...)")
        print()
        
        run_id = f"test_{datetime.now().strftime('%Y%m%d_%H%M%S')}"
        result = translator_service.run(run_id)
        
        print()
        print("="*60)
        print("  TRANSLATOR RESULT")
        print("="*60)
        print(f"Status: {result.status}")
        print(f"Raw files found: {result.raw_files_found}")
        print(f"Files written: {result.files_written}")
        print(f"Processed files: {len(result.processed_files)}")
        print(f"Duration: {result.duration_seconds:.2f} seconds")
        if result.error_message:
            print(f"Error: {result.error_message}")
        print()
        
        if result.status == "success":
            print("✓ Translator completed successfully!")
            if result.processed_files:
                print(f"\nProcessed files:")
                for f in list(result.processed_files)[:10]:
                    print(f"  • {f.name}")
        else:
            print(f"❌ Translator failed: {result.error_message}")
            print(f"\nCheck the log file for details: {log_file}")
        
    except Exception as e:
        print(f"\n❌ Exception during test: {e}")
        import traceback
        traceback.print_exc()
        logger.error(f"Test failed: {e}", exc_info=True)


if __name__ == "__main__":
    asyncio.run(test_translator())

