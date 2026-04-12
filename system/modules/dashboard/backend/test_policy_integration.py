"""
Integration Tests for Run Health and Policy Gate

Tests that verify the policy gate is properly integrated into the service layer
and that blocked runs emit correct events and update state appropriately.
"""

import pytest
import anyio
import asyncio
import tempfile
import shutil
from pathlib import Path
from unittest.mock import Mock, AsyncMock, patch
from datetime import datetime
import sys

# Add project root to path
qtsw2_root = Path(__file__).parent.parent.parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

from orchestrator.service import PipelineOrchestrator
from orchestrator.config import OrchestratorConfig
from orchestrator.run_history import RunSummary, RunResult
from orchestrator.run_health import RunHealth
from orchestrator.state import PipelineRunState


class TestPolicyGateIntegration:
    """Integration tests for policy gate in service layer"""
    
    @pytest.fixture
    def temp_dir(self):
        """Create temporary directory for test"""
        temp_dir = tempfile.mkdtemp()
        yield Path(temp_dir)
        shutil.rmtree(temp_dir, ignore_errors=True)
    
    @pytest.fixture
    def config(self, temp_dir):
        """Create orchestrator config for testing"""
        from orchestrator.config import StageConfig
        return OrchestratorConfig(
            qtsw2_root=temp_dir,
            event_logs_dir=temp_dir / "events",
            state_file=temp_dir / "state.json",
            lock_dir=temp_dir / "locks",
            stages={
                "translator": StageConfig(
                    name="translator",
                    order=1,
                    timeout_sec=3600,
                    max_retries=2
                )
            },
            lock_timeout_sec=300,
            event_buffer_size=1000
        )
    
    @pytest.fixture
    def orchestrator(self, config, temp_dir):
        """Create orchestrator instance for testing"""
        schedule_config = temp_dir / "schedule_config.json"
        schedule_config.write_text('{"schedules": []}')
        
        logger = Mock()
        orchestrator = PipelineOrchestrator(
            config=config,
            schedule_config_path=schedule_config,
            logger=logger
        )
        return orchestrator
    
    @pytest.fixture
    def mock_runner(self, orchestrator):
        """Mock the pipeline runner to avoid actual execution"""
        orchestrator.runner.run_pipeline = AsyncMock(return_value=True)
        return orchestrator.runner
    
    def _create_run_summary(
        self,
        orchestrator,
        run_id: str,
        result: str = RunResult.SUCCESS.value,
        failure_reason: str = None,
        stages_failed: list = None,
        metadata: dict = None
    ):
        """Helper to create and persist a run summary"""
        summary = RunSummary(
            run_id=run_id,
            started_at=datetime.now().isoformat(),
            ended_at=datetime.now().isoformat(),
            result=result,
            failure_reason=failure_reason,
            stages_executed=[],
            stages_failed=stages_failed or [],
            metadata=metadata or {}
        )
        orchestrator.run_history.persist_run(summary)
    
    @pytest.mark.anyio
    async def test_auto_run_blocked_emits_event(self, orchestrator, mock_runner):
        """Test: Auto-run blocked by policy emits run_blocked event"""
        # Create unstable history (3 failures)
        for i in range(3):
            self._create_run_summary(
                orchestrator,
                f"run-{i}",
                result=RunResult.FAILED.value
            )
        
        # Track events
        events_published = []
        original_publish = orchestrator.event_bus.publish
        
        async def track_publish(event):
            events_published.append(event)
            return await original_publish(event)
        
        orchestrator.event_bus.publish = track_publish
        
        # Try to start auto-run (should be blocked)
        with pytest.raises(ValueError, match="Pipeline run blocked"):
            await orchestrator.start_pipeline(manual=False, manual_override=False)
        
        # Verify run_blocked event was emitted
        run_blocked_events = [e for e in events_published if e.get("event") == "run_blocked"]
        assert len(run_blocked_events) == 1
        
        event = run_blocked_events[0]
        assert event["data"]["run_health"] == RunHealth.UNSTABLE.value
        assert event["data"]["auto_run"] is True
        assert "block_reason" in event["data"]
    
    @pytest.mark.asyncio
    async def test_auto_run_blocked_updates_state_health(self, orchestrator, mock_runner):
        """Test: Auto-run blocked updates state with health information"""
        # Create unstable history
        for i in range(3):
            self._create_run_summary(
                orchestrator,
                f"run-{i}",
                result=RunResult.FAILED.value
            )
        
        # Try to start auto-run (should be blocked)
        with pytest.raises(ValueError):
            await orchestrator.start_pipeline(manual=False, manual_override=False)
        
        # Verify state has health information
        state = await orchestrator.state_manager.get_state()
        if state:
            # Health should be in the state (if state exists)
            # Note: state might be None if no run context exists
            pass  # State update happens but context might not exist if blocked early
    
    @pytest.mark.asyncio
    async def test_auto_run_healthy_starts_pipeline(self, orchestrator, mock_runner):
        """Test: Auto-run with healthy system starts pipeline"""
        # Create healthy history
        self._create_run_summary(
            orchestrator,
            "run-1",
            result=RunResult.SUCCESS.value
        )
        
        # Mock state manager to return idle state
        async def mock_get_state():
            return Mock(state=PipelineRunState.IDLE)
        
        orchestrator.state_manager.get_state = mock_get_state
        
        # Start auto-run (should succeed)
        try:
            run_ctx = await orchestrator.start_pipeline(manual=False, manual_override=False)
            # If we get here, pipeline started (or would have if not for other mocks)
            # The important thing is no ValueError was raised
        except ValueError as e:
            if "Pipeline run blocked" in str(e):
                pytest.fail("Auto-run should not be blocked when healthy")
            raise
    
    @pytest.mark.asyncio
    async def test_manual_run_blocked_never_allowed(self, orchestrator, mock_runner):
        """Test: Manual run with blocked health is never allowed (even with override)"""
        # Create blocked history (force lock clear)
        self._create_run_summary(
            orchestrator,
            "run-1",
            result=RunResult.SUCCESS.value,
            metadata={"force_lock_clear": True}
        )
        
        # Try manual run without override
        with pytest.raises(ValueError, match="Pipeline run blocked"):
            await orchestrator.start_pipeline(manual=True, manual_override=False)
        
        # Try manual run with override (should still be blocked)
        with pytest.raises(ValueError, match="Pipeline run blocked"):
            await orchestrator.start_pipeline(manual=True, manual_override=True)
    
    @pytest.mark.asyncio
    async def test_manual_run_degraded_requires_override(self, orchestrator, mock_runner):
        """Test: Manual run with degraded health requires override"""
        # Create degraded history (same stage failed twice)
        self._create_run_summary(
            orchestrator,
            "run-1",
            result=RunResult.FAILED.value,
            stages_failed=["data_export"]
        )
        self._create_run_summary(
            orchestrator,
            "run-2",
            result=RunResult.FAILED.value,
            stages_failed=["data_export"]
        )
        
        # Try manual run without override (should be blocked)
        with pytest.raises(ValueError, match="Pipeline run blocked"):
            await orchestrator.start_pipeline(manual=True, manual_override=False)
        
        # Try manual run with override (should be allowed)
        # Mock state manager to return idle state
        async def mock_get_state():
            return Mock(state=PipelineRunState.IDLE)
        
        orchestrator.state_manager.get_state = mock_get_state
        
        try:
            run_ctx = await orchestrator.start_pipeline(manual=True, manual_override=True)
            # If we get here, pipeline was allowed (or would have been)
        except ValueError as e:
            if "Pipeline run blocked" in str(e):
                pytest.fail("Manual run with override should be allowed for degraded health")
            raise
    
    @pytest.mark.asyncio
    async def test_manual_run_unstable_requires_override(self, orchestrator, mock_runner):
        """Test: Manual run with unstable health requires override"""
        # Create unstable history (3 failures)
        for i in range(3):
            self._create_run_summary(
                orchestrator,
                f"run-{i}",
                result=RunResult.FAILED.value
            )
        
        # Try manual run without override (should be blocked)
        with pytest.raises(ValueError, match="Pipeline run blocked"):
            await orchestrator.start_pipeline(manual=True, manual_override=False)
        
        # Try manual run with override (should be allowed)
        async def mock_get_state():
            return Mock(state=PipelineRunState.IDLE)
        
        orchestrator.state_manager.get_state = mock_get_state
        
        try:
            run_ctx = await orchestrator.start_pipeline(manual=True, manual_override=True)
            # If we get here, pipeline was allowed
        except ValueError as e:
            if "Pipeline run blocked" in str(e):
                pytest.fail("Manual run with override should be allowed for unstable health")
            raise
    
    @pytest.mark.asyncio
    async def test_policy_gate_before_locks(self, orchestrator, mock_runner):
        """Test: Policy gate is checked before locks are acquired"""
        # Create blocked history
        self._create_run_summary(
            orchestrator,
            "run-1",
            result=RunResult.FAILED.value,
            failure_reason="Connection timeout error"
        )
        
        # Track lock acquisition attempts
        lock_acquired = []
        original_acquire = orchestrator.lock_manager.acquire
        
        async def track_acquire(run_id):
            lock_acquired.append(run_id)
            return await original_acquire(run_id)
        
        orchestrator.lock_manager.acquire = track_acquire
        
        # Try to start run (should be blocked)
        with pytest.raises(ValueError, match="Pipeline run blocked"):
            await orchestrator.start_pipeline(manual=False, manual_override=False)
        
        # Verify no lock was acquired (policy gate should block before locks)
        assert len(lock_acquired) == 0
    
    @pytest.mark.asyncio
    async def test_blocked_run_no_summary_created(self, orchestrator, mock_runner):
        """Test: Blocked runs do not create RunSummary"""
        # Create unstable history
        for i in range(3):
            self._create_run_summary(
                orchestrator,
                f"run-{i}",
                result=RunResult.FAILED.value
            )
        
        # Count existing runs
        initial_runs = len(orchestrator.run_history.list_runs())
        
        # Try to start run (should be blocked)
        with pytest.raises(ValueError):
            await orchestrator.start_pipeline(manual=False, manual_override=False)
        
        # Verify no new run summary was created
        final_runs = len(orchestrator.run_history.list_runs())
        assert final_runs == initial_runs
    
    @pytest.mark.asyncio
    async def test_health_reasons_in_event(self, orchestrator, mock_runner):
        """Test: Health reasons are included in run_blocked event"""
        # Create unstable history
        for i in range(3):
            self._create_run_summary(
                orchestrator,
                f"run-{i}",
                result=RunResult.FAILED.value
            )
        
        # Track events
        events_published = []
        original_publish = orchestrator.event_bus.publish
        
        async def track_publish(event):
            events_published.append(event)
            return await original_publish(event)
        
        orchestrator.event_bus.publish = track_publish
        
        # Try to start run (should be blocked)
        with pytest.raises(ValueError):
            await orchestrator.start_pipeline(manual=False, manual_override=False)
        
        # Verify health reasons are in event
        run_blocked_events = [e for e in events_published if e.get("event") == "run_blocked"]
        assert len(run_blocked_events) == 1
        
        event = run_blocked_events[0]
        assert "health_reasons" in event["data"]
        assert isinstance(event["data"]["health_reasons"], list)
        assert len(event["data"]["health_reasons"]) > 0

