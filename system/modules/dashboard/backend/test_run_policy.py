"""
Unit Tests for Run Policy Gate

Tests for can_run_pipeline() function - the single gate that determines
if a pipeline run is allowed based on health and run type.
"""

import pytest
from pathlib import Path
from datetime import datetime
from typing import List
import tempfile
import shutil

import sys
from pathlib import Path
import importlib.util
import types

# Add backend directory to path
backend_dir = Path(__file__).parent
if str(backend_dir) not in sys.path:
    sys.path.insert(0, str(backend_dir))

# Set up orchestrator package structure to avoid triggering __init__.py
orchestrator_path = backend_dir / "orchestrator"
orchestrator_pkg = types.ModuleType('orchestrator')
orchestrator_pkg.__path__ = [str(orchestrator_path)]
sys.modules['orchestrator'] = orchestrator_pkg

# Load run_history module directly
run_history_spec = importlib.util.spec_from_file_location(
    "orchestrator.run_history",
    orchestrator_path / "run_history.py"
)
run_history_module = importlib.util.module_from_spec(run_history_spec)
sys.modules['orchestrator.run_history'] = run_history_module
run_history_spec.loader.exec_module(run_history_module)
RunHistory = run_history_module.RunHistory
RunSummary = run_history_module.RunSummary
RunResult = run_history_module.RunResult

# Load run_health module
run_health_spec = importlib.util.spec_from_file_location(
    "orchestrator.run_health",
    orchestrator_path / "run_health.py"
)
run_health_module = importlib.util.module_from_spec(run_health_spec)
run_health_module.__dict__['run_history'] = run_history_module
sys.modules['orchestrator.run_health'] = run_health_module
run_health_spec.loader.exec_module(run_health_module)
RunHealth = run_health_module.RunHealth
compute_run_health = run_health_module.compute_run_health

# Load run_policy module
run_policy_spec = importlib.util.spec_from_file_location(
    "orchestrator.run_policy",
    orchestrator_path / "run_policy.py"
)
run_policy_module = importlib.util.module_from_spec(run_policy_spec)
run_policy_module.__dict__['run_health'] = run_health_module
run_policy_module.__dict__['run_history'] = run_history_module
sys.modules['orchestrator.run_policy'] = run_policy_module
run_policy_spec.loader.exec_module(run_policy_module)
can_run_pipeline = run_policy_module.can_run_pipeline


