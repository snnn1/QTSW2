"""
Test orchestrator startup to find the error
"""
import asyncio
import logging
from pathlib import Path
import sys

logging.basicConfig(level=logging.DEBUG, format='%(levelname)s: %(message)s')
logger = logging.getLogger(__name__)

async def test():
    try:
        print("=" * 60)
        print("Testing Orchestrator Startup")
        print("=" * 60)
        print()
        
        print("1. Importing...")
        from orchestrator import PipelineOrchestrator, OrchestratorConfig
        print("   ✅ Imported")
        print()
        
        print("2. Creating config...")
        qtsw2_root = Path(__file__).parent.parent.parent
        config = OrchestratorConfig.from_environment(qtsw2_root=qtsw2_root)
        print("   ✅ Config created")
        print()
        
        print("3. Creating orchestrator...")
        schedule_config_path = qtsw2_root / "automation" / "schedule_config.json"
        orchestrator = PipelineOrchestrator(
            config=config,
            schedule_config_path=schedule_config_path,
            logger=logger
        )
        print("   ✅ Orchestrator created")
        print()
        
        print("4. Starting orchestrator...")
        await orchestrator.start()
        print("   ✅ Started!")
        print()
        
        print("5. Getting status...")
        status = await orchestrator.get_status()
        print(f"   Status: {status}")
        print()
        
        print("6. Stopping...")
        await orchestrator.stop()
        print("   ✅ Stopped")
        print()
        
        print("=" * 60)
        print("✅ SUCCESS!")
        print("=" * 60)
        
    except Exception as e:
        print()
        print("=" * 60)
        print("❌ ERROR!")
        print("=" * 60)
        print(f"Type: {type(e).__name__}")
        print(f"Message: {str(e)}")
        print()
        import traceback
        traceback.print_exc()
        print("=" * 60)

if __name__ == "__main__":
    asyncio.run(test())

