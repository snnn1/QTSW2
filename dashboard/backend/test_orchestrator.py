"""
Quick test script to diagnose orchestrator issues
"""
import sys
from pathlib import Path

print("=" * 60)
print("Orchestrator Diagnostic Test")
print("=" * 60)
print()

# Test 1: Check Python version
print("1. Python version:")
print(f"   {sys.version}")
print()

# Test 2: Check paths
print("2. Paths:")
qtsw2_root = Path(__file__).parent.parent.parent
print(f"   QTSW2_ROOT: {qtsw2_root}")
print(f"   Exists: {qtsw2_root.exists()}")
print()

# Test 3: Check automation package
print("3. Automation package:")
automation_path = qtsw2_root / "automation"
print(f"   Path: {automation_path}")
print(f"   Exists: {automation_path.exists()}")
if automation_path.exists():
    config_file = automation_path / "config.py"
    print(f"   config.py exists: {config_file.exists()}")
print()

# Test 4: Check orchestrator package
print("4. Orchestrator package:")
orchestrator_path = Path(__file__).parent / "orchestrator"
print(f"   Path: {orchestrator_path}")
print(f"   Exists: {orchestrator_path.exists()}")
if orchestrator_path.exists():
    files = list(orchestrator_path.glob("*.py"))
    print(f"   Files: {len(files)}")
    for f in files:
        print(f"     - {f.name}")
print()

# Test 5: Try importing
print("5. Import tests:")
try:
    from orchestrator.config import OrchestratorConfig
    print("   ✅ OrchestratorConfig imported")
except Exception as e:
    print(f"   ❌ OrchestratorConfig failed: {e}")

try:
    from orchestrator.state import PipelineRunState, PipelineStage
    print("   ✅ State classes imported")
except Exception as e:
    print(f"   ❌ State classes failed: {e}")

try:
    from orchestrator.events import EventBus
    print("   ✅ EventBus imported")
except Exception as e:
    print(f"   ❌ EventBus failed: {e}")

try:
    from orchestrator.service import PipelineOrchestrator
    print("   ✅ PipelineOrchestrator imported")
except Exception as e:
    print(f"   ❌ PipelineOrchestrator failed: {e}")

# Test 6: Try importing automation
print()
print("6. Automation package import:")
sys.path.insert(0, str(qtsw2_root))
try:
    from automation.config import PipelineConfig
    print("   ✅ PipelineConfig imported")
except Exception as e:
    print(f"   ❌ PipelineConfig failed: {e}")

try:
    from automation.pipeline.stages.translator import TranslatorService
    print("   ✅ TranslatorService imported")
except Exception as e:
    print(f"   ❌ TranslatorService failed: {e}")

# Test 7: Try creating config
print()
print("7. Config creation:")
try:
    from orchestrator.config import OrchestratorConfig
    config = OrchestratorConfig.from_environment(qtsw2_root=qtsw2_root)
    print("   ✅ Config created")
    print(f"   Event logs dir: {config.event_logs_dir}")
    print(f"   Lock dir: {config.lock_dir}")
except Exception as e:
    print(f"   ❌ Config creation failed: {e}")
    import traceback
    traceback.print_exc()

print()
print("=" * 60)
print("Diagnostic complete!")
print("=" * 60)