class TestCanRunPipeline:
    """Tests for can_run_pipeline() function"""
    
    def setup_method(self):
        """Create a temporary directory for test runs"""
        self.temp_dir = tempfile.mkdtemp()
        self.runs_dir = Path(self.temp_dir) / "runs"
        self.runs_dir.mkdir(parents=True, exist_ok=True)
        self.run_history = RunHistory(runs_dir=self.runs_dir)
    
    def teardown_method(self):
        """Clean up temporary directory"""
        shutil.rmtree(self.temp_dir, ignore_errors=True)
    
    def _create_run_summary(
        self,
        run_id: str,
        result: str = RunResult.SUCCESS.value,
        failure_reason: str = None,
        stages_executed: List[str] = None,
        stages_failed: List[str] = None,
        metadata: dict = None
    ) -> RunSummary:
        """Helper to create a RunSummary"""
        now = datetime.now().isoformat()
        return RunSummary(
            run_id=run_id,
            started_at=now,
            ended_at=now,
            result=result,
            failure_reason=failure_reason,
            stages_executed=stages_executed or [],
            stages_failed=stages_failed or [],
            metadata=metadata or {}
        )
    
    def _create_healthy_history(self):
        """Helper to create a healthy run history"""
        summary = self._create_run_summary("run-1", result=RunResult.SUCCESS.value)
        self.run_history.persist_run(summary)
    
    def _create_blocked_history(self):
        """Helper to create a blocked run history (force lock clear)"""
        summary = self._create_run_summary(
            "run-1",
            result=RunResult.SUCCESS.value,
            metadata={"force_lock_clear": True}
        )
        self.run_history.persist_run(summary)
    
    def _create_unstable_history(self):
        """Helper to create an unstable run history (3+ failures)"""
        for i in range(3):
            summary = self._create_run_summary(f"run-{i}", result=RunResult.FAILED.value)
            self.run_history.persist_run(summary)
    
    def _create_degraded_history(self):
        """Helper to create a degraded run history (same stage failed twice)"""
        summary1 = self._create_run_summary(
            "run-1",
            result=RunResult.FAILED.value,
            stages_failed=["data_export"]
        )
        self.run_history.persist_run(summary1)
        
        summary2 = self._create_run_summary(
            "run-2",
            result=RunResult.FAILED.value,
            stages_failed=["data_export"]
        )
        self.run_history.persist_run(summary2)
    
    # ========== AUTO-RUN TESTS ==========
    
    def test_auto_run_healthy_allowed(self):
        """Test: Auto-run with healthy system is allowed"""
        self._create_healthy_history()
        
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=True,
            manual_override=False
        )
        
        assert allowed is True
        assert reason is None
        assert health == RunHealth.HEALTHY
    
    def test_auto_run_unstable_blocked(self):
        """Test: Auto-run with unstable system is blocked"""
        self._create_unstable_history()
        
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=True,
            manual_override=False
        )
        
        assert allowed is False
        assert reason is not None
        assert "auto-run blocked" in reason.lower()
        assert "unstable" in reason.lower()
        assert health == RunHealth.UNSTABLE
    
    def test_auto_run_degraded_blocked(self):
        """Test: Auto-run with degraded system is blocked"""
        self._create_degraded_history()
        
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=True,
            manual_override=False
        )
        
        assert allowed is False
        assert reason is not None
        assert "auto-run blocked" in reason.lower()
        assert "degraded" in reason.lower()
        assert health == RunHealth.DEGRADED
    
    def test_auto_run_blocked_health_blocked(self):
        """Test: Auto-run with blocked health is blocked"""
        self._create_blocked_history()
        
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=True,
            manual_override=False
        )
        
        assert allowed is False
        assert reason is not None
        assert "auto-run blocked" in reason.lower()
        assert "blocked" in reason.lower()
        assert health == RunHealth.BLOCKED
    
    def test_auto_run_override_ignored(self):
        """Test: Manual override is ignored for auto-runs"""
        self._create_unstable_history()
        
        # Even with override, auto-run should be blocked
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=True,
            manual_override=True  # Override should be ignored
        )
        
        assert allowed is False
        assert health == RunHealth.UNSTABLE
    
    # ========== MANUAL RUN TESTS ==========
    
    def test_manual_run_healthy_allowed(self):
        """Test: Manual run with healthy system is allowed"""
        self._create_healthy_history()
        
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=False,
            manual_override=False
        )
        
        assert allowed is True
        assert reason is None
        assert health == RunHealth.HEALTHY
    
    def test_manual_run_blocked_never_allowed(self):
        """Test: Manual run with blocked health is NEVER allowed (even with override)"""
        self._create_blocked_history()
        
        # Without override
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=False,
            manual_override=False
        )
        
        assert allowed is False
        assert "cannot override" in reason.lower()
        assert health == RunHealth.BLOCKED
        
        # With override (should still be blocked)
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=False,
            manual_override=True
        )
        
        assert allowed is False
        assert "cannot override" in reason.lower()
        assert health == RunHealth.BLOCKED
    
    def test_manual_run_degraded_requires_override(self):
        """Test: Manual run with degraded health requires override"""
        self._create_degraded_history()
        
        # Without override - should be blocked
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=False,
            manual_override=False
        )
        
        assert allowed is False
        assert "requires override" in reason.lower()
        assert health == RunHealth.DEGRADED
        
        # With override - should be allowed
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=False,
            manual_override=True
        )
        
        assert allowed is True
        assert "allowed with override" in reason.lower()
        assert health == RunHealth.DEGRADED
    
    def test_manual_run_unstable_requires_override(self):
        """Test: Manual run with unstable health requires override"""
        self._create_unstable_history()
        
        # Without override - should be blocked
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=False,
            manual_override=False
        )
        
        assert allowed is False
        assert "requires override" in reason.lower()
        assert health == RunHealth.UNSTABLE
        
        # With override - should be allowed
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=False,
            manual_override=True
        )
        
        assert allowed is True
        assert "allowed with override" in reason.lower()
        assert health == RunHealth.UNSTABLE
    
    # ========== EDGE CASES ==========
    
    def test_no_history_auto_run_allowed(self):
        """Test: Auto-run with no history is allowed (healthy by default)"""
        # No runs persisted
        
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=True,
            manual_override=False
        )
        
        assert allowed is True
        assert health == RunHealth.HEALTHY
    
    def test_no_history_manual_run_allowed(self):
        """Test: Manual run with no history is allowed"""
        # No runs persisted
        
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=False,
            manual_override=False
        )
        
        assert allowed is True
        assert health == RunHealth.HEALTHY
    
    def test_reason_includes_health_reasons(self):
        """Test: Block reason includes health determination reasons"""
        self._create_unstable_history()
        
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=True,
            manual_override=False
        )
        
        assert allowed is False
        assert reason is not None
        assert len(health_reasons) > 0
        # Reason should reference the health state
        assert health.value in reason.lower()
    
    def test_allowed_returns_none_reason(self):
        """Test: When allowed, reason is None"""
        self._create_healthy_history()
        
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=True,
            manual_override=False
        )
        
        assert allowed is True
        assert reason is None
    
    def test_health_reasons_always_present(self):
        """Test: Health reasons are always returned (even when allowed)"""
        self._create_healthy_history()
        
        allowed, reason, health, health_reasons = can_run_pipeline(
            self.run_history,
            auto_run=True,
            manual_override=False
        )
        
        assert len(health_reasons) > 0
        assert isinstance(health_reasons, list)

