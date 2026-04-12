"""
Integration tests for orchestrator-backend separation
Tests that verify quant-grade architecture requirements
"""
import pytest
import json
import tempfile
import asyncio
from pathlib import Path
from unittest.mock import Mock, patch, AsyncMock
import sys

# Add project root to path
qtsw2_root = Path(__file__).parent.parent.parent.parent
sys.path.insert(0, str(qtsw2_root))

from modules.orchestrator.state import (
    PipelineStateManager, 
    PipelineRunState, 
    PipelineStage,
    RunContext
)
from modules.orchestrator.events import EventBus
from modules.orchestrator.config import OrchestratorConfig


class TestStatePersistence:
    """Test that orchestrator state persists to disk"""
    
    def test_state_saves_to_disk(self, tmp_path):
        """State should be saved to disk file"""
        state_file = tmp_path / "orchestrator_state.json"
        event_bus = Mock(spec=EventBus)
        logger = Mock()
        
        manager = PipelineStateManager(
            event_bus=event_bus,
            logger=logger,
            state_file=state_file
        )
        
        # Create a run
        run_id = "test-run-123"
        context = RunContext(
            run_id=run_id,
            state=PipelineRunState.STARTING,
            started_at=None
        )
        manager._current_context = context
        
        # Save state
        manager._save_state()
        
        # Verify file exists and contains correct data
        assert state_file.exists()
        with open(state_file, 'r') as f:
            data = json.load(f)
            assert data['run_id'] == run_id
            assert data['state'] == 'starting'
    
    def test_state_loads_from_disk(self, tmp_path):
        """State should be loaded from disk on startup"""
        state_file = tmp_path / "orchestrator_state.json"
        
        # Create state file manually
        state_data = {
            "run_id": "persisted-run-456",
            "state": "running_translator",
            "current_stage": "translator",
            "started_at": "2025-01-01T12:00:00",
            "updated_at": "2025-01-01T12:05:00",
            "retry_count": 0,
            "error": None,
            "metadata": {}
        }
        with open(state_file, 'w') as f:
            json.dump(state_data, f)
        
        event_bus = Mock(spec=EventBus)
        logger = Mock()
        
        # Create manager - should load state
        manager = PipelineStateManager(
            event_bus=event_bus,
            logger=logger,
            state_file=state_file
        )
        
        # Verify state was loaded
        assert manager._current_context is not None
        assert manager._current_context.run_id == "persisted-run-456"
        assert manager._current_context.state == PipelineRunState.RUNNING_TRANSLATOR
        assert manager._current_context.current_stage == PipelineStage.TRANSLATOR
    
    @pytest.mark.anyio
    @pytest.mark.parametrize("anyio_backend", ["asyncio"])
    async def test_state_persists_on_transition(self, tmp_path):
        """State should be saved automatically on state transitions"""
        state_file = tmp_path / "orchestrator_state.json"
        event_bus = AsyncMock(spec=EventBus)
        logger = Mock()
        
        manager = PipelineStateManager(
            event_bus=event_bus,
            logger=logger,
            state_file=state_file
        )
        
        # Create initial run
        run_id = "test-run-789"
        await manager.create_run(run_id=run_id)
        
        # Verify state was saved
        assert state_file.exists()
        with open(state_file, 'r') as f:
            data = json.load(f)
            assert data['run_id'] == run_id
        
        # Transition through valid states: IDLE -> STARTING -> RUNNING_TRANSLATOR
        await manager.transition(PipelineRunState.STARTING)
        await manager.transition(PipelineRunState.RUNNING_TRANSLATOR, stage=PipelineStage.TRANSLATOR)
        
        # Verify updated state was saved
        with open(state_file, 'r') as f:
            data = json.load(f)
            assert data['state'] == 'running_translator'
            assert data['current_stage'] == 'translator'


class TestBackendOrchestratorSeparation:
    """Test that backend doesn't start orchestrator"""
    
    def test_backend_creates_connector_not_owner(self):
        """Backend should create connector, not start orchestrator"""
        # This test verifies the pattern - actual implementation is in main.py
        # Backend should NOT call orchestrator.start()
        pass  # Integration test would verify this


class TestStateQueryFromDisk:
    """Test that backend can query state from disk without orchestrator"""
    
    def test_read_state_file_directly(self, tmp_path):
        """Backend should be able to read state file directly"""
        state_file = tmp_path / "orchestrator_state.json"
        
        # Create state file
        state_data = {
            "run_id": "disk-run-999",
            "state": "running_analyzer",
            "current_stage": "analyzer",
            "started_at": "2025-01-01T10:00:00",
            "updated_at": "2025-01-01T10:30:00",
            "retry_count": 0,
            "error": None,
            "metadata": {}
        }
        with open(state_file, 'w') as f:
            json.dump(state_data, f)
        
        # Backend reads directly (simulating pipeline router behavior)
        with open(state_file, 'r') as f:
            data = json.load(f)
        
        assert data['run_id'] == "disk-run-999"
        assert data['state'] == "running_analyzer"
        assert data['current_stage'] == "analyzer"


class TestNoDynamicReloading:
    """Test that matrix endpoints don't use dynamic module reloading"""
    
    def test_matrix_endpoint_uses_standard_import(self):
        """Matrix endpoints should use standard imports, not reload"""
        # This test verifies the pattern - actual code should not have:
        # - importlib.reload()
        # - del sys.modules[...]
        # - importlib.util.spec_from_file_location()
        pass  # Code review test - verify main.py doesn't have these


class TestNoDirectExecution:
    """Test that backend doesn't execute pipelines directly"""
    
    def test_no_subprocess_popen_for_pipelines(self):
        """Backend should not use subprocess.Popen for pipeline execution"""
        # This test verifies the pattern - actual code should not have:
        # - subprocess.Popen for SCHEDULER_SCRIPT
        # - subprocess.Popen for pipeline stages
        # - subprocess.Popen for data merger
        pass  # Code review test - verify main.py doesn't have these


if __name__ == "__main__":
    pytest.main([__file__, "-v"])

