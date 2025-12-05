"""
Pipeline State Management - FSM, PipelineState, RunContext
"""

from enum import Enum
from dataclasses import dataclass, field
from typing import Optional, Dict, Any
from datetime import datetime
import asyncio
import logging
import pytz


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
    
    def __post_init__(self):
        if self.updated_at is None:
            self.updated_at = datetime.now(pytz.timezone("America/Chicago"))
    
    def to_dict(self) -> Dict[str, Any]:
        """Convert to dictionary for serialization"""
        return {
            "run_id": self.run_id,
            "state": self.state.value,
            "current_stage": self.current_stage.value if self.current_stage else None,
            "started_at": self.started_at.isoformat() if self.started_at else None,
            "updated_at": self.updated_at.isoformat() if self.updated_at else None,
            "retry_count": self.retry_count,
            "error": self.error,
            "metadata": self.metadata,
        }


class PipelineStateManager:
    """Manages pipeline state with FSM validation"""
    
    def __init__(self, event_bus, logger: logging.Logger):
        self.event_bus = event_bus
        self.logger = logger
        self._lock = asyncio.Lock()
        self._current_context: Optional[RunContext] = None
    
    async def get_state(self) -> Optional[RunContext]:
        """Get current run context"""
        async with self._lock:
            return self._current_context
    
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
                }
            })
            
            self.logger.info(f"State transition: {old_state.value} -> {new_state.value}")
            
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
            
            return self._current_context
    
    async def clear_run(self):
        """Clear current run context"""
        async with self._lock:
            self._current_context = None

