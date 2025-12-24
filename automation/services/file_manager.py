"""
FileManager - File operations and directory scanning utilities

Single Responsibility: Provide file scanning and management utilities
"""

import logging
from pathlib import Path
from typing import List, Optional


class FileManager:
    """
    Manages file operations and directory scanning.
    
    Currently a minimal implementation - can be extended as needed.
    """
    
    def __init__(self, logger: logging.Logger, lock_timeout: int = 300):
        """
        Initialize FileManager.
        
        Args:
            logger: Logger instance
            lock_timeout: Lock timeout in seconds (currently unused, kept for compatibility)
        """
        self.logger = logger
        self.lock_timeout = lock_timeout
    
    def scan_directory(self, directory: Path, pattern: str = "*.csv", recursive: bool = True) -> List[Path]:
        """
        Scan directory for files matching pattern.
        
        Args:
            directory: Directory to scan
            pattern: File pattern (e.g., "*.csv", "*.parquet")
            recursive: If True, scan subdirectories recursively
        
        Returns:
            List of matching file paths
        """
        if not directory.exists():
            self.logger.warning(f"Directory does not exist: {directory}")
            return []
        
        if recursive:
            files = list(directory.rglob(pattern))
        else:
            files = list(directory.glob(pattern))
        
        return sorted(files)
