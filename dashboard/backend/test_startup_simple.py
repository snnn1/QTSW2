"""
Simple test to see what happens during startup
"""
import asyncio
import sys
from pathlib import Path

# Add to path
sys.path.insert(0, str(Path(__file__).parent))

async def test():
    print("Testing startup...")
    try:
        # Import main module
        print("1. Importing main...")
        import main
        print("   ✅ Imported")
        
        # Check if orchestrator_instance exists
        print("2. Checking orchestrator_instance...")
        if hasattr(main, 'orchestrator_instance'):
            print(f"   orchestrator_instance = {main.orchestrator_instance}")
        else:
            print("   orchestrator_instance not found")
        
        # Try to access app
        print("3. Checking app...")
        if hasattr(main, 'app'):
            print(f"   ✅ App exists: {main.app}")
        else:
            print("   ❌ App not found")
            
    except Exception as e:
        print(f"❌ Error: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    asyncio.run(test())

