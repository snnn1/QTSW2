"""
Lock Management - File-based locks to prevent overlapping runs
"""

import asyncio
import json
import logging
from pathlib import Path
from typing import Optional
from datetime import datetime
import time


class LockManager:
    """
    File-based lock manager - Phase 5 Simplified.
    
    Simple lock mechanism:
    - Lock file + timestamp + max runtime
    - No heartbeat updates needed
    - Stale locks detected by file age
    """
    
    def __init__(
        self,
        lock_dir: Path,
        max_runtime_sec: int = 3600,  # 1 hour max runtime
        logger: Optional[logging.Logger] = None
    ):
        self.lock_dir = lock_dir
        self.max_runtime_sec = max_runtime_sec
        self.logger = logger or logging.getLogger(__name__)
        
        self.lock_file = lock_dir / "pipeline.lock"
        self._lock = asyncio.Lock()
        
        # Ensure lock directory exists
        self.lock_dir.mkdir(parents=True, exist_ok=True)
    
    async def acquire(self, run_id: str) -> bool:
        """
        Acquire lock for a run.
        
        Args:
            run_id: Run ID requesting the lock
        
        Returns:
            True if lock acquired, False if already locked
        """
        async with self._lock:
            if self.lock_file.exists():
                # Check if lock is stale
                if await self._is_stale():
                    self.logger.warning(f"Stale lock detected, reclaiming for run {run_id}")
                    # Remove stale lock
                    try:
                        self.lock_file.unlink()
                    except Exception as e:
                        self.logger.error(f"Failed to remove stale lock: {e}")
                        return False
                else:
                    # Lock is active
                    return False
            
            # Phase 5: Create lock file with timestamp (no heartbeat)
            lock_data = {
                "run_id": run_id,
                "acquired_at": datetime.now().isoformat(),
            }
            
            try:
                with open(self.lock_file, "w") as f:
                    json.dump(lock_data, f)
                self.logger.info(f"Lock acquired for run {run_id}")
                return True
            except Exception as e:
                self.logger.error(f"Failed to acquire lock: {e}")
                return False
    
    async def release(self, run_id: str) -> bool:
        """
        Release lock.
        
        Args:
            run_id: Run ID releasing the lock
        
        Returns:
            True if released successfully
        """
        async with self._lock:
            if not self.lock_file.exists():
                return True
            
            # Verify we own the lock
            try:
                with open(self.lock_file, "r") as f:
                    lock_data = json.load(f)
                    if lock_data.get("run_id") != run_id:
                        self.logger.warning(f"Lock owned by different run, cannot release")
                        return False
            except Exception as e:
                self.logger.error(f"Failed to read lock file: {e}")
                return False
            
            # Remove lock file
            try:
                self.lock_file.unlink()
                self.logger.info(f"Lock released for run {run_id}")
                return True
            except Exception as e:
                self.logger.error(f"Failed to release lock: {e}")
                return False
    
    async def _is_stale(self) -> bool:
        """
        Check if lock is stale (Phase 5: simple timestamp check).
        
        Lock is stale if:
        - File age > max_runtime_sec, OR
        - acquired_at timestamp > max_runtime_sec ago
        
        Uses atomic file read to minimize race conditions.
        """
        try:
            if not self.lock_file.exists():
                return False
            
            # Read file first (atomic operation) to minimize race conditions
            # If file is modified between stat and read, we'll catch it on retry
            try:
                with open(self.lock_file, "r") as f:
                    lock_data = json.load(f)
                    acquired_at_str = lock_data.get("acquired_at")
                    
                    # Check acquired_at timestamp (most accurate)
                    if acquired_at_str:
                        acquired_at = datetime.fromisoformat(acquired_at_str)
                        age_sec = (datetime.now() - acquired_at).total_seconds()
                        if age_sec > self.max_runtime_sec:
                            return True
                    
                    # Fallback: Check file modification time
                    # Get stat after read to ensure consistency
                    stat = self.lock_file.stat()
                    file_age_sec = time.time() - stat.st_mtime
                    if file_age_sec > self.max_runtime_sec:
                        return True
            except (json.JSONDecodeError, ValueError, KeyError) as e:
                # Invalid lock file - treat as stale so it can be reclaimed
                self.logger.warning(f"Lock file contains invalid data: {e}. Treating as stale.")
                return True
            except FileNotFoundError:
                # File was deleted between exists() check and open() - not stale
                return False
            
            return False
        except Exception as e:
            # If we can't check (permissions, etc.), assume stale to allow recovery
            self.logger.warning(f"Error checking lock staleness: {e}. Treating as stale.")
            return True
    
    async def is_locked(self) -> bool:
        """Check if lock is currently held"""
        async with self._lock:
            if not self.lock_file.exists():
                return False
            
            if await self._is_stale():
                return False
            
            return True
    
    async def get_lock_info(self) -> Optional[dict]:
        """Get current lock information"""
        async with self._lock:
            if not self.lock_file.exists():
                return None
            
            try:
                with open(self.lock_file, "r") as f:
                    return json.load(f)
            except Exception:
                return None
    
    async def force_clear_all(self) -> bool:
        """
        Force clear all locks regardless of run_id.
        Used for reset operations when system is stuck.
        
        Returns:
            True if cleared successfully
        """
        async with self._lock:
            if not self.lock_file.exists():
                return True
            
            try:
                self.lock_file.unlink()
                self.logger.warning("Force cleared all locks")
                return True
            except Exception as e:
                self.logger.error(f"Failed to force clear locks: {e}")
                return False



