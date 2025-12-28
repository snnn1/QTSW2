"""
Unit Tests for Run Health Model

Tests for compute_run_health() function - pure function that determines
pipeline health from run history.
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

# Load run_history module directly (no problematic dependencies)
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

# Load run_health module (depends on run_history)
run_health_spec = importlib.util.spec_from_file_location(
    "orchestrator.run_health",
    orchestrator_path / "run_health.py"
)
run_health_module = importlib.util.module_from_spec(run_health_spec)
# Inject run_history into run_health's namespace for relative import
run_health_module.__dict__['run_history'] = run_history_module
sys.modules['orchestrator.run_health'] = run_health_module
run_health_spec.loader.exec_module(run_health_module)
RunHealth = run_health_module.RunHealth
compute_run_health = run_health_module.compute_run_health


class TestComputeRunHealth:
    """Tests for compute_run_health() function"""
    
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
    
    def test_no_history_returns_healthy(self):
        """Test: No run history returns healthy"""
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.HEALTHY
        assert "No run history" in reasons[0] or "assuming healthy" in reasons[0]
    
    def test_healthy_with_successful_runs(self):
        """Test: All successful runs return healthy"""
        # Add 5 successful runs
        for i in range(5):
            summary = self._create_run_summary(f"run-{i}", result=RunResult.SUCCESS.value)
            self.run_history.persist_run(summary)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.HEALTHY
        assert "No health issues detected" in reasons[0]
    
    def test_blocked_force_lock_clear(self):
        """Test: Force lock clear in metadata blocks"""
        summary = self._create_run_summary(
            "run-1",
            result=RunResult.SUCCESS.value,
            metadata={"force_lock_clear": True}
        )
        self.run_history.persist_run(summary)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.BLOCKED
        assert "force lock clear" in reasons[0].lower()
    
    def test_blocked_infrastructure_error_lock(self):
        """Test: Infrastructure error (lock) blocks"""
        summary = self._create_run_summary(
            "run-1",
            result=RunResult.FAILED.value,
            failure_reason="Failed to acquire lock file"
        )
        self.run_history.persist_run(summary)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.BLOCKED
        assert "infrastructure error" in reasons[0].lower()
    
    def test_blocked_infrastructure_error_permission(self):
        """Test: Infrastructure error (permission) blocks"""
        summary = self._create_run_summary(
            "run-1",
            result=RunResult.FAILED.value,
            failure_reason="Permission denied accessing file"
        )
        self.run_history.persist_run(summary)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.BLOCKED
    
    def test_blocked_infrastructure_error_disk(self):
        """Test: Infrastructure error (disk) blocks"""
        summary = self._create_run_summary(
            "run-1",
            result=RunResult.FAILED.value,
            failure_reason="Disk full error"
        )
        self.run_history.persist_run(summary)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.BLOCKED
    
    def test_unstable_three_failures_in_five_runs(self):
        """Test: 3 failures in last 5 runs returns unstable"""
        # Create 5 runs: 3 failed, 2 successful
        runs = [
            ("run-1", RunResult.FAILED.value),
            ("run-2", RunResult.SUCCESS.value),
            ("run-3", RunResult.FAILED.value),
            ("run-4", RunResult.FAILED.value),
            ("run-5", RunResult.SUCCESS.value),
        ]
        
        for run_id, result in runs:
            summary = self._create_run_summary(run_id, result=result)
            self.run_history.persist_run(summary)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.UNSTABLE
        assert "3 failures in last 5 runs" in reasons[0]
    
    def test_unstable_four_failures_in_five_runs(self):
        """Test: 4 failures in last 5 runs returns unstable"""
        # Create 5 runs: 4 failed, 1 successful
        runs = [
            ("run-1", RunResult.FAILED.value),
            ("run-2", RunResult.FAILED.value),
            ("run-3", RunResult.FAILED.value),
            ("run-4", RunResult.FAILED.value),
            ("run-5", RunResult.SUCCESS.value),
        ]
        
        for run_id, result in runs:
            summary = self._create_run_summary(run_id, result=result)
            self.run_history.persist_run(summary)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.UNSTABLE
        assert "4 failures in last 5 runs" in reasons[0]
    
    def test_unstable_all_five_failures(self):
        """Test: All 5 failures returns unstable"""
        for i in range(5):
            summary = self._create_run_summary(f"run-{i}", result=RunResult.FAILED.value)
            self.run_history.persist_run(summary)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.UNSTABLE
        assert "5 failures in last 5 runs" in reasons[0]
    
    def test_degraded_same_stage_failed_consecutive(self):
        """Test: Same stage failed in 2 consecutive runs returns degraded"""
        # First failed run
        summary1 = self._create_run_summary(
            "run-1",
            result=RunResult.FAILED.value,
            stages_failed=["data_export"]
        )
        self.run_history.persist_run(summary1)
        
        # Second failed run with same stage
        summary2 = self._create_run_summary(
            "run-2",
            result=RunResult.FAILED.value,
            stages_failed=["data_export"]
        )
        self.run_history.persist_run(summary2)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.DEGRADED
        assert "same stage" in reasons[0].lower()
        assert "data_export" in reasons[0]
    
    def test_degraded_multiple_stages_failed_consecutive(self):
        """Test: Multiple same stages failed in 2 consecutive runs returns degraded"""
        summary1 = self._create_run_summary(
            "run-1",
            result=RunResult.FAILED.value,
            stages_failed=["data_export", "matrix_generation"]
        )
        self.run_history.persist_run(summary1)
        
        summary2 = self._create_run_summary(
            "run-2",
            result=RunResult.FAILED.value,
            stages_failed=["data_export", "matrix_generation"]
        )
        self.run_history.persist_run(summary2)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.DEGRADED
        assert "same stage" in reasons[0].lower()
    
    def test_degraded_partial_stage_overlap(self):
        """Test: Partial stage overlap in consecutive failures returns degraded"""
        summary1 = self._create_run_summary(
            "run-1",
            result=RunResult.FAILED.value,
            stages_failed=["data_export", "matrix_generation"]
        )
        self.run_history.persist_run(summary1)
        
        summary2 = self._create_run_summary(
            "run-2",
            result=RunResult.FAILED.value,
            stages_failed=["data_export", "quant_grade"]  # Only data_export overlaps
        )
        self.run_history.persist_run(summary2)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.DEGRADED
        assert "data_export" in reasons[0]
    
    def test_not_degraded_different_stages_failed(self):
        """Test: Different stages failed in consecutive runs does NOT return degraded"""
        summary1 = self._create_run_summary(
            "run-1",
            result=RunResult.FAILED.value,
            stages_failed=["data_export"]
        )
        self.run_history.persist_run(summary1)
        
        summary2 = self._create_run_summary(
            "run-2",
            result=RunResult.FAILED.value,
            stages_failed=["matrix_generation"]  # Different stage
        )
        self.run_history.persist_run(summary2)
        
        health, reasons = compute_run_health(self.run_history)
        # Should be healthy (only 2 failures, not 3+, and different stages)
        assert health == RunHealth.HEALTHY
        assert "No health issues detected" in reasons[0]
    
    def test_priority_blocked_over_unstable(self):
        """Test: Blocked takes priority over unstable"""
        # Add failures that would make it unstable (but not blocked)
        for i in range(1, 4):
            summary = self._create_run_summary(f"run-{i}", result=RunResult.FAILED.value)
            self.run_history.persist_run(summary)
        
        # Create LAST run with infrastructure error (should be blocked)
        # Blocked check only applies to the most recent run
        summary = self._create_run_summary(
            "run-4",
            result=RunResult.FAILED.value,
            failure_reason="Connection timeout error"
        )
        self.run_history.persist_run(summary)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.BLOCKED  # Blocked takes priority (checked first)
        assert "infrastructure error" in reasons[0].lower()
    
    def test_priority_blocked_over_degraded(self):
        """Test: Blocked takes priority over degraded"""
        # Add consecutive failures with same stage (would normally be degraded)
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
        
        # Create LAST run with force lock clear (should be blocked)
        # Blocked check only applies to the most recent run
        summary3 = self._create_run_summary(
            "run-3",
            result=RunResult.SUCCESS.value,
            metadata={"force_lock_clear": True}
        )
        self.run_history.persist_run(summary3)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.BLOCKED  # Blocked takes priority (checked first)
    
    def test_priority_unstable_over_degraded(self):
        """Test: Unstable takes priority over degraded when both conditions exist"""
        # Create 3 failures (unstable) with same stage (degraded)
        for i in range(3):
            summary = self._create_run_summary(
                f"run-{i+1}",
                result=RunResult.FAILED.value,
                stages_failed=["data_export"]  # Same stage each time
            )
            self.run_history.persist_run(summary)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.UNSTABLE  # Unstable takes priority
        assert "3 failures in last 5 runs" in reasons[0]
    
    def test_application_error_not_blocked(self):
        """Test: Application errors (not infrastructure) do NOT block"""
        summary = self._create_run_summary(
            "run-1",
            result=RunResult.FAILED.value,
            failure_reason="Matrix calculation error: division by zero"
        )
        self.run_history.persist_run(summary)
        
        health, reasons = compute_run_health(self.run_history)
        # Should be healthy (single failure, not infrastructure)
        assert health == RunHealth.HEALTHY
    
    def test_many_runs_only_checks_last_5_for_unstable(self):
        """Test: Only last 5 runs are checked for unstable condition"""
        # Create 10 runs: first 5 all failed, last 5 all successful
        for i in range(5):
            summary = self._create_run_summary(f"run-{i}", result=RunResult.FAILED.value)
            self.run_history.persist_run(summary)
        
        for i in range(5, 10):
            summary = self._create_run_summary(f"run-{i}", result=RunResult.SUCCESS.value)
            self.run_history.persist_run(summary)
        
        health, reasons = compute_run_health(self.run_history)
        # Should be healthy (last 5 are all successful)
        assert health == RunHealth.HEALTHY
    
    def test_stopped_runs_not_counted_as_failures(self):
        """Test: Stopped runs are not counted as failures"""
        # Create 5 stopped runs
        for i in range(5):
            summary = self._create_run_summary(f"run-{i}", result=RunResult.STOPPED.value)
            self.run_history.persist_run(summary)
        
        health, reasons = compute_run_health(self.run_history)
        assert health == RunHealth.HEALTHY  # Stopped is not a failure

