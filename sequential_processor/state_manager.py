#!/usr/bin/env python3
"""
State Manager for Sequential Processor
Handles saving and loading state to maintain 13-peak/trade windows between runs
"""

import json
import os
from datetime import datetime
from pathlib import Path
from typing import Dict, List, Optional, Any
import pandas as pd


class SequencerStateManager:
    """Manages state persistence for sequential processor runs"""
    
    def __init__(self, state_dir: str = "data/sequencer_runs/state"):
        """
        Initialize state manager
        
        Args:
            state_dir: Directory to store state files
        """
        self.state_dir = Path(state_dir)
        self.state_dir.mkdir(parents=True, exist_ok=True)
    
    def get_state_file_path(self, stream: str, instrument: str) -> Path:
        """Get the state file path for a specific stream/instrument combination"""
        # Create a unique identifier based on stream and instrument
        state_filename = f"state_{instrument}_{stream}.json"
        return self.state_dir / state_filename
    
    def save_state(
        self,
        stream: str,
        instrument: str,
        rolling_median_window: List[float],
        median_ladder_window: List[float],
        time_slot_histories: Dict[str, List[float]],
        current_target: float,
        current_time: str,
        rolling_median_target: Optional[float] = None,
        median_ladder_target: Optional[float] = None,
        median_ladder_current_step: Optional[int] = None,
        median_ladder_days_at_level: Optional[int] = None,
        last_processed_date: Optional[str] = None,
        **kwargs
    ) -> bool:
        """
        Save state to file
        
        Args:
            stream: Stream identifier (e.g., "ES1", "NQ1")
            instrument: Instrument name (e.g., "ES", "NQ")
            rolling_median_window: Last 13 peaks for rolling median
            median_ladder_window: Last 13 peaks for median ladder
            time_slot_histories: Dict of time slot -> list of last 13 scores
            current_target: Current target value
            current_time: Current time slot
            rolling_median_target: Current rolling median target (if applicable)
            median_ladder_target: Current median ladder target (if applicable)
            median_ladder_current_step: Current ladder step (if applicable)
            median_ladder_days_at_level: Days at current level (if applicable)
            last_processed_date: Last processed date (ISO format string)
            **kwargs: Additional state to save
        
        Returns:
            True if saved successfully, False otherwise
        """
        try:
            state_file = self.get_state_file_path(stream, instrument)
            
            state = {
                "stream": stream,
                "instrument": instrument,
                "saved_at": datetime.now().isoformat(),
                "rolling_median_window": rolling_median_window,
                "median_ladder_window": median_ladder_window,
                "time_slot_histories": time_slot_histories,
                "current_target": current_target,
                "current_time": current_time,
                "last_processed_date": last_processed_date,
            }
            
            # Add optional fields if provided
            if rolling_median_target is not None:
                state["rolling_median_target"] = rolling_median_target
            if median_ladder_target is not None:
                state["median_ladder_target"] = median_ladder_target
            if median_ladder_current_step is not None:
                state["median_ladder_current_step"] = median_ladder_current_step
            if median_ladder_days_at_level is not None:
                state["median_ladder_days_at_level"] = median_ladder_days_at_level
            
            # Add any additional kwargs
            state.update(kwargs)
            
            with open(state_file, 'w') as f:
                json.dump(state, f, indent=2)
            
            print(f"✅ State saved to: {state_file}")
            return True
            
        except Exception as e:
            print(f"❌ Error saving state: {e}")
            return False
    
    def load_state(
        self,
        stream: str,
        instrument: str
    ) -> Optional[Dict[str, Any]]:
        """
        Load state from file
        
        Args:
            stream: Stream identifier
            instrument: Instrument name
        
        Returns:
            State dictionary if found, None otherwise
        """
        try:
            state_file = self.get_state_file_path(stream, instrument)
            
            if not state_file.exists():
                print(f"ℹ️ No state file found: {state_file}")
                return None
            
            with open(state_file, 'r') as f:
                state = json.load(f)
            
            print(f"✅ State loaded from: {state_file}")
            print(f"   Saved at: {state.get('saved_at', 'Unknown')}")
            print(f"   Last processed date: {state.get('last_processed_date', 'Unknown')}")
            print(f"   Rolling median window: {len(state.get('rolling_median_window', []))} peaks")
            print(f"   Time slot histories: {len(state.get('time_slot_histories', {}))} slots")
            
            return state
            
        except Exception as e:
            print(f"❌ Error loading state: {e}")
            return None
    
    def save_state_from_processor(
        self,
        processor,
        stream: str,
        instrument: str,
        last_processed_date: Optional[datetime] = None
    ) -> bool:
        """
        Save state directly from a SequentialProcessorV2 instance
        
        Args:
            processor: SequentialProcessorV2 instance
            stream: Stream identifier
            instrument: Instrument name
            last_processed_date: Last processed date
        
        Returns:
            True if saved successfully
        """
        # Convert date to string if provided
        date_str = None
        if last_processed_date:
            if isinstance(last_processed_date, datetime):
                date_str = last_processed_date.isoformat()
            else:
                date_str = str(last_processed_date)
        
        return self.save_state(
            stream=stream,
            instrument=instrument,
            rolling_median_window=getattr(processor, 'rolling_median_window', []),
            median_ladder_window=getattr(processor, 'median_ladder_window', []),
            time_slot_histories=getattr(processor, 'time_slot_histories', {}),
            current_target=getattr(processor, 'current_target', processor.BASE_TARGETS.get(instrument, 10)),
            current_time=getattr(processor, 'current_time', '08:00'),
            rolling_median_target=getattr(processor, 'rolling_median_target', None),
            median_ladder_target=getattr(processor, 'median_ladder_target', None),
            median_ladder_current_step=getattr(processor, 'median_ladder_current_step', None),
            median_ladder_days_at_level=getattr(processor, 'median_ladder_days_at_level', None),
            last_processed_date=date_str
        )
    
    def load_state_to_processor(
        self,
        processor,
        stream: str,
        instrument: str
    ) -> bool:
        """
        Load state into a SequentialProcessorV2 instance
        
        Args:
            processor: SequentialProcessorV2 instance
            stream: Stream identifier
            instrument: Instrument name
        
        Returns:
            True if loaded successfully, False otherwise
        """
        state = self.load_state(stream, instrument)
        
        if not state:
            print("ℹ️ No previous state found - starting fresh")
            return False
        
        try:
            # Restore rolling median window
            if 'rolling_median_window' in state:
                processor.rolling_median_window = state['rolling_median_window']
                print(f"   Restored {len(processor.rolling_median_window)} peaks to rolling median window")
            
            # Restore median ladder window
            if 'median_ladder_window' in state:
                processor.median_ladder_window = state['median_ladder_window']
                print(f"   Restored {len(processor.median_ladder_window)} peaks to median ladder window")
            
            # Restore time slot histories
            if 'time_slot_histories' in state:
                for time_slot, history in state['time_slot_histories'].items():
                    if time_slot in processor.time_slot_histories:
                        processor.time_slot_histories[time_slot] = history
                        processor.time_slot_rolling[time_slot] = sum(history)
                print(f"   Restored histories for {len(state['time_slot_histories'])} time slots")
            
            # Restore current target and time
            if 'current_target' in state:
                processor.current_target = state['current_target']
                processor.rolling_median_target = state.get('rolling_median_target', processor.current_target)
                processor.median_ladder_target = state.get('median_ladder_target', processor.current_target)
            
            if 'current_time' in state:
                processor.current_time = state['current_time']
                processor.current_session = processor._get_session_for_time(state['current_time'])
            
            # Restore median ladder state
            if 'median_ladder_current_step' in state:
                processor.median_ladder_current_step = state['median_ladder_current_step']
            if 'median_ladder_days_at_level' in state:
                processor.median_ladder_days_at_level = state['median_ladder_days_at_level']
            
            # Restore last processed date
            if 'last_processed_date' in state and state['last_processed_date']:
                try:
                    processor.last_processed_date = pd.to_datetime(state['last_processed_date'])
                except:
                    pass
            
            print("✅ State restored successfully")
            return True
            
        except Exception as e:
            print(f"❌ Error restoring state: {e}")
            return False
    
    def list_saved_states(self) -> List[Dict[str, Any]]:
        """List all saved state files"""
        states = []
        if not self.state_dir.exists():
            return states
        
        for state_file in self.state_dir.glob("state_*.json"):
            try:
                with open(state_file, 'r') as f:
                    state = json.load(f)
                    state['file_path'] = str(state_file)
                    states.append(state)
            except:
                pass
        
        return states
    
    def delete_state(self, stream: str, instrument: str) -> bool:
        """Delete a state file"""
        state_file = self.get_state_file_path(stream, instrument)
        if state_file.exists():
            state_file.unlink()
            print(f"✅ Deleted state file: {state_file}")
            return True
        return False





