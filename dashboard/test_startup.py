"""Test script to diagnose backend startup issues"""
import sys
from pathlib import Path

# Add project root to path
project_root = Path(__file__).parent.parent
if str(project_root) not in sys.path:
    sys.path.insert(0, str(project_root))

print(f"Project root: {project_root}")
print(f"Python path: {sys.path[:3]}")
print("\n" + "="*60)
print("Testing imports...")
print("="*60)

try:
    print("\n1. Testing dashboard.backend.main import...")
    from dashboard.backend.main import app
    print("   ✅ Success!")
except Exception as e:
    print(f"   ❌ Failed: {e}")
    import traceback
    traceback.print_exc()

try:
    print("\n2. Testing orchestrator import...")
    from dashboard.backend.orchestrator import PipelineOrchestrator
    print("   ✅ Success!")
except Exception as e:
    print(f"   ❌ Failed: {e}")
    import traceback
    traceback.print_exc()

try:
    print("\n3. Testing automation.config import...")
    from automation.config import PipelineConfig
    print("   ✅ Success!")
except Exception as e:
    print(f"   ❌ Failed: {e}")
    import traceback
    traceback.print_exc()

print("\n" + "="*60)
print("Done!")
print("="*60)

