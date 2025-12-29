"""
Checkpoint manager for Master Matrix sequencer state.

This module handles saving and loading sequencer state snapshots for
deterministic restoration during rolling window updates.
"""

import json
import logging
import uuid
from pathlib import Path
from typing import Dict, Optional, List
from datetime import datetime
import tempfile
import shutil

from .logging_config import setup_matrix_logger

logger = setup_matrix_logger(__name__, console=True, level=logging.INFO)


class CheckpointManager:
    """Manages sequencer state checkpoints for deterministic restoration."""
    
    def __init__(self, checkpoint_dir: str = "data/matrix/state/checkpoints"):
        """
        Initialize checkpoint manager.
        
        Args:
            checkpoint_dir: Directory to store checkpoint files
        """
        self.checkpoint_dir = Path(checkpoint_dir)
        self.checkpoint_dir.mkdir(parents=True, exist_ok=True)
    
    def create_checkpoint(
        self,
        checkpoint_date: str,
        stream_states: Dict[str, Dict],
        checkpoint_id: Optional[str] = None
    ) -> str:
        """
        Create a checkpoint snapshot of sequencer state.
        
        Args:
            checkpoint_date: The last date included in this checkpoint (YYYY-MM-DD)
            stream_states: Dictionary mapping stream_id to state dict:
                {
                    "current_time": str,
                    "current_session": str,
                    "time_slot_histories": {
                        "07:30": [1, -1, 0, ...],
                        ...
                    }
                }
            checkpoint_id: Optional checkpoint ID (generated if not provided)
            
        Returns:
            checkpoint_id (str)
        """
        if checkpoint_id is None:
            checkpoint_id = str(uuid.uuid4())
        
        checkpoint_data = {
            "checkpoint_id": checkpoint_id,
            "checkpoint_date": checkpoint_date,
            "created_at": datetime.now().isoformat(),
            "streams": stream_states
        }
        
        # Atomic write: write to temp file, then rename
        checkpoint_file = self.checkpoint_dir / f"checkpoint_{checkpoint_id}.json"
        temp_file = checkpoint_file.with_suffix('.tmp')
        
        try:
            # Write to temp file
            with open(temp_file, 'w', encoding='utf-8') as f:
                json.dump(checkpoint_data, f, indent=2, ensure_ascii=False)
            
            # Atomic rename
            temp_file.replace(checkpoint_file)
            
            logger.info(f"Created checkpoint {checkpoint_id} for date {checkpoint_date}")
            return checkpoint_id
        except Exception as e:
            logger.error(f"Failed to create checkpoint: {e}")
            # Clean up temp file on error
            if temp_file.exists():
                temp_file.unlink()
            raise
    
    def load_checkpoint(self, checkpoint_id: Optional[str] = None) -> Optional[Dict]:
        """
        Load a checkpoint by ID, or load the latest checkpoint if ID not provided.
        
        Args:
            checkpoint_id: Optional checkpoint ID. If None, loads latest checkpoint.
            
        Returns:
            Checkpoint data dict, or None if not found
        """
        if checkpoint_id:
            checkpoint_file = self.checkpoint_dir / f"checkpoint_{checkpoint_id}.json"
            if not checkpoint_file.exists():
                logger.warning(f"Checkpoint {checkpoint_id} not found")
                return None
            
            try:
                with open(checkpoint_file, 'r', encoding='utf-8') as f:
                    return json.load(f)
            except Exception as e:
                logger.error(f"Failed to load checkpoint {checkpoint_id}: {e}")
                return None
        else:
            # Load latest checkpoint
            return self.load_latest_checkpoint()
    
    def load_latest_checkpoint(self) -> Optional[Dict]:
        """
        Load the most recent checkpoint by checkpoint_date.
        
        Returns:
            Latest checkpoint data dict, or None if no checkpoints exist
        """
        checkpoint_files = list(self.checkpoint_dir.glob("checkpoint_*.json"))
        
        if not checkpoint_files:
            logger.debug("No checkpoints found")
            return None
        
        # Try to find latest by checkpoint_date
        latest_checkpoint = None
        latest_date = None
        
        for checkpoint_file in checkpoint_files:
            try:
                with open(checkpoint_file, 'r', encoding='utf-8') as f:
                    checkpoint_data = json.load(f)
                    checkpoint_date = checkpoint_data.get('checkpoint_date')
                    
                    if checkpoint_date:
                        if latest_date is None or checkpoint_date > latest_date:
                            latest_date = checkpoint_date
                            latest_checkpoint = checkpoint_data
            except Exception as e:
                logger.warning(f"Failed to read checkpoint file {checkpoint_file.name}: {e}")
                continue
        
        if latest_checkpoint:
            logger.info(f"Loaded latest checkpoint {latest_checkpoint.get('checkpoint_id')} for date {latest_date}")
        else:
            logger.warning("No valid checkpoints found")
        
        return latest_checkpoint
    
    def list_checkpoints(self) -> List[Dict]:
        """
        List all available checkpoints.
        
        Returns:
            List of checkpoint metadata dicts (checkpoint_id, checkpoint_date, created_at)
        """
        checkpoint_files = list(self.checkpoint_dir.glob("checkpoint_*.json"))
        checkpoints = []
        
        for checkpoint_file in checkpoint_files:
            try:
                with open(checkpoint_file, 'r', encoding='utf-8') as f:
                    checkpoint_data = json.load(f)
                    checkpoints.append({
                        'checkpoint_id': checkpoint_data.get('checkpoint_id'),
                        'checkpoint_date': checkpoint_data.get('checkpoint_date'),
                        'created_at': checkpoint_data.get('created_at'),
                        'file': checkpoint_file.name
                    })
            except Exception as e:
                logger.warning(f"Failed to read checkpoint file {checkpoint_file.name}: {e}")
                continue
        
        # Sort by checkpoint_date descending
        checkpoints.sort(key=lambda x: x.get('checkpoint_date', ''), reverse=True)
        return checkpoints
    
    def get_max_processed_date(self) -> Optional[str]:
        """
        Get the maximum processed date from the latest checkpoint.
        
        Returns:
            Latest checkpoint_date (YYYY-MM-DD), or None if no checkpoints exist
        """
        latest = self.load_latest_checkpoint()
        if latest:
            return latest.get('checkpoint_date')
        return None


__all__ = ['CheckpointManager']

