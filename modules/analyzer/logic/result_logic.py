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
    
    def __init__(self, instrument_manager=None):
        """
        Initialize result processor
        
        Args:
            instrument_manager: InstrumentManager instance for profit calculations
        """
        self.instrument_manager = instrument_manager
    
    def _validate_time_label(self, time_label: str) -> bool:
        """
        Validate time label format (should be HH:MM)
        
        Args:
            time_label: Time label to validate
            
        Returns:
            True if valid, False otherwise
        """
        if not time_label or not isinstance(time_label, str):
            return False
        
        if ":" not in time_label:
            return False
        
        try:
            parts = time_label.split(":")
            if len(parts) != 2:
                return False
            hour = int(parts[0])
            minute = int(parts[1])
            if hour < 0 or hour > 23 or minute < 0 or minute > 59:
                return False
            return True
        except (ValueError, IndexError):
            return False
    
    def create_result_row(self, date: pd.Timestamp, time_label: str, target: float, 
                         peak: float, direction: str, result: str, range_sz: float, 
                         stream: str, instrument: str, session: str, profit: float,
                         entry_time: Optional[pd.Timestamp] = None,
                         exit_time: Optional[pd.Timestamp] = None,
                         entry_price: Optional[float] = None,
                         exit_price: Optional[float] = None,
                         stop_loss: Optional[float] = None) -> Dict[str, object]:
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
            entry_time: Entry timestamp (optional)
            exit_time: Exit timestamp (optional)
            entry_price: Entry price (optional)
            exit_price: Exit price (optional)
            stop_loss: Stop loss price (optional) - will be converted to points distance
            
        Returns:
            Dictionary representing the result row
        """
        from breakout_core.utils import hhmm_to_sort_int
        
        # Validate time_label format
        if not self._validate_time_label(time_label):
            # Try to derive time_label from entry_time if available
            if entry_time is not None and isinstance(entry_time, pd.Timestamp):
                # Try to find the closest slot time based on entry_time
                # Get slot ends from config to find the closest match
                from logic.config_logic import ConfigManager
                config_manager = ConfigManager()
                
                # Determine session from entry_time hour
                # S1 slots: 07:30, 08:00, 09:00
                # S2 slots: 09:30, 10:00, 10:30, 11:00
                entry_hour = entry_time.hour
                entry_minute = entry_time.minute
                
                # Try to match to closest slot
                if session == "S1":
                    slot_ends = config_manager.get_slot_ends("S1")
                elif session == "S2":
                    slot_ends = config_manager.get_slot_ends("S2")
                else:
                    slot_ends = []
                
                # Find the slot that entry_time falls into
                # Entry should be at or after the slot end time
                # Find the most recent slot that entry_time is >= to
                best_match = None
                best_slot_minutes = -1
                for slot_time in slot_ends:
                    try:
                        slot_hour, slot_minute = map(int, slot_time.split(":"))
                        slot_minutes = slot_hour * 60 + slot_minute
                        entry_minutes = entry_hour * 60 + entry_minute
                        # Find the most recent slot that entry_time is >= to
                        # This means entry happened at or after this slot ended
                        if entry_minutes >= slot_minutes and slot_minutes > best_slot_minutes:
                            best_slot_minutes = slot_minutes
                            best_match = slot_time
                    except (ValueError, IndexError):
                        continue
                
                # If no slot found where entry >= slot, use the first slot as fallback
                if best_match is None and slot_ends:
                    best_match = slot_ends[0]
                
                if best_match and self._validate_time_label(best_match):
                    time_label = best_match
                else:
                    # Use empty string as last resort
                    time_label = ""
            else:
                # Use empty string as fallback for invalid time_label
                time_label = ""
        
        # Calculate _sortTime AFTER time_label is finalized (may have been derived from entry_time)
        sort_time_value = hhmm_to_sort_int(time_label) if time_label and self._validate_time_label(time_label) else 0
        
        # Format EntryTime and ExitTime as DD/MM/YY HH:MM strings
        entry_time_str = ""
        if entry_time is not None:
            if isinstance(entry_time, pd.Timestamp):
                entry_time_str = entry_time.strftime("%d/%m/%y %H:%M")
            else:
                entry_time_str = str(entry_time)
        
        exit_time_str = ""
        if exit_time is not None and not pd.isna(exit_time):
            if isinstance(exit_time, pd.Timestamp):
                exit_time_str = exit_time.strftime("%d/%m/%y %H:%M")
            else:
                exit_time_str = str(exit_time)
        
        # Calculate stop loss distance in points (not price)
        stop_loss_points = 0.0
        if stop_loss is not None and entry_price is not None and direction != "NA":
            if direction == "Long":
                stop_loss_points = entry_price - stop_loss
            elif direction == "Short":
                stop_loss_points = stop_loss - entry_price
        
        row = {
            "Date": date.date().isoformat(),
            "Time": time_label,
            "EntryTime": entry_time_str,
            "ExitTime": exit_time_str,
            "EntryPrice": entry_price if entry_price is not None else 0.0,
            "ExitPrice": exit_price if exit_price is not None else 0.0,
            "StopLoss": stop_loss_points,  # Store as points distance, not price
            "Target": target,
            "Peak": peak,
            "Direction": direction,
            "Result": result,
            "Range": range_sz,
            "Stream": stream,
            "Instrument": instrument.upper(),
            "Session": session,
            "Profit": profit,
            "_sortTime": sort_time_value
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
        # Define base columns (always present) - EntryTime and ExitTime after Time to match expected schema
        base_columns = ["Date","Time","EntryTime","ExitTime","EntryPrice","ExitPrice","StopLoss","Target","Peak","Direction","Result","Range","Stream","Instrument","Session","Profit","_sortTime"]
        
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
            
            # Remove empty "SL" column if "StopLoss" exists (legacy column cleanup)
            if 'SL' in out.columns and 'StopLoss' in out.columns:
                out = out.drop(columns=['SL'])
        else:
            # Empty DataFrame
            out = pd.DataFrame(columns=base_columns)
        
        if out.empty:
            # Drop _sortTime if present
            cols_to_drop = ["_sortTime"]
            return out.drop(columns=[c for c in cols_to_drop if c in out.columns])
        
        # Convert Date to datetime for proper sorting
        out["Date"] = pd.to_datetime(out["Date"])
        
        # Filter out rows with empty/invalid Time values before deduplication
        if "Time" in out.columns:
            # Keep rows where Time is not empty and is valid format
            valid_time_mask = out["Time"].apply(lambda x: self._validate_time_label(str(x)) if pd.notna(x) and str(x) else False)
            invalid_count = (~valid_time_mask).sum()
            if invalid_count > 0:
                # Log warning but don't fail - just filter out invalid rows
                import warnings
                warnings.warn(f"Filtered out {invalid_count} rows with invalid Time values", UserWarning)
            out = out[valid_time_mask].copy()
        
        # Define result ranking
        rank = {"Win":5,"BE":4,"Loss":3,"TIME":2}
        out["_rank"] = out["Result"].map(rank).fillna(-1)
        
        # Ensure _sortTime is valid for all rows before sorting
        if "_sortTime" in out.columns and "Time" in out.columns:
            # Recalculate _sortTime for any invalid values
            invalid_sort_mask = (out["_sortTime"] == 0) | out["_sortTime"].isna()
            if invalid_sort_mask.any():
                out.loc[invalid_sort_mask, "_sortTime"] = out.loc[invalid_sort_mask, "Time"].apply(
                    lambda x: hhmm_to_sort_int(str(x)) if self._validate_time_label(str(x)) else 0
                )
        
        # Sort and deduplicate - earliest first by Date and Time
        out = (out.sort_values(["Date","_sortTime","Target","_rank","Peak"], ascending=[True,True,True,False,False])
                  .drop_duplicates(subset=["Date","Time","Target","Direction","Session","Instrument"], keep="first"))
        
        # Round numeric columns based on instrument tick size
        # Apply rounding per row since different instruments may be in same DataFrame
        if self.instrument_manager and not out.empty and "Instrument" in out.columns:
            # Price columns (EntryPrice, ExitPrice) - round based on instrument tick size
            price_columns = ["EntryPrice", "ExitPrice"]
            for col in price_columns:
                if col in out.columns:
                    out[col] = out.apply(
                        lambda row: self.instrument_manager.round_for_instrument(
                            row["Instrument"].upper(), 
                            row[col]
                        ) if pd.notna(row[col]) else row[col],
                        axis=1
                    )
            
            # Point columns (StopLoss, Target, Peak, Profit) - round based on instrument tick size
            point_columns = ["StopLoss", "Target", "Peak", "Profit"]
            for col in point_columns:
                if col in out.columns:
                    out[col] = out.apply(
                        lambda row: self.instrument_manager.round_for_instrument(
                            row["Instrument"].upper(), 
                            row[col]
                        ) if pd.notna(row[col]) else row[col],
                        axis=1
                    )
        
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
                                  target_pts: float, instrument: str,
                                  use_display_profit: bool = False) -> float:
        """
        Calculate profit based on result and trigger
        
        This method now delegates to InstrumentManager.calculate_profit() for unified logic.
        
        Args:
            entry_price: Trade entry price
            exit_price: Trade exit price
            direction: Trade direction
            result: Trade result classification
            t1_triggered: Whether T1 trigger was activated
            target_pts: Target points (unscaled)
            instrument: Trading instrument
            use_display_profit: If True, returns display profit (ES equivalent for micro-futures)
            
        Returns:
            Calculated profit
        """
        if not self.instrument_manager:
            raise ValueError("InstrumentManager required for profit calculation")
        
        # Use unified profit calculation from InstrumentManager
        # Note: ResultProcessor uses BE=0 logic, not BE=-tick_size
        # So we need to handle BE differently here
        if result == "BE":
            # ResultProcessor expects BE to return 0, not -tick_size
            return 0.0
        
        return self.instrument_manager.calculate_profit(
            entry_price=entry_price,
            exit_price=exit_price,
            direction=direction,
            result=result,
            t1_triggered=t1_triggered,
            target_pts=target_pts,
            instrument=instrument.upper(),
            use_display_profit=use_display_profit
        )