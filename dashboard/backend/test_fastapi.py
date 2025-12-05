"""
Test FastAPI app startup
"""
import sys
from pathlib import Path

# Add to path
sys.path.insert(0, str(Path(__file__).parent))

print("Testing FastAPI app startup...")
print()

try:
    print("1. Importing main...")
    from main import app
    print("   ✅ App imported")
    print()
    
    print("2. Checking routers...")
    print(f"   Routes: {len(app.routes)}")
    for route in app.routes[:5]:
        print(f"     - {route.path}")
    print()
    
    print("3. Testing orchestrator instance...")
    from main import orchestrator_instance
    if orchestrator_instance:
        print("   ✅ Orchestrator instance exists")
    else:
        print("   ⚠️  Orchestrator instance is None (might be expected if startup failed)")
    print()
    
    print("✅ FastAPI app looks good!")
    print()
    print("To start the server, run:")
    print("  python -m uvicorn main:app --reload")
    
except Exception as e:
    print()
    print("❌ ERROR:")
    print(f"   {type(e).__name__}: {str(e)}")
    print()
    import traceback
    traceback.print_exc()

