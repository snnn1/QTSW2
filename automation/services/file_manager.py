"""
FileManager - Atomic file operations and locking

Single Responsibility: Manage file operations safely with locking
"""

import time
import shutil
from pathlib import Path
from typing import List, Set, Optional
import logging


class FileManager:
    """
    Manages file operations with:
    - Atomic operations via lock files
    - Safe file deletion with verification
    - Directory scanning with locking
    """
    
    def __init__(self, logger: logging.Logger, lock_timeout: int = 300):
        self.logger = logger
        self.lock_timeout = lock_timeout  # 5 minutes default
    
    def acquire_lock(self, lock_file: Path) -> bool:
        """
        Acquire a lock file. Returns True if lock acquired, False if already locked.
        
        Args:
            lock_file: Path to lock file
        
        Returns:
            True if lock acquired, False if already locked
        """
        if lock_file.exists():
            # Check if lock is stale (older than timeout)
            lock_age = time.time() - lock_file.stat().st_mtime
            if lock_age > self.lock_timeout:
                self.logger.warning(f"Removing stale lock file: {lock_file} (age: {lock_age}s)")
                lock_file.unlink()
            else:
                return False
        
        # Create lock file
        try:
            lock_file.touch()
            return True
        except Exception as e:
            self.logger.error(f"Failed to acquire lock: {e}")
            return False
    
    def release_lock(self, lock_file: Path) -> None:
        """Release a lock file."""
        try:
            if lock_file.exists():
                lock_file.unlink()
        except Exception as e:
            self.logger.warning(f"Failed to release lock: {e}")
    
    def scan_directory(
        self,
        directory: Path,
        pattern: str,
        lock_file: Optional[Path] = None
    ) -> List[Path]:
        """
        Scan directory for files matching pattern, with optional locking.
        
        Args:
            directory: Directory to scan
            pattern: Glob pattern (e.g., "*.csv")
            lock_file: Optional lock file to acquire before scanning
        
        Returns:
            List of matching files
        """
        if lock_file and not self.acquire_lock(lock_file):
            self.logger.warning(f"Could not acquire lock for scanning {directory}")
            return []
        
        try:
            files = list(directory.glob(pattern))
            # Filter to only files in root directory (exclude subdirectories)
            files = [f for f in files if f.parent == directory]
            return sorted(files, key=lambda x: x.stat().st_mtime, reverse=True)
        finally:
            if lock_file:
                self.release_lock(lock_file)
    
    def delete_files(
        self,
        files: List[Path],
        verify_pattern: Optional[str] = None,
        dry_run: bool = False
    ) -> int:
        """
        Safely delete files with optional pattern verification.
        
        Args:
            files: List of files to delete
            verify_pattern: Optional pattern to verify before deletion (e.g., "CL_*.parquet")
            dry_run: If True, don't actually delete, just log
        
        Returns:
            Number of files deleted
        """
        deleted = 0
        for file_path in files:
            try:
                # Verify pattern if provided
                if verify_pattern and not file_path.match(verify_pattern):
                    self.logger.warning(f"Skipping deletion - pattern mismatch: {file_path}")
                    continue
                
                if dry_run:
                    self.logger.info(f"[DRY RUN] Would delete: {file_path}")
                else:
                    file_path.unlink()
                    self.logger.info(f"Deleted: {file_path.name}")
                deleted += 1
            except Exception as e:
                self.logger.error(f"Failed to delete {file_path}: {e}")
        
        return deleted
    
    def move_files_atomic(
        self,
        source_files: List[Path],
        target_directory: Path,
        lock_file: Optional[Path] = None
    ) -> int:
        """
        Atomically move files to target directory using lock.
        
        Args:
            source_files: Files to move
            target_directory: Destination directory
            lock_file: Optional lock file
        
        Returns:
            Number of files moved
        """
        if lock_file and not self.acquire_lock(lock_file):
            self.logger.warning("Could not acquire lock for atomic move")
            return 0
        
        try:
            target_directory.mkdir(parents=True, exist_ok=True)
            moved = 0
            for source_file in source_files:
                try:
                    target = target_directory / source_file.name
                    shutil.move(str(source_file), str(target))
                    moved += 1
                except Exception as e:
                    self.logger.error(f"Failed to move {source_file}: {e}")
            return moved
        finally:
            if lock_file:
                self.release_lock(lock_file)



