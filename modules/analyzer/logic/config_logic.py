"""
Configuration Logic Module
Handles configuration management and parameter validation
"""

from pydantic import BaseModel, Field
from typing import List, Literal, Dict, Tuple, Optional

Instrument = Literal["ES","NQ","YM","CL","NG","GC","RTY","MES","MNQ","MYM","MCL","MNG","MGC"]

class RunParams(BaseModel):
    """Run parameters for the breakout strategy"""
    instrument: Instrument
    enabled_sessions: List[str] = ["S1", "S2"]
    enabled_slots: Dict[str, List[str]] = Field(default_factory=dict)
    trade_days: List[int] = [0, 1, 2, 3, 4]  # Monday=0, Friday=4
    same_bar_priority: str = "STOP_FIRST"  # "STOP_FIRST" or "TP_FIRST"
    write_setup_rows: bool = False
    write_no_trade_rows: bool = True  # Always show NoTrade entries by default
    target: float = 50.0  # Default target for NQ

class ConfigManager:
    """Handles configuration management and validation"""
    
    def __init__(self):
        """Initialize configuration manager"""
        self.slot_ends = {
            "S1": ["07:30","08:00","09:00"],
            "S2": ["09:30","10:00","10:30","11:00"],
        }
        self.slot_start = {"S1":"02:00","S2":"08:00"}
        self.market_close_time = "16:00"  # Market close time in HH:MM format (Chicago time)
        
        self.tick_size: Dict[Instrument, float] = {
            "ES": 0.25, "NQ": 0.25, "YM": 1.0, "CL": 0.01, "NG": 0.001, "GC": 0.1, "RTY": 0.10,
            "MES": 0.25, "MNQ": 0.25, "MYM": 1.0, "MCL": 0.01, "MNG": 0.001, "MGC": 0.1
        }
        
        self.target_ladder: Dict[Instrument, Tuple[float,...]] = {
            "ES": (10,15,20,25,30,35,40),
            "NQ": (50,75,100,125,150,175,200),
            "YM": (100,150,200,250,300,350,400),
            "CL": (0.5,0.75,1.0,1.25,1.5,1.75,2.0),
            "NG": (0.05,0.075,0.10,0.125,0.15,0.175,0.20),
            "GC": (5,7.5,10,12.5,15,17.5,20),
            "RTY": (10,),
            "MES": (10,15,20,25,30,35,40),
            "MNQ": (50,75,100,125,150,175,200),
            "MYM": (100,150,200,250,300,350,400),
            "MCL": (0.5,0.75,1.0,1.25,1.5,1.75,2.0),
            "MNG": (0.05,0.075,0.10,0.125,0.15,0.175,0.20),
            "MGC": (5,7.5,10,12.5,15,17.5,20),
        }
    
    def get_tick_size(self, instrument: Instrument) -> float:
        """Get tick size for an instrument"""
        return self.tick_size[instrument]
    
    def get_target_ladder(self, instrument: Instrument) -> Tuple[float,...]:
        """
        Get target ladder for an instrument
        
        DEPRECATED: Use InstrumentManager.get_target_ladder() instead.
        This method is kept for backward compatibility but should use InstrumentManager.
        """
        import warnings
        warnings.warn(
            "ConfigManager.get_target_ladder() is deprecated. Use InstrumentManager.get_target_ladder() instead.",
            DeprecationWarning,
            stacklevel=2
        )
        return self.target_ladder[instrument]
    
    def get_slot_ends(self, session: str) -> List[str]:
        """Get slot end times for a session"""
        return self.slot_ends.get(session, [])
    
    def get_slot_start(self, session: str) -> str:
        """Get slot start time for a session"""
        return self.slot_start.get(session, "02:00")
    
    def get_target_profit(self, instrument: Instrument, target_value: float) -> float:
        """
        Get the actual profit for a target value, accounting for micro-futures scaling
        
        DEPRECATED: Use InstrumentManager.get_target_profit() instead.
        This method is kept for backward compatibility but delegates to InstrumentManager.
        """
        # This method should not be used directly - InstrumentManager handles this
        # Keeping for backward compatibility but should be removed in future
        import warnings
        warnings.warn(
            "ConfigManager.get_target_profit() is deprecated. Use InstrumentManager.get_target_profit() instead.",
            DeprecationWarning,
            stacklevel=2
        )
        # For now, delegate to InstrumentManager if available, otherwise use old logic
        # This is a temporary bridge until all callers are updated
        if instrument.startswith("M"):  # Micro-futures
            return target_value / 10.0  # Micro-futures are 1/10th the size
        else:
            return target_value  # Regular futures use the target value as-is
    
    def get_base_target(self, instrument: Instrument) -> float:
        """
        Get base target for an instrument (first level)
        
        DEPRECATED: Use InstrumentManager.get_base_target() instead.
        This method is kept for backward compatibility but should use InstrumentManager.
        """
        import warnings
        warnings.warn(
            "ConfigManager.get_base_target() is deprecated. Use InstrumentManager.get_base_target() instead.",
            DeprecationWarning,
            stacklevel=2
        )
        return self.target_ladder[instrument][0]
    
    def get_stream_tag(self, instrument: str, session: str) -> str:
        """Generate stream tag for instrument and session"""
        return f"{instrument.upper()}{'1' if session=='S1' else '2'}"
    
    def validate_run_params(self, params: RunParams) -> bool:
        """Validate run parameters"""
        try:
            # Check if instrument is supported
            if params.instrument not in self.tick_size:
                return False
            
            # Check if sessions are valid
            valid_sessions = set(self.slot_ends.keys())
            if not set(params.enabled_sessions).issubset(valid_sessions):
                return False
            
            # Check if trade days are valid
            if not all(0 <= day <= 4 for day in params.trade_days):
                return False
            
            return True
        except Exception:
            return False
    
    def get_market_close_time(self) -> str:
        """Get market close time in HH:MM format"""
        return self.market_close_time
    
    def get_slot_config(self) -> Dict:
        """Get slot configuration dictionary"""
        return {
            "SLOT_START": self.slot_start,
            "SLOT_ENDS": self.slot_ends
        }
    
    
