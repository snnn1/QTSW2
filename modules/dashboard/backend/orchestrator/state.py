"""
Pipeline State Management - FSM, PipelineState, RunContext
"""

from enum import Enum
from dataclasses import dataclass, field
from typing import Optional, Dict, Any, List
from datetime import datetime
from pathlib import Path
import asyncio
import logging
import json
import pytz
from concurrent.futures import ThreadPoolExecutor


class PipelineStage(Enum):
    """Pipeline stages"""
    TRANSLATOR = "translator"
    ANALYZER = "analyzer"
    MERGER = "merger"


class PipelineRunState(Enum):
    """Pipeline run state (FSM)"""
    IDLE = "idle"
    SCHEDULED = "scheduled"
    STARTING = "starting"
    RUNNING_TRANSLATOR = "running_translator"
    RUNNING_ANALYZER = "running_analyzer"
    RUNNING_MERGER = "running_merger"
    SUCCESS = "success"
    FAILED = "failed"
    RETRYING = "retrying"
    STOPPED = "stopped"


def canonical_state(internal_state: PipelineRunState) -> str:
    """
    Map internal FSM state to canonical pipeline state.
    
    Canonical states (only 4 valid values):
    - idle: No active run or run completed
    - running: Pipeline is actively executing
    - stopped: Pipeline was explicitly stopped
    - error: Pipeline failed with error
    
    No inferred states, no starting_up, ready, waiting, initializing.
    """
    if internal_state == PipelineRunState.IDLE:
        return "idle"
    elif internal_state == PipelineRunState.STOPPED:
        return "stopped"
    elif internal_state == PipelineRunState.FAILED:
        return "error"
    elif internal_state in {
        PipelineRunState.SCHEDULED,
        PipelineRunState.STARTING,
        PipelineRunState.RUNNING_TRANSLATOR,
        PipelineRunState.RUNNING_ANALYZER,
        PipelineRunState.RUNNING_MERGER,
        PipelineRunState.RETRYING,
    }:
        return "running"
    elif internal_state == PipelineRunState.SUCCESS:
        return "idle"  # Success transitions to idle
    else:
        # Fallback for unknown states
        return "idle"


# Valid state transitions
VALID_TRANSITIONS: Dict[PipelineRunState, set] = {
    PipelineRunState.IDLE: {
        PipelineRunState.SCHEDULED,
        PipelineRunState.STARTING,
    },
    PipelineRunState.SCHEDULED: {
        PipelineRunState.STARTING,
        PipelineRunState.IDLE,  # Can cancel scheduled
    },
    PipelineRunState.STARTING: {
        PipelineRunState.RUNNING_TRANSLATOR,
        PipelineRunState.FAILED,
        PipelineRunState.STOPPED,
    },
    PipelineRunState.RUNNING_TRANSLATOR: {
        PipelineRunState.RUNNING_ANALYZER,
        PipelineRunState.FAILED,
        PipelineRunState.RETRYING,
        PipelineRunState.STOPPED,
    },
    PipelineRunState.RUNNING_ANALYZER: {
        PipelineRunState.RUNNING_MERGER,
        PipelineRunState.FAILED,
        PipelineRunState.RETRYING,
        PipelineRunState.STOPPED,
    },
    PipelineRunState.RUNNING_MERGER: {
        PipelineRunState.SUCCESS,
        PipelineRunState.FAILED,
        PipelineRunState.RETRYING,
        PipelineRunState.STOPPED,
    },
    PipelineRunState.RETRYING: {
        PipelineRunState.RUNNING_TRANSLATOR,
        PipelineRunState.RUNNING_ANALYZER,
        PipelineRunState.RUNNING_MERGER,
        PipelineRunState.FAILED,
        PipelineRunState.STOPPED,
    },
    PipelineRunState.SUCCESS: {
        PipelineRunState.IDLE,
    },
    PipelineRunState.FAILED: {
        PipelineRunState.IDLE,
        PipelineRunState.RETRYING,
    },
    PipelineRunState.STOPPED: {
        PipelineRunState.IDLE,
    },
}


