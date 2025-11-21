"""
Result Processing Logic Module
Handles trade result classification and output formatting
"""

import pandas as pd
from typing import Dict, List, Optional
from dataclasses import dataclass

@dataclass
class TradeResult:
    """Represents a trade result"""
    date: pd.Timestamp
    time_label: str
    target: float
    peak: float
    direction: str
    result: str
    range_size: float
    stream: str
    instrument: str
    session: str
    profit: float

class ResultProcessor:
    """Handles trade result processing and formatting"""
    
    def __init__(self):
        """Initialize result processor"""
        pass
    
    def create_result_row(self, date: pd.Timestamp, time_label: str, target: float, 
                         peak: float, direction: str, result: str, range_sz: float, 
                         stream: str, instrument: str, session: str, profit: float) -> Dict[str, object]:
        """
        Create a result row dictionary
        
        Args:
            date: Trading date
            time_label: Time slot label
            target: Target points (trading target)
            peak: Peak (MFE) value
            direction: Trade direction
            result: Trade result
            range_sz: Range size
            stream: Stream identifier
            instrument: Trading instrument
            session: Trading session
            profit: Profit amount
            
        Returns:
            Dictionary representing the result row
        """
        from breakout_core.utils import hhmm_to_sort_int
        
        row = {
            "Date": date.date().isoformat(),
            "Time": time_label,
            "Target": target,
            "Peak": peak,
            "Direction": direction,
            "Result": result,
            "Range": range_sz,
            "Stream": stream,
            "Instrument": instrument.upper(),
            "Session": session,
            "Profit": profit,
            "_sortTime": hhmm_to_sort_int(time_label)
        }
        
        return row
    
    def process_results(self, rows: List[Dict[str, object]]) -> pd.DataFrame:
        """
        Process and format the results DataFrame
        
        Args:
            rows: List of result row dictionaries
            
        Returns:
            Processed DataFrame sorted by Date and Time (earliest first)
        """
        # Define base columns (always present)
        base_columns = ["Date","Time","Target","Peak","Direction","Result","Range","Stream","Instrument","Session","Profit","_sortTime"]
        
        if rows:
            # Create DataFrame
            out = pd.DataFrame(rows)
            
            # Remove any ONR or SCF columns that might exist (from old data or cached rows)
            old_columns_to_remove = ['onr_high', 'onr_low', 'onr', 'onr_q1', 'onr_q2', 'onr_q3', 'onr_bucket', 'ONR Q',
                                     'scf_s1', 'scf_s2', 'SCF',
                                     'prewindow_high_s1', 'prewindow_low_s1', 'prewindow_range_s1',
                                     'session_high_s1', 'session_low_s1', 'session_range_s1',
                                     'prewindow_high_s2', 'prewindow_low_s2', 'prewindow_range_s2',
                                     'session_high_s2', 'session_low_s2', 'session_range_s2']
            for col in old_columns_to_remove:
                if col in out.columns:
                    out = out.drop(columns=[col])
        else:
            # Empty DataFrame
            out = pd.DataFrame(columns=base_columns)
        
        if out.empty:
            # Drop _sortTime if present
            cols_to_drop = ["_sortTime"]
            return out.drop(columns=[c for c in cols_to_drop if c in out.columns])
        
        # Convert Date to datetime for proper sorting
        out["Date"] = pd.to_datetime(out["Date"])
        
        # Define result ranking
        rank = {"Win":5,"BE":4,"Loss":3,"TIME":2}
        out["_rank"] = out["Result"].map(rank).fillna(-1)
        
        # Sort and deduplicate - earliest first by Date and Time
        out = (out.sort_values(["Date","_sortTime","Target","_rank","Peak"], ascending=[True,True,True,False,False])
                  .drop_duplicates(subset=["Date","Time","Target","Direction","Session","Instrument"], keep="first"))
        
        # Final sort by Date and Time (earliest first) and cleanup
        out = out.sort_values(["Date","_sortTime"], ascending=[True,True]).drop(columns=["_sortTime","_rank"]).reset_index(drop=True)
        
        # Convert Date back to string format for display
        out["Date"] = out["Date"].dt.strftime("%Y-%m-%d")
        
        return out
    
    def classify_trade_result(self, exit_reason: str, t1_triggered: bool, 
                            target_hit: bool = False) -> str:
        """
        Classify trade result based on exit reason and trigger
        
        Args:
            exit_reason: Reason for trade exit ("Win", "Loss", "TIME")
            t1_triggered: Whether T1 trigger was activated
            target_hit: Whether target was hit
            
        Returns:
            Result classification ("Win", "BE", "Loss")
        """
        if target_hit or exit_reason == "Win":
            return "Win"
        
        # Handle time expiry first - this should override trigger status
        if exit_reason == "TIME":
            return "TIME"
        
        if t1_triggered:
            return "BE"   # T1 triggered = Break Even
        else:
            return "Loss" # No triggers hit = Loss
    
    def calculate_profit_for_result(self, entry_price: float, exit_price: float,
                                  direction: str, result: str, 
                                  t1_triggered: bool,
                                  target_pts: float, instrument: str) -> float:
        """
        Calculate profit based on result and trigger
        
        Args:
            entry_price: Trade entry price
            exit_price: Trade exit price
            direction: Trade direction
            result: Trade result classification
            t1_triggered: Whether T1 trigger was activated
            target_pts: Target points
            instrument: Trading instrument
            
        Returns:
            Calculated profit
        """
        # Calculate base PnL
        if direction == "Long":
            pnl_pts = exit_price - entry_price
        else:
            pnl_pts = entry_price - exit_price
        
        # Scale PnL for micro-futures
        if instrument.startswith("M"):  # Micro-futures
            pnl_pts = pnl_pts / 10.0
        
        # Adjust profit based on result
        if result == "Win":
            # Win trades: Use target profit
            return target_pts
        elif result == "BE":
            # Break-even trades: 0 profit
            return 0.0
        else:
            # Loss trades: Use actual PnL (already scaled for micro-futures above)
            # For MES display purposes, multiply losses by 10 to show ES equivalent
            if instrument.startswith("M") and pnl_pts < 0:
                return pnl_pts * 10.0
            else:
                return pnl_pts