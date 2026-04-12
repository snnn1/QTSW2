"""
Test quant-grade requirements are met
"""
import pytest
import json
import tempfile
from pathlib import Path
import sys

# Add project root to path
qtsw2_root = Path(__file__).parent.parent.parent.parent
sys.path.insert(0, str(qtsw2_root))


class TestQuantGradeRequirements:
    """Verify all quant-grade requirements are met"""
    
    def test_state_persistence_exists(self):
        """State should persist to disk"""
        from modules.orchestrator.state import PipelineStateManager
        from modules.orchestrator.events import EventBus
        from unittest.mock import Mock
        
        # State manager should accept state_file parameter
        event_bus = Mock(spec=EventBus)
        logger = Mock()
        state_file = Path("test_state.json")
        
        manager = PipelineStateManager(
            event_bus=event_bus,
            logger=logger,
            state_file=state_file
        )
        
        # Should have save/load methods
        assert hasattr(manager, '_save_state')
        assert hasattr(manager, '_load_state')
    
    def test_no_dynamic_reload_in_main(self):
        """main.py should not contain dynamic module reloading"""
        main_file = Path(__file__).parent / "main.py"
        if main_file.exists():
            content = main_file.read_text()
            
            # Should NOT contain these patterns
            bad_patterns = [
                "importlib.reload",
                "exec_module",
                "spec_from_file_location",
                "del sys.modules",
            ]
            
            for pattern in bad_patterns:
                # Allow in comments or docstrings, but not in active code
                # Simple check - pattern should not appear in function bodies
                if pattern in content:
                    # Check if it's in a comment
                    lines = content.split('\n')
                    for i, line in enumerate(lines):
                        if pattern in line and not line.strip().startswith('#'):
                            # Check if it's in a string literal
                            if not (line.strip().startswith('"') or line.strip().startswith("'")):
                                pytest.fail(f"Found {pattern} in main.py - dynamic reloading should be removed")
    
    def test_no_subprocess_popen_for_pipelines(self):
        """main.py should not use subprocess.Popen for pipeline execution"""
        main_file = Path(__file__).parent / "main.py"
        if main_file.exists():
            content = main_file.read_text()
            
            # Check for subprocess.Popen with scheduler or pipeline scripts
            # Allow subprocess for Streamlit apps (acceptable)
            lines = content.split('\n')
            for i, line in enumerate(lines):
                if "subprocess.Popen" in line:
                    # Check if it's for pipeline execution (bad) vs app launch (ok)
                    if any(bad in line.lower() for bad in ["scheduler", "pipeline", "merger"]):
                        if "streamlit" not in line.lower() and "app" not in line.lower():
                            pytest.fail(f"Found subprocess.Popen for pipeline execution at line {i+1}")
    
    def test_orchestrator_config_has_state_file(self):
        """OrchestratorConfig should include state_file"""
        from modules.orchestrator.config import OrchestratorConfig
        
        config = OrchestratorConfig.from_environment()
        
        # Should have state_file attribute
        assert hasattr(config, 'state_file')
        assert config.state_file is not None
        assert isinstance(config.state_file, Path)


if __name__ == "__main__":
    pytest.main([__file__, "-v"])