@dataclass
class RunContext:
    """Context for a pipeline run"""
    run_id: str
    state: PipelineRunState = PipelineRunState.IDLE
    current_stage: Optional[PipelineStage] = None
    started_at: Optional[datetime] = None
    updated_at: Optional[datetime] = None
    retry_count: int = 0
    error: Optional[str] = None
    metadata: Dict[str, Any] = field(default_factory=dict)
    # Track stages executed for run history
    stages_executed: List[str] = field(default_factory=list)
    stages_failed: List[str] = field(default_factory=list)
    
    def __post_init__(self):
        if self.updated_at is None:
            self.updated_at = datetime.now(pytz.timezone("America/Chicago"))
    
    def to_dict(self) -> Dict[str, Any]:
        """Convert to dictionary for serialization"""
        # Extract health from metadata (derived, not persisted)
        run_health = self.metadata.get("run_health")
        run_health_reasons = self.metadata.get("run_health_reasons", [])
        
        return {
            "run_id": self.run_id,
            "state": self.state.value,
            "current_stage": self.current_stage.value if self.current_stage else None,
            "started_at": self.started_at.isoformat() if self.started_at else None,
            "updated_at": self.updated_at.isoformat() if self.updated_at else None,
            "retry_count": self.retry_count,
            "error": self.error,
            "metadata": self.metadata,
            "stages_executed": self.stages_executed,
            "stages_failed": self.stages_failed,
            # Health fields (derived, read-only)
            "run_health": run_health,
            "run_health_reasons": run_health_reasons,
        }


