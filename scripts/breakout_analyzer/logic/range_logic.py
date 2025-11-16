"""
Range Detection Logic Module
Handles calculation of trading ranges for different time slots and sessions
"""

import pandas as pd
from typing import Dict, List, Tuple, Optional
from dataclasses import dataclass

@dataclass
class RangeResult:
    """Result of range calculation"""
    range_high: float
    range_low: float
    range_size: float
    freeze_close: float
    start_time: pd.Timestamp
    end_time: pd.Timestamp

@dataclass
class SlotRange:
    """Represents a trading slot range"""
    date: pd.Timestamp
    session: str
    end_label: str
    start_ts: pd.Timestamp
    end_ts: pd.Timestamp
    range_high: float
    range_low: float
    range_size: float
    freeze_close: float

class RangeDetector:
    """Handles range detection for trading slots"""
    
    def __init__(self, slot_config: Dict):
        """
        Initialize range detector with slot configuration
        
        Args:
            slot_config: Dictionary containing SLOT_START and SLOT_ENDS configuration
        """
        self.slot_start = slot_config.get("SLOT_START", {})
        self.slot_ends = slot_config.get("SLOT_ENDS", {})
    
    def calculate_range(self, df: pd.DataFrame, date: pd.Timestamp, 
                       time_label: str, session: str) -> Optional[RangeResult]:
        """
        Calculate range for a specific date and time slot
        
        Args:
            df: DataFrame with OHLCV data
            date: Trading date
            time_label: Time slot (e.g., "08:00")
            session: Session (S1 or S2)
            
        Returns:
            RangeResult object with range details or None if no data
        """
        try:
            # Get session start time
            session_start = self.slot_start.get(session, "02:00")
            start_h, start_m = map(int, session_start.split(":"))
            
            # Get slot end time
            slot_end = self.slot_ends.get(session, [])
            if time_label not in slot_end:
                return None
            
            end_h, end_m = map(int, time_label.split(":"))
            
            # Create date objects (preserve timezone from original data)
            # Slot times are Chicago trading hours (e.g., 07:30 = 7:30 AM Chicago time)
            if df["timestamp"].dt.tz is not None:
                tz = df["timestamp"].dt.tz  # Should be America/Chicago
                # Create timestamps directly in Chicago time (slot times are Chicago time)
                date0 = date.replace(hour=0, minute=0, second=0, microsecond=0)
                start_ts = date0.replace(hour=start_h, minute=start_m, second=0)
                end_ts = date0.replace(hour=end_h, minute=end_m, second=0)
            else:
                # Data is naive - create naive timestamps
                date0 = date.replace(hour=0, minute=0, second=0, microsecond=0)
                start_ts = date0.replace(hour=start_h, minute=start_m, second=0)
                end_ts = date0.replace(hour=end_h, minute=end_m, second=0)
            
            # Filter data for range period
            range_data = df[(df["timestamp"] >= start_ts) & (df["timestamp"] < end_ts)]
            
            # Debug: Log timezone info if empty (to help diagnose timezone issues)
            # Note: debug parameter not available in this function, removed debug check
            
            if range_data.empty:
                return None
            
            # Calculate range values
            range_high = float(range_data["high"].max())
            range_low = float(range_data["low"].min())
            range_size = range_high - range_low
            
            # Freeze close should be the last bar of the range period
            freeze_close = float(range_data.iloc[-1]["close"])
            
            # Debug: Print range information (no emojis to avoid Windows encoding errors)
            # Use simple print statements without emojis to avoid encoding issues
            try:
                print(f"  RANGE CALCULATION:")
                print(f"     Period: {start_ts.strftime('%Y-%m-%d %H:%M')} to {end_ts.strftime('%Y-%m-%d %H:%M')} Chicago")
                print(f"     Bars: {len(range_data)}")
                print(f"     High: {range_high:.2f}")
                print(f"     Low: {range_low:.2f}")
                print(f"     Size: {range_size:.2f}")
                print(f"     Freeze Close: {freeze_close:.2f}")
            except Exception:
                # Silently skip print if there's any encoding issue
                pass
            
            return RangeResult(
                range_high=range_high,
                range_low=range_low,
                range_size=range_size,
                freeze_close=freeze_close,
                start_time=start_ts,
                end_time=end_ts
            )
            
        except Exception as e:
            print(f"Error calculating range for {date} {time_label}: {e}")
            return None
    
    def calculate_breakout_levels(self, range_result: RangeResult, 
                                level_class: float = 1.0, tick_size: float = 0.25) -> Tuple[float, float]:
        """
        Calculate breakout levels based on range
        
        Args:
            range_result: Range calculation result
            level_class: Level scaling factor (1.0 for Level 1, 1.5 for Level 2, etc.)
            tick_size: Tick size for the instrument
            
        Returns:
            Tuple of (long_breakout, short_breakout) levels
        """
        if not range_result:
            return None, None
        
        # Calculate breakout levels
        brk_long = range_result.range_high + (tick_size * level_class)  # Add tick
        brk_short = range_result.range_low - (tick_size * level_class)  # Subtract tick
        
        return brk_long, brk_short
    
    def validate_range(self, range_result: RangeResult, 
                      min_range_size: float = 5.0) -> bool:
        """
        Validate if range meets minimum requirements
        
        Args:
            range_result: Range calculation result
            min_range_size: Minimum range size in points
            
        Returns:
            True if range is valid, False otherwise
        """
        if not range_result:
            return False
        
        return range_result.range_size >= min_range_size
    
    def build_slot_ranges(self, df: pd.DataFrame, rp, debug: bool = False) -> List[SlotRange]:
        """
        Build slot ranges for all enabled sessions and time slots
        
        Args:
            df: DataFrame with OHLCV data
            rp: RunParams object with configuration
            debug: Enable debug output
            
        Returns:
            List of SlotRange objects
        """
        ranges = []
        
        # Get unique dates from the dataframe (data already in correct timezone)
        # For overnight sessions: timestamps from 23:00 onwards belong to the next day's session
        # (e.g., 23:00 Nov 10 is part of Nov 11's session which starts at 02:00 Nov 11)
        # Timestamps from 00:00-01:59 are still part of the current day's overnight session
        if df["timestamp"].dt.tz is not None:
            tz = df["timestamp"].dt.tz
            # Shift timestamps from 23:00 onwards to the next day for date extraction
            # This ensures overnight data (23:00-02:00) is assigned to the correct trading day
            # Note: 00:00-01:59 stay on current day (they're part of current day's overnight session)
            df["date_ct"] = df["timestamp"].apply(
                lambda ts: (ts.date() + pd.Timedelta(days=1) if ts.hour >= 23 else ts.date())
            )
        else:
            # Data is naive - use calendar date
            df["date_ct"] = df["timestamp"].dt.date
        
        unique_dates = df["date_ct"].unique()
        
        if debug:
            print(f"DEBUG: Found {len(unique_dates)} unique dates in data")
            print(f"DEBUG: Trade days filter: {rp.trade_days} (0=Mon, 1=Tue, 2=Wed, 3=Thu, 4=Fri)")
            print(f"DEBUG: Enabled sessions: {rp.enabled_sessions}")
            print(f"DEBUG: Enabled slots: {rp.enabled_slots}")
        
        dates_processed = 0
        dates_skipped_not_trading_day = 0
        
        for date_str in unique_dates:
            # Convert date string back to timestamp (preserve timezone from original data)
            if df["timestamp"].dt.tz is not None:
                # Data has timezone - create timezone-aware timestamp at midnight in that timezone
                tz = df["timestamp"].dt.tz
                # Create timestamp directly in the timezone (date_str is a date object)
                # Convert date object to string first, then create timestamp in timezone
                date = pd.Timestamp(str(date_str), tz=tz)
            else:
                # Data is naive - create naive timestamp
                date = pd.Timestamp(date_str)
            
            # Check if this is a trading day
            if date.weekday() not in rp.trade_days:
                dates_skipped_not_trading_day += 1
                if debug:
                    day_names = {0: 'Mon', 1: 'Tue', 2: 'Wed', 3: 'Thu', 4: 'Fri', 5: 'Sat', 6: 'Sun'}
                    print(f"DEBUG: Skipping {date.date()} ({day_names.get(date.weekday(), 'Unknown')}) - not a trading day")
                continue
            
            dates_processed += 1
            
            # Process each enabled session
            for sess in rp.enabled_sessions:
                if sess not in self.slot_ends:
                    continue
                
                # Process each time slot in the session
                for time_label in self.slot_ends[sess]:
                    # Check if this slot is enabled
                    if sess in rp.enabled_slots and rp.enabled_slots[sess] and time_label not in rp.enabled_slots[sess]:
                        continue
                    
                    # Calculate range for this slot
                    range_result = self.calculate_range(df, date, time_label, sess)
                    
                    if range_result is None:
                        continue
                    
                    # Create SlotRange object
                    slot_range = SlotRange(
                        date=date,
                        session=sess,
                        end_label=time_label,
                        start_ts=range_result.start_time,
                        end_ts=range_result.end_time,
                        range_high=range_result.range_high,
                        range_low=range_result.range_low,
                        range_size=range_result.range_size,
                        freeze_close=range_result.freeze_close
                    )
                    
                    ranges.append(slot_range)
                    
                    # Range calculated
        
        if debug:
            print(f"DEBUG: Range building summary:")
            print(f"  Total unique dates: {len(unique_dates)}")
            print(f"  Dates skipped (not trading day): {dates_skipped_not_trading_day}")
            print(f"  Dates processed: {dates_processed}")
            print(f"  Total ranges found: {len(ranges)}")
            if len(ranges) == 0:
                print(f"  WARNING: No ranges found!")
                print(f"    - Check if data contains dates matching trade_days: {rp.trade_days}")
                print(f"    - Check if data contains times matching sessions: {rp.enabled_sessions}")
                print(f"    - Check if calculate_range() is returning None for all slots")
        
        return ranges
