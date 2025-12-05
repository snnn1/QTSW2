"""
Orchestrator Configuration - Timeouts, retry policy, paths, stages definition
"""

from pathlib import Path
from typing import Dict, Optional
from dataclasses import dataclass
import os


@dataclass
class StageConfig:
    """Configuration for a pipeline stage"""
    name: str
    order: int
    timeout_sec: int
    max_retries: int = 3
    retry_delay_sec: int = 5


@dataclass
class OrchestratorConfig:
    """Configuration for the pipeline orchestrator"""
    
    # Base paths
    qtsw2_root: Path
    event_logs_dir: Path
    lock_dir: Path
    
    # Stage configurations
    stages: Dict[str, StageConfig]
    
    # Timeouts
    heartbeat_timeout_sec: int = 300  # 5 minutes
    lock_timeout_sec: int = 3600  # 1 hour
    watchdog_interval_sec: int = 30
    
    # Retry policy
    default_max_retries: int = 3
    retry_backoff_multiplier: float = 2.0
    
    # Event bus
    event_buffer_size: int = 1000
    
    @classmethod
    def from_environment(cls, qtsw2_root: Optional[Path] = None) -> 'OrchestratorConfig':
        """Create config from environment"""
        if qtsw2_root is None:
            # Try to get from environment or use default
            qtsw2_root_str = os.getenv("QTSW2_ROOT", r"C:\Users\jakej\QTSW2")
            qtsw2_root = Path(qtsw2_root_str)
        
        event_logs_dir = qtsw2_root / "automation" / "logs" / "events"
        lock_dir = qtsw2_root / "automation" / "logs"
        
        # Define stages
        stages = {
            "translator": StageConfig(
                name="translator",
                order=1,
                timeout_sec=3600,  # 1 hour
                max_retries=2,
                retry_delay_sec=10
            ),
            "analyzer": StageConfig(
                name="analyzer",
                order=2,
                timeout_sec=21600,  # 6 hours
                max_retries=1,
                retry_delay_sec=30
            ),
            "merger": StageConfig(
                name="merger",
                order=3,
                timeout_sec=1800,  # 30 minutes
                max_retries=2,
                retry_delay_sec=5
            )
        }
        
        return cls(
            qtsw2_root=qtsw2_root,
            event_logs_dir=event_logs_dir,
            lock_dir=lock_dir,
            stages=stages
        )

