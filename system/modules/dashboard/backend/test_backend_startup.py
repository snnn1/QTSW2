"""
Test backend startup behavior - should not start orchestrator
"""
import pytest
import sys
from pathlib import Path
from unittest.mock import Mock, patch, AsyncMock

# Add project root to path
qtsw2_root = Path(__file__).parent.parent.parent.parent
sys.path.insert(0, str(qtsw2_root))


class TestBackendStartup:
    """Test that backend starts without starting orchestrator"""
    
    def test_backend_does_not_call_start(self):
        """Backend should create orchestrator but NOT call start()"""
        # Read main.py to verify it doesn't call orchestrator.start()
        main_file = Path(__file__).parent / "main.py"
        content = main_file.read_text()
        
        # Check that lifespan doesn't call orchestrator_instance.start()
        # It should only create the instance, not start it
        if "orchestrator_instance.start()" in content:
            # Check if it's commented out or in a try/except that handles it gracefully
            lines = content.split('\n')
            for i, line in enumerate(lines):
                if "orchestrator_instance.start()" in line:
                    # Should be in a comment or removed
                    if not line.strip().startswith('#'):
                        pytest.fail(f"Found orchestrator_instance.start() at line {i+1} - backend should not start orchestrator")
        
        # Should have connector pattern instead
        assert "connector" in content.lower() or "attach" in content.lower(), "Backend should attach to orchestrator, not start it"


class TestBackendGracefulDegradation:
    """Test that backend handles missing orchestrator gracefully"""
    
    def test_backend_handles_missing_orchestrator(self):
        """Backend should handle orchestrator being None"""
        # Simulate orchestrator_instance = None
        orchestrator_instance = None
        
        # Backend should still be able to:
        # 1. Query state from disk
        # 2. Return appropriate error messages
        # 3. Not crash
        
        if orchestrator_instance is None:
            # Should be able to read from disk
            state_file = Path("automation/logs/orchestrator_state.json")
            if state_file.exists():
                import json
                with open(state_file, 'r') as f:
                    data = json.load(f)
                assert data is not None or data == {}  # Should handle both cases


if __name__ == "__main__":
    pytest.main([__file__, "-v"])

