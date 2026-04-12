"""
Pipeline orchestration - refactored for single responsibility
"""

from .state import PipelineState, StageResult

# Note: PipelineOrchestrator is in modules/orchestrator/service.py
# This module's orchestrator.py is empty/unused

__all__ = ['PipelineState', 'StageResult']


















