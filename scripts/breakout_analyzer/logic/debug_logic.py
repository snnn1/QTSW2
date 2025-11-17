"""
Debug Logic Module
Handles debug output and logging for the trading system
"""

import pandas as pd
from typing import Optional, Dict, Any
from dataclasses import dataclass
import time

@dataclass
class DebugInfo:
    """Debug information for a trade"""
    trade_id: str
    entry_time: pd.Timestamp
    entry_price: float
    direction: str
    stop_loss: float
    target_pts: float
    mfe_end_time: Optional[pd.Timestamp]
    mfe_bars_count: int
    peak_value: float
    peak_time: pd.Timestamp
    stop_loss_hit: bool
    stop_loss_hit_time: Optional[pd.Timestamp]

class DebugManager:
    """Handles debug output and logging"""
    
    def __init__(self, debug_enabled: bool = False):
        """
        Initialize debug manager
        
        Args:
            debug_enabled: Whether debug output is enabled
        """
        self.debug_enabled = debug_enabled
        self.start_time = None
        self.debug_info = []
        
        if self.debug_enabled:
            try:
                print(f"\n{'='*80}")
                print(f"DEBUG MODE ENABLED")
                print(f"{'='*80}")
                print(f"Detailed trade analysis and execution logging will be displayed")
                print(f"This includes entry/exit details, MFE tracking, and stop loss analysis")
                print(f"Performance timing information will also be shown")
                print(f"{'='*80}\n")
            except UnicodeEncodeError:
                # Fallback for Windows console that doesn't support Unicode
                print(f"\n{'='*80}")
                print(f"DEBUG MODE ENABLED")
                print(f"{'='*80}")
                print(f"Detailed trade analysis and execution logging will be displayed")
                print(f"This includes entry/exit details, MFE tracking, and stop loss analysis")
                print(f"Performance timing information will also be shown")
                print(f"{'='*80}\n")
        
    def start_timer(self):
        """Start performance timer"""
        if self.debug_enabled:
            self.start_time = time.time()
    
    def end_timer(self) -> float:
        """End performance timer and return elapsed time"""
        if self.debug_enabled and self.start_time:
            elapsed = time.time() - self.start_time
            self.start_time = None
            return elapsed
        return 0.0
    
    def print_trade_debug(self, trade_info: DebugInfo):
        """Print debug information for a trade"""
        if not self.debug_enabled:
            return
            
        try:
            print(f"\n{'='*60}")
            print(f"TRADE EXECUTION SUMMARY")
            print(f"{'='*60}")
            print(f"Trade ID: {trade_info.trade_id}")
            print(f"Entry: {trade_info.entry_time} at ${trade_info.entry_price:.2f} ({trade_info.direction})")
            print(f"Stop Loss: ${trade_info.stop_loss:.2f}")
            print(f"Target: {trade_info.target_pts} points")
            print(f"")
            print(f"MFE ANALYSIS:")
            print(f"   - MFE Period: {trade_info.entry_time} -> {trade_info.mfe_end_time}")
            print(f"   - Bars Analyzed: {trade_info.mfe_bars_count}")
            print(f"   - Peak Movement: {trade_info.peak_value:.2f} points at {trade_info.peak_time}")
            print(f"")
            print(f"STOP LOSS STATUS:")
            if trade_info.stop_loss_hit:
                print(f"   [X] HIT at {trade_info.stop_loss_hit_time}")
            else:
                print(f"   [OK] NOT HIT")
            print(f"{'='*60}")
        except UnicodeEncodeError:
            # Fallback without emojis
            print(f"\n{'='*60}")
            print(f"TRADE EXECUTION SUMMARY")
            print(f"{'='*60}")
            print(f"Trade ID: {trade_info.trade_id}")
            print(f"Entry: {trade_info.entry_time} at ${trade_info.entry_price:.2f} ({trade_info.direction})")
            print(f"Stop Loss: ${trade_info.stop_loss:.2f}")
            print(f"Target: {trade_info.target_pts} points")
            print(f"")
            print(f"MFE ANALYSIS:")
            print(f"   - MFE Period: {trade_info.entry_time} -> {trade_info.mfe_end_time}")
            print(f"   - Bars Analyzed: {trade_info.mfe_bars_count}")
            print(f"   - Peak Movement: {trade_info.peak_value:.2f} points at {trade_info.peak_time}")
            print(f"")
            print(f"STOP LOSS STATUS:")
            if trade_info.stop_loss_hit:
                print(f"   [X] HIT at {trade_info.stop_loss_hit_time}")
            else:
                print(f"   [OK] NOT HIT")
            print(f"{'='*60}")
    
    def print_mfe_debug(self, entry_time: pd.Timestamp, mfe_end_time: pd.Timestamp, 
                       mfe_bars_count: int, first_bar: Optional[pd.Timestamp] = None,
                       last_bar: Optional[pd.Timestamp] = None):
        """Print MFE debug information"""
        if not self.debug_enabled:
            return
            
        try:
            print(f"\n{'='*50}")
            print(f"MFE ANALYSIS SETUP")
            print(f"{'='*50}")
            print(f"Entry Time: {entry_time}")
            print(f"MFE End Time: {mfe_end_time}")
            print(f"Total Bars for MFE: {mfe_bars_count}")
            if first_bar:
                print(f"First Bar: {first_bar}")
            if last_bar:
                print(f"Last Bar: {last_bar}")
            print(f"{'='*50}")
        except UnicodeEncodeError:
            # Fallback without emojis
            print(f"\n{'='*50}")
            print(f"MFE ANALYSIS SETUP")
            print(f"{'='*50}")
            print(f"Entry Time: {entry_time}")
            print(f"MFE End Time: {mfe_end_time}")
            print(f"Total Bars for MFE: {mfe_bars_count}")
            if first_bar:
                print(f"First Bar: {first_bar}")
            if last_bar:
                print(f"Last Bar: {last_bar}")
            print(f"{'='*50}")
    
    def print_peak_debug(self, entry_price: float, direction: str, stop_loss: float,
                        current_favorable: float, max_favorable: float, 
                        bar_time: pd.Timestamp, peak_price: float):
        """Print peak calculation debug information"""
        if not self.debug_enabled:
            return
            
        direction_arrow = "[UP]" if direction == "Long" else "[DN]"
        try:
            print(f"   {direction_arrow} NEW PEAK: {bar_time} - Movement: {current_favorable:.2f} pts (Max: {max_favorable:.2f}) at ${peak_price:.2f}")
        except UnicodeEncodeError:
            print(f"   {direction_arrow} NEW PEAK: {bar_time} - Movement: {current_favorable:.2f} pts (Max: {max_favorable:.2f}) at ${peak_price:.2f}")
    
    def print_stop_loss_debug(self, bar_time: pd.Timestamp, high: float, low: float,
                             stop_loss: float, direction: str):
        """Print stop loss hit debug information"""
        if not self.debug_enabled:
            return
            
        direction_arrow = "[UP]" if direction == "Long" else "[DN]"
        try:
            print(f"   [STOP] STOP LOSS HIT: {bar_time} - {direction_arrow} {direction}")
            if direction == "Long":
                print(f"      Low: ${low:.2f} <= Stop: ${stop_loss:.2f}")
            else:
                print(f"      High: ${high:.2f} >= Stop: ${stop_loss:.2f}")
        except UnicodeEncodeError:
            print(f"   [STOP] STOP LOSS HIT: {bar_time} - {direction_arrow} {direction}")
            if direction == "Long":
                print(f"      Low: ${low:.2f} <= Stop: ${stop_loss:.2f}")
            else:
                print(f"      High: ${high:.2f} >= Stop: ${stop_loss:.2f}")
    
    def print_performance_summary(self):
        """Print performance summary"""
        if not self.debug_enabled:
            return
            
        try:
            print(f"\n{'='*80}")
            print(f"PERFORMANCE SUMMARY")
            print(f"{'='*80}")
            print(f"Total trades processed: {len(self.debug_info)}")
            if self.debug_info:
                avg_peak = sum(info.peak_value for info in self.debug_info) / len(self.debug_info)
                print(f"Average peak movement: {avg_peak:.2f} points")
                max_peak = max(info.peak_value for info in self.debug_info)
                print(f"Maximum peak movement: {max_peak:.2f} points")
                
                # Calculate stop loss hit rate
                stop_loss_hits = sum(1 for info in self.debug_info if info.stop_loss_hit)
                hit_rate = (stop_loss_hits / len(self.debug_info)) * 100
                print(f"Stop loss hit rate: {hit_rate:.1f}% ({stop_loss_hits}/{len(self.debug_info)})")
            print(f"{'='*80}")
        except UnicodeEncodeError:
            # Fallback without emojis
            print(f"\n{'='*80}")
            print(f"PERFORMANCE SUMMARY")
            print(f"{'='*80}")
            print(f"Total trades processed: {len(self.debug_info)}")
            if self.debug_info:
                avg_peak = sum(info.peak_value for info in self.debug_info) / len(self.debug_info)
                print(f"Average peak movement: {avg_peak:.2f} points")
                max_peak = max(info.peak_value for info in self.debug_info)
                print(f"Maximum peak movement: {max_peak:.2f} points")
                
                # Calculate stop loss hit rate
                stop_loss_hits = sum(1 for info in self.debug_info if info.stop_loss_hit)
                hit_rate = (stop_loss_hits / len(self.debug_info)) * 100
                print(f"Stop loss hit rate: {hit_rate:.1f}% ({stop_loss_hits}/{len(self.debug_info)})")
            print(f"{'='*80}")
    
    def log_trade_info(self, trade_info: DebugInfo):
        """Log trade information for analysis"""
        if self.debug_enabled:
            self.debug_info.append(trade_info)
    
    def get_debug_summary(self) -> Dict[str, Any]:
        """Get debug summary statistics"""
        if not self.debug_enabled or not self.debug_info:
            return {"message": "No debug data available"}
        
        return {
            "total_trades": len(self.debug_info),
            "avg_peak": sum(info.peak_value for info in self.debug_info) / len(self.debug_info),
            "max_peak": max(info.peak_value for info in self.debug_info),
            "min_peak": min(info.peak_value for info in self.debug_info),
            "stop_loss_hit_count": sum(1 for info in self.debug_info if info.stop_loss_hit),
            "stop_loss_hit_rate": sum(1 for info in self.debug_info if info.stop_loss_hit) / len(self.debug_info)
        }
    
    def clear_debug_info(self):
        """Clear stored debug information"""
        self.debug_info.clear()
    
    def is_debug_enabled(self) -> bool:
        """Check if debug is enabled"""
        return self.debug_enabled