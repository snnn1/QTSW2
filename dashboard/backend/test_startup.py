"""
Test orchestrator startup to catch errors
"""
import asyncio
import logging
from pathlib import Path
import sys

# Setup logging
logging.basicConfig(level=logging.DEBUG, format='%(levelname)s: %(message)s')
logger = logging.getLogger(__name__)

async def test_startup():
    """Test orchestrator startup"""
    try:
        print("=" * 60)
        print("Testing Orchestrator Startup")
        print("=" * 60)
        print()
        
        print("1. Importing orchestrator...")
        from orchestrator import PipelineOrchestrator, OrchestratorConfig
        print("   ✅ Imported")
        print()
        
        print("2. Creating config...")
        qtsw2_root = Path(__file__).parent.parent.parent
        config = OrchestratorConfig.from_environment(qtsw2_root=qtsw2_root)
        print("   ✅ Config created")
        print()
        
        print("3. Creating orchestrator instance...")
        schedule_config_path = qtsw2_root / "automation" / "schedule_config.json"
        orchestrator = PipelineOrchestrator(
            config=config,
            schedule_config_path=schedule_config_path,
            logger=logger
        )
        print("   ✅ Instance created")
        print()
        
        print("4. Starting orchestrator...")
        await orchestrator.start()
        print("   ✅ Started successfully!")
        print()
        
        print("5. Getting status...")
        status = await orchestrator.get_status()
        print(f"   Status: {status}")
        print()
        
        print("6. Stopping orchestrator...")
        await orchestrator.stop()
        print("   ✅ Stopped successfully!")
        print()
        
        print("=" * 60)
        print("✅ All tests passed!")
        print("=" * 60)
        
    except Exception as e:
        print()
        print("=" * 60)
        print("❌ ERROR OCCURRED")
        print("=" * 60)
        print(f"Error type: {type(e).__name__}")
        print(f"Error message: {str(e)}")
        print()
        print("Full traceback:")
        import traceback
        traceback.print_exc()
        print("=" * 60)

if __name__ == "__main__":
    asyncio.run(test_startup())

