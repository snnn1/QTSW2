"""
Data Lifecycle - Manage file deletion with explicit rules

Single Responsibility: Safely delete files based on explicit rules
Never deletes raw files (as per requirements)
"""

import logging
from pathlib import Path
from typing import List, Set, Optional
from dataclasses import dataclass

from automation.config import PipelineConfig


@dataclass
class DeletionRule:
    """Rule for file deletion"""
    stage: str  # Which stage can trigger deletion
    reason: str  # Why files are being deleted
    file_pattern: Optional[str] = None  # Optional pattern verification
    require_success: bool = True  # Only delete if stage succeeded


class DataLifecycleManager:
    """
    Manages file deletion with explicit rules.
    
    Policy:
    - Raw files: NEVER deleted (as per requirements)
    - Processed files: Only deleted after analyzer AND merger succeed
    - Deletion requires explicit file list (never derived via vague patterns)
    - Every deletion is logged with stage, reason, and file path
    """
    
    def __init__(self, config: PipelineConfig, logger: logging.Logger):
        self.config = config
        self.logger = logger
    
    def mark_for_deletion(
        self,
        files: List[Path],
        stage: str,
        reason: str,
        require_success: bool = True
    ) -> List[Path]:
        """
        Mark files as safe to delete (but don't delete yet).
        Returns list of files that are safe to delete.
        
        Args:
            files: Files to consider for deletion
            stage: Stage that processed these files
            reason: Why these files can be deleted
            require_success: Only mark if stage succeeded
        
        Returns:
            List of files safe to delete (empty if requirements not met)
        """
        # Never delete raw files
        safe_files = [f for f in files if self.config.data_raw not in f.parents]
        
        if not safe_files:
            return []
        
        self.logger.info(f"Marked {len(safe_files)} file(s) for deletion: {reason}")
        for file_path in safe_files[:5]:  # Log first 5
            self.logger.info(f"  - {file_path.name}")
        if len(safe_files) > 5:
            self.logger.info(f"  ... and {len(safe_files) - 5} more")
        
        return safe_files
    
    def delete_files(
        self,
        files: List[Path],
        stage: str,
        reason: str,
        verify_pattern: Optional[str] = None
    ) -> int:
        """
        Delete files with explicit verification.
        Logs every deletion with stage, reason, and file path.
        
        Args:
            files: Explicit list of files to delete
            stage: Stage requesting deletion
            reason: Reason for deletion
            verify_pattern: Optional pattern to verify before deletion
        
        Returns:
            Number of files deleted
        """
        deleted = 0
        
        for file_path in files:
            # Never delete raw files
            if self.config.data_raw in file_path.parents:
                self.logger.warning(f"Skipping deletion of raw file: {file_path}")
                continue
            
            # Verify pattern if provided
            if verify_pattern and not file_path.match(verify_pattern):
                self.logger.warning(f"Skipping deletion - pattern mismatch: {file_path}")
                continue
            
            try:
                file_path.unlink()
                self.logger.info(f"Deleted [{stage}] {file_path.name} - {reason}")
                deleted += 1
            except Exception as e:
                self.logger.error(f"Failed to delete {file_path}: {e}")
        
        if deleted > 0:
            self.logger.info(f"Deleted {deleted} file(s) from stage '{stage}' - {reason}")
        
        return deleted
    
    def should_delete_processed_files(
        self,
        translator_succeeded: bool,
        analyzer_succeeded: bool,
        merger_succeeded: bool
    ) -> bool:
        """
        Determine if processed files should be deleted.
        
        Policy: Only delete after analyzer AND merger both succeeded.
        
        Args:
            translator_succeeded: Whether translator stage succeeded
            analyzer_succeeded: Whether analyzer stage succeeded
            merger_succeeded: Whether merger stage succeeded
        
        Returns:
            True if processed files can be deleted
        """
        # Only delete if both analyzer and merger succeeded
        return analyzer_succeeded and merger_succeeded































