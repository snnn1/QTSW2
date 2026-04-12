"""
Instrument Logic Module
Handles instrument-specific calculations and logic
"""

from typing import Dict, Tuple, Literal
from dataclasses import dataclass
import pandas as pd

Instrument = Literal["ES","NQ","YM","CL","NG","GC","RTY","MES","MNQ","MYM","MCL","MNG","MGC","M2K","MINUTEDATAEXPORT"]

@dataclass
class InstrumentConfig:
    """Configuration for a specific instrument"""
    tick_size: float
    target_ladder: Tuple[float, ...]
    is_micro: bool
    base_instrument: str
    scaling_factor: float

class InstrumentManager:
    """Handles instrument-specific calculations and logic"""
    
    def __init__(self):
        """Initialize instrument manager"""
        self.instruments: Dict[Instrument, InstrumentConfig] = {
            # Regular futures
            "ES": InstrumentConfig(0.25, (10,15,20,25,30,35,40), False, "ES", 1.0),
            "NQ": InstrumentConfig(0.25, (50,75,100,125,150,175,200), False, "NQ", 1.0),
            "YM": InstrumentConfig(1.0, (100,150,200,250,300,350,400), False, "YM", 1.0),
            "CL": InstrumentConfig(0.01, (0.5,0.75,1.0,1.25,1.5,1.75,2.0), False, "CL", 1.0),
            "NG": InstrumentConfig(0.001, (0.05,0.075,0.10,0.125,0.15,0.175,0.20), False, "NG", 1.0),
            "GC": InstrumentConfig(0.1, (5,7.5,10,12.5,15,17.5,20), False, "GC", 1.0),
            "RTY": InstrumentConfig(0.10, (10,), False, "RTY", 1.0),
            
            # Micro futures (1/10th size)
            "MES": InstrumentConfig(0.25, (10,15,20,25,30,35,40), True, "ES", 0.1),
            "MNQ": InstrumentConfig(0.25, (50,75,100,125,150,175,200), True, "NQ", 0.1),
            "MYM": InstrumentConfig(1.0, (100,150,200,250,300,350,400), True, "YM", 0.1),
            "MCL": InstrumentConfig(0.01, (0.5,0.75,1.0,1.25,1.5,1.75,2.0), True, "CL", 0.1),
            "MNG": InstrumentConfig(0.001, (0.05,0.075,0.10,0.125,0.15,0.175,0.20), True, "NG", 0.1),
            "MGC": InstrumentConfig(0.1, (5,7.5,10,12.5,15,17.5,20), True, "GC", 0.1),
            # Micro Russell (1/10th size). Standard symbol: M2K.
            # Tick size matches RTY; scaling_factor controls profit scaling.
            "M2K": InstrumentConfig(0.10, (10,), True, "RTY", 0.1),
            
            # Data export format
            "MINUTEDATAEXPORT": InstrumentConfig(0.25, (10,15,20,25,30,35,40), False, "ES", 1.0),
        }
    
    def get_tick_size(self, instrument: Instrument) -> float:
        """Get tick size for an instrument"""
        return self.instruments[instrument].tick_size
    
    def get_target_ladder(self, instrument: Instrument) -> Tuple[float, ...]:
        """Get target ladder for an instrument"""
        return self.instruments[instrument].target_ladder
    
    def is_micro_future(self, instrument: Instrument) -> bool:
        """Check if instrument is a micro future"""
        return self.instruments[instrument].is_micro
    
    def get_base_instrument(self, instrument: Instrument) -> str:
        """Get the base instrument for micro futures"""
        return self.instruments[instrument].base_instrument
    
    def get_scaling_factor(self, instrument: Instrument) -> float:
        """Get scaling factor for profit calculations"""
        return self.instruments[instrument].scaling_factor
    
    def scale_profit(self, instrument: Instrument, profit: float) -> float:
        """
        Scale profit for instrument type
        
        Args:
            instrument: Trading instrument
            profit: Raw profit value
            
        Returns:
            Scaled profit value
        """
        if self.is_micro_future(instrument):
            return profit * self.get_scaling_factor(instrument)
        else:
            return profit
    
    def unscale_profit(self, instrument: Instrument, scaled_profit: float) -> float:
        """
        Unscale profit to get raw value
        
        Args:
            instrument: Trading instrument
            scaled_profit: Scaled profit value
            
        Returns:
            Raw profit value
        """
        if self.is_micro_future(instrument):
            return scaled_profit / self.get_scaling_factor(instrument)
        else:
            return scaled_profit
    
    def get_display_profit(self, instrument: Instrument, actual_profit: float) -> float:
        """
        Get display profit (shows as if it were the base instrument)
        
        Args:
            instrument: Trading instrument
            actual_profit: Actual profit value
            
        Returns:
            Display profit value
        """
        if self.is_micro_future(instrument):
            # For micro futures, show profit as if it were the base instrument
            return actual_profit / self.get_scaling_factor(instrument)
        else:
            return actual_profit
    
    def calculate_breakout_levels(self, instrument: Instrument, range_high: float, 
                                range_low: float) -> Tuple[float, float]:
        """
        Calculate breakout levels for an instrument (always 1 tick above/below)
        
        Args:
            instrument: Trading instrument
            range_high: Range high price
            range_low: Range low price
            
        Returns:
            Tuple of (long_breakout, short_breakout) levels
        """
        tick_size = self.get_tick_size(instrument)
        
        # Calculate breakout levels (always 1 tick above/below range)
        long_breakout = range_high + tick_size
        short_breakout = range_low - tick_size
        
        return long_breakout, short_breakout
    
    def get_target_profit(self, instrument: Instrument, target_value: float) -> float:
        """
        Get the actual profit for a target value
        
        Args:
            instrument: Trading instrument
            target_value: Target value from ladder
            
        Returns:
            Actual profit value
        """
        return self.scale_profit(instrument, target_value)
    
    def get_base_target(self, instrument: Instrument) -> float:
        """Get base target for an instrument (first level)"""
        return self.get_target_ladder(instrument)[0]
    
    def get_stream_tag(self, instrument: str, session: str) -> str:
        """
        Generate stream tag for instrument and session
        
        Args:
            instrument: Trading instrument
            session: Trading session (S1 or S2)
            
        Returns:
            Stream tag string
        """
        return f"{instrument.upper()}{'1' if session=='S1' else '2'}"
    
    def get_level_class(self, level_idx: int) -> int:
        """
        Get level class for a level index
        
        Args:
            level_idx: Level index (0-based)
            
        Returns:
            Level class (1, 2, or 3)
        """
        if level_idx == 0: return 1
        if level_idx == 1: return 2
        return 3
    
    def validate_instrument(self, instrument: str) -> bool:
        """
        Validate if instrument is supported
        
        Args:
            instrument: Instrument to validate
            
        Returns:
            True if instrument is valid
        """
        return instrument in self.instruments
    
    def get_instrument_info(self, instrument: Instrument) -> Dict:
        """
        Get comprehensive information about an instrument
        
        Args:
            instrument: Trading instrument
            
        Returns:
            Dictionary with instrument information
        """
        config = self.instruments[instrument]
        return {
            "instrument": instrument,
            "tick_size": config.tick_size,
            "target_ladder": config.target_ladder,
            "is_micro": config.is_micro,
            "base_instrument": config.base_instrument,
            "scaling_factor": config.scaling_factor,
            "base_target": self.get_base_target(instrument)
        }
    
    def get_all_instruments(self) -> list[Instrument]:
        """Get list of all supported instruments"""
        return list(self.instruments.keys())
    
    def get_micro_futures(self) -> list[Instrument]:
        """Get list of all micro futures"""
        return [inst for inst, config in self.instruments.items() if config.is_micro]
    
    def get_regular_futures(self) -> list[Instrument]:
        """Get list of all regular futures"""
        return [inst for inst, config in self.instruments.items() if not config.is_micro]
    
    def get_decimal_places(self, instrument: Instrument) -> int:
        """
        Get number of decimal places for rounding based on tick size
        
        Args:
            instrument: Trading instrument
            
        Returns:
            Number of decimal places to round to
        """
        tick_size = self.get_tick_size(instrument)
        
        # Convert tick size to decimal places by counting decimal digits
        # 0.001 -> 3 places, 0.01 -> 2 places, 0.1 -> 1 place, 0.25 -> 2 places, 1.0 -> 0 places
        if tick_size >= 1.0:
            return 0
        
        # Convert to string and count decimal places
        tick_str = f"{tick_size:.10f}".rstrip('0').rstrip('.')
        if '.' in tick_str:
            return len(tick_str.split('.')[1])
        else:
            return 0
    
    def round_for_instrument(self, instrument: Instrument, value: float) -> float:
        """
        Round a value to the appropriate decimal places for an instrument
        
        Args:
            instrument: Trading instrument
            value: Value to round
            
        Returns:
            Rounded value
        """
        if value is None or (isinstance(value, float) and (value != value)):  # Check for NaN
            return value
        
        decimal_places = self.get_decimal_places(instrument)
        return round(value, decimal_places)
    
    def calculate_profit(self, entry_price: float, exit_price: float,
                        direction: str, result: str, 
                        t1_triggered: bool,
                        target_pts: float, instrument: Instrument,
                        use_display_profit: bool = False) -> float:
        """
        Unified profit calculation method
        
        Calculates profit based on trade result, handling all result types consistently.
        This replaces duplicate profit calculation logic scattered across multiple files.
        
        Args:
            entry_price: Trade entry price
            exit_price: Trade exit price
            direction: Trade direction ("Long" or "Short")
            result: Trade result ("Win", "BE", "Loss", "TIME", or numeric)
            t1_triggered: Whether T1 trigger was activated
            target_pts: Target points from ladder (unscaled)
            instrument: Trading instrument
            use_display_profit: If True, returns display profit (ES equivalent for micro-futures)
                              If False, returns actual profit (scaled for micro-futures)
            
        Returns:
            Calculated profit value
        """
        # Handle Win result
        if result == "Win" or (isinstance(result, (int, float)) and result > 0):
            # Win trades: Use target profit (scaled for micro-futures)
            actual_profit = self.scale_profit(instrument, target_pts)
            if use_display_profit:
                return self.get_display_profit(instrument, actual_profit)
            return actual_profit
        
        # Handle Break-Even result
        elif result == "BE":
            # Break-even trades: T1 triggered = 1 tick loss, otherwise 0
            if t1_triggered:
                tick_size = self.get_tick_size(instrument)
                actual_profit = -tick_size
            else:
                actual_profit = 0.0
            
            if use_display_profit:
                return self.get_display_profit(instrument, actual_profit)
            return actual_profit
        
        # Handle OPEN result (trade still open)
        elif result == "OPEN":
            # Open trades: Calculate current PnL based on current price
            if direction == "Long":
                pnl_pts = exit_price - entry_price
            else:
                pnl_pts = entry_price - exit_price
            
            # Scale for micro-futures
            actual_profit = self.scale_profit(instrument, pnl_pts)
            
            if use_display_profit:
                return self.get_display_profit(instrument, actual_profit)
            return actual_profit
        
        # Handle TIME expiry result
        elif result == "TIME":
            # Time expiry trades: Calculate actual PnL based on exit price
            # TIME only used when trade is closed (exit_time != NaT)
            if direction == "Long":
                pnl_pts = exit_price - entry_price
            else:
                pnl_pts = entry_price - exit_price
            
            # Scale for micro-futures
            actual_profit = self.scale_profit(instrument, pnl_pts)
            
            if use_display_profit:
                return self.get_display_profit(instrument, actual_profit)
            return actual_profit
        
        # Handle Loss result
        elif result == "Loss":
            # Loss trades: Calculate actual PnL
            if direction == "Long":
                pnl_pts = exit_price - entry_price
            else:
                pnl_pts = entry_price - exit_price
            
            # Scale for micro-futures
            actual_profit = self.scale_profit(instrument, pnl_pts)
            
            # For display purposes, show ES equivalent for micro-futures losses
            if use_display_profit and actual_profit < 0:
                return self.get_display_profit(instrument, actual_profit)
            
            return actual_profit
        
        # Default: return 0 for unknown result types
        return 0.0