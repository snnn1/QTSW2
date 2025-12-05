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
    File-based lock manager to prevent overlapping pipeline runs.
    """
    
    def __init__(
        self,
        lock_dir: Path,
        lock_timeout_sec: int = 3600,
        heartbeat_timeout_sec: int = 300,
        logger: Optional[logging.Logger] = None
    ):
        self.lock_dir = lock_dir
        self.lock_timeout_sec = lock_timeout_sec
        self.heartbeat_timeout_sec = heartbeat_timeout_sec
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
            
            # Create lock file
            lock_data = {
                "run_id": run_id,
                "acquired_at": datetime.now().isoformat(),
                "last_heartbeat": datetime.now().isoformat(),
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
    
    async def heartbeat(self, run_id: str) -> bool:
        """
        Update lock heartbeat.
        
        Args:
            run_id: Run ID updating heartbeat
        
        Returns:
            True if heartbeat updated successfully
        """
        async with self._lock:
            if not self.lock_file.exists():
                return False
            
            try:
                with open(self.lock_file, "r") as f:
                    lock_data = json.load(f)
                
                if lock_data.get("run_id") != run_id:
                    return False
                
                lock_data["last_heartbeat"] = datetime.now().isoformat()
                
                with open(self.lock_file, "w") as f:
                    json.dump(lock_data, f)
                
                return True
            except Exception as e:
                self.logger.error(f"Failed to update heartbeat: {e}")
                return False
    
    async def _is_stale(self) -> bool:
        """Check if lock is stale"""
        try:
            stat = self.lock_file.stat()
            age_sec = time.time() - stat.st_mtime
            
            # Check file modification time
            if age_sec > self.lock_timeout_sec:
                return True
            
            # Check heartbeat in file
            try:
                with open(self.lock_file, "r") as f:
                    lock_data = json.load(f)
                    last_heartbeat_str = lock_data.get("last_heartbeat")
                    if last_heartbeat_str:
                        last_heartbeat = datetime.fromisoformat(last_heartbeat_str)
                        age_sec = (datetime.now() - last_heartbeat).total_seconds()
                        if age_sec > self.heartbeat_timeout_sec:
                            return True
            except Exception:
                pass
            
            return False
        except Exception:
            return True  # If we can't check, assume stale
    
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