class PipelineStateManager:
    """Manages pipeline state with FSM validation and disk persistence"""
    
    def __init__(self, event_bus, logger: logging.Logger, state_file: Optional[Path] = None):
        self.event_bus = event_bus
        self.logger = logger
        self._lock = asyncio.Lock()
        self._current_context: Optional[RunContext] = None
        self._executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="state-save")
        
        # State persistence - state lives on disk, not just in memory
        if state_file is None:
            # Default location: automation/logs/orchestrator_state.json
            qtsw2_root = Path(__file__).parent.parent.parent.parent
            state_file = qtsw2_root / "automation" / "logs" / "orchestrator_state.json"
        self.state_file = Path(state_file)
        self.state_file.parent.mkdir(parents=True, exist_ok=True)
        
        # Load persisted state on startup
        self._load_state()
    
    def _load_state(self):
        """Load state from disk"""
        try:
            if self.state_file.exists():
                with open(self.state_file, 'r') as f:
                    data = json.load(f)
                    if data:
                        # Reconstruct RunContext from dict
                        state_enum = PipelineRunState(data['state'])
                        stage_enum = None
                        if data.get('current_stage'):
                            stage_enum = PipelineStage(data['current_stage'])
                        
                        self._current_context = RunContext(
                            run_id=data['run_id'],
                            state=state_enum,
                            current_stage=stage_enum,
                            started_at=datetime.fromisoformat(data['started_at']) if data.get('started_at') else None,
                            updated_at=datetime.fromisoformat(data['updated_at']) if data.get('updated_at') else None,
                            retry_count=data.get('retry_count', 0),
                            error=data.get('error'),
                            metadata=data.get('metadata', {})
                        )
                        self.logger.info(f"Loaded persisted state: {state_enum.value} (run_id: {data['run_id']})")
                    else:
                        self.logger.info("No persisted state found")
        except Exception as e:
            self.logger.warning(f"Failed to load persisted state: {e}")
            self._current_context = None
    
    def _save_state(self):
        """Save state to disk (synchronous, but should be fast)"""
        try:
            if self._current_context:
                data = self._current_context.to_dict()
            else:
                data = None
            
            # Use atomic write to prevent corruption
            temp_file = self.state_file.with_suffix('.tmp')
            with open(temp_file, 'w') as f:
                json.dump(data, f, indent=2)
            # Atomic rename
            temp_file.replace(self.state_file)
        except Exception as e:
            self.logger.error(f"Failed to save state: {e}")
    
    async def _save_state_async(self):
        """Save state to disk asynchronously (non-blocking)"""
        # Run synchronous save in thread pool to avoid blocking
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(self._executor, self._save_state)
    
    async def get_state(self) -> Optional[RunContext]:
        """
        Get current run context.
        
        This is the single source of truth for state - used by both
        polling endpoints and WebSocket state_change events.
        
        Returns:
            Current RunContext or None if no active run
        """
        async with self._lock:
            return self._current_context
    
    async def get_canonical_state_dict(self) -> Optional[Dict[str, Any]]:
        """
        Get current canonical state as dictionary.
        
        This is the single source of truth for state - used by both
        polling endpoints and WebSocket state_change events.
        
        Returns:
            Canonical state dictionary or None if no active run
        """
        async with self._lock:
            if self._current_context is None:
                return None
            return self._current_context.to_dict()
    
    async def transition(
        self,
        new_state: PipelineRunState,
        *,
        stage: Optional[PipelineStage] = None,
        error: Optional[str] = None,
        metadata: Optional[Dict[str, Any]] = None
    ) -> RunContext:
        """
        Transition to a new state with validation.
        
        Args:
            new_state: Target state
            stage: Current stage (if applicable)
            error: Error message (if applicable)
            metadata: Additional metadata
        
        Returns:
            Updated RunContext
        
        Raises:
            ValueError: If transition is invalid
        """
        async with self._lock:
            if self._current_context is None:
                raise ValueError("No active run context")
            
            old_state = self._current_context.state
            
            # Validate transition
            if new_state not in VALID_TRANSITIONS.get(old_state, set()):
                raise ValueError(
                    f"Invalid transition: {old_state.value} -> {new_state.value}"
                )
            
            # Update context
            self._current_context.state = new_state
            if stage is not None:
                self._current_context.current_stage = stage
            if error is not None:
                self._current_context.error = error
            if metadata is not None:
                self._current_context.metadata.update(metadata)
            self._current_context.updated_at = datetime.now(pytz.timezone("America/Chicago"))
            
            # Emit state change event
            # Skip emitting for STARTING -> RUNNING_* transitions (STARTING is very brief, <1 second)
            # This prevents duplicate-looking events in the UI
            skip_emission = (
                old_state == PipelineRunState.STARTING and
                new_state in {
                    PipelineRunState.RUNNING_TRANSLATOR,
                    PipelineRunState.RUNNING_ANALYZER,
                    PipelineRunState.RUNNING_MERGER
                }
            )
            
            if not skip_emission:
                # Include complete canonical state in state_change event
                # This ensures WebSocket and polling always agree on state
                canonical_state_dict = self._current_context.to_dict()
                await self.event_bus.publish({
                    "run_id": self._current_context.run_id,
                    "stage": "pipeline",
                    "event": "state_change",
                    "timestamp": self._current_context.updated_at.isoformat(),
                    "msg": f"State transition: {old_state.value} -> {new_state.value}",
                    "data": {
                        "old_state": old_state.value,
                        "new_state": new_state.value,
                        "current_stage": stage.value if stage else None,
                        "error": error,
                        # CRITICAL: Include complete canonical state for truthfulness
                        "canonical_state": canonical_state_dict,
                    }
                })
            
            self.logger.info(f"State transition: {old_state.value} -> {new_state.value}")
            
            # Persist state to disk (async, non-blocking)
            asyncio.create_task(self._save_state_async())
            
            return self._current_context
    
    async def create_run(
        self,
        run_id: str,
        metadata: Optional[Dict[str, Any]] = None
    ) -> RunContext:
        """Create a new run context"""
        async with self._lock:
            if self._current_context is not None and self._current_context.state not in {
                PipelineRunState.IDLE,
                PipelineRunState.SUCCESS,
                PipelineRunState.FAILED,
                PipelineRunState.STOPPED,
            }:
                raise ValueError(f"Cannot create new run: current run is {self._current_context.state.value}")
            
            self._current_context = RunContext(
                run_id=run_id,
                state=PipelineRunState.IDLE,
                started_at=datetime.now(pytz.timezone("America/Chicago")),
                metadata=metadata or {}
            )
            
            # Persist state to disk (async, non-blocking)
            asyncio.create_task(self._save_state_async())
            
            return self._current_context
    
    async def clear_run(self):
        """Clear current run context"""
        async with self._lock:
            self._current_context = None
            # Persist cleared state to disk (async, non-blocking)
            await self._save_state_async()

