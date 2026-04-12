"""
PipelineState - Immutable state management for pipeline execution

Single Responsibility: Manage pipeline state transitions
"""

from dataclasses import dataclass, field
from enum import Enum
from typing import Dict, Set, Optional
from pathlib import Path


class StageResult(Enum):
    """Result of a pipeline stage"""
    PENDING = "pending"
    RUNNING = "running"
    SUCCESS = "success"
    FAILURE = "failure"
    SKIPPED = "skipped"


@dataclass(frozen=True)
class PipelineState:
    """
    Immutable pipeline state.
    All state transitions create new instances.
    """
    run_id: str
    stage_results: Dict[str, StageResult] = field(default_factory=dict)
    processed_files: Set[Path] = field(default_factory=set)
    raw_files: Set[Path] = field(default_factory=set)
    
    def with_stage_result(self, stage: str, result: StageResult) -> 'PipelineState':
        """Create new state with updated stage result"""
        new_results = {**self.stage_results, stage: result}
        return PipelineState(
            run_id=self.run_id,
            stage_results=new_results,
            processed_files=self.processed_files,
            raw_files=self.raw_files
        )
    
    def with_processed_files(self, files: Set[Path]) -> 'PipelineState':
        """Create new state with updated processed files"""
        return PipelineState(
            run_id=self.run_id,
            stage_results=self.stage_results,
            processed_files=files,
            raw_files=self.raw_files
        )
    
    def with_raw_files(self, files: Set[Path]) -> 'PipelineState':
        """Create new state with updated raw files"""
        return PipelineState(
            run_id=self.run_id,
            stage_results=self.stage_results,
            processed_files=self.processed_files,
            raw_files=files
        )
    
    def is_complete(self) -> bool:
        """Check if pipeline is complete (all stages finished)"""
        required_stages = {"translator", "analyzer", "merger"}
        completed = {s for s, r in self.stage_results.items() 
                    if r in (StageResult.SUCCESS, StageResult.FAILURE, StageResult.SKIPPED)}
        return required_stages.issubset(completed)
    
    def is_successful(self) -> bool:
        """Check if pipeline completed successfully"""
        if not self.is_complete():
            return False
        return all(r == StageResult.SUCCESS for r in self.stage_results.values())
































