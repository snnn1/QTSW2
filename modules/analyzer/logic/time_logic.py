"""
Time Logic Module
Handles time and date calculations and conversions
"""

import pandas as pd
from typing import Optional, List
from dataclasses import dataclass
from .config_logic import ConfigManager

@dataclass
class TimeSlot:
    """Represents a time slot"""
    start_time: pd.Timestamp
    end_time: pd.Timestamp
    session: str
    label: str

class TimeManager:
    """Handles time and date calculations"""
    
    def __init__(self):
        """Initialize time manager"""
        self.timezone = "America/Chicago"
        self.slot_starts = {"S1": "02:00", "S2": "08:00"}
        self.slot_ends = {
            "S1": ["07:30", "08:00", "09:00"],
            "S2": ["09:30", "10:00", "10:30", "11:00"]
        }
    
    def ensure_timezone(self, timestamp: pd.Timestamp, timezone: str = None) -> pd.Timestamp:
        """
        Data is already in correct timezone from NinjaTrader export with bar time fix
        Just return the timestamp as-is
        
        Args:
            timestamp: Timestamp to process
            timezone: Target timezone (ignored - data already correct)
            
        Returns:
            Timestamp as-is (already in correct timezone)
        """
        # Data is already in correct timezone from NinjaTrader export
        return timestamp
    
    def create_date_timestamp(self, date: pd.Timestamp, time_str: str) -> pd.Timestamp:
        """
        Create timestamp from date and time string
        
        Args:
            date: Date timestamp
            time_str: Time string in HH:MM format
            
        Returns:
            Combined timestamp
        """
        hour, minute = map(int, time_str.split(":"))
        return date.replace(hour=hour, minute=minute, second=0, microsecond=0)
    
    def get_next_slot_time(self, date: pd.Timestamp, time_label: str) -> pd.Timestamp:
        """
        Get next occurrence of a time slot
        
        Args:
            date: Current date
            time_label: Time slot label (e.g., "08:00")
            
        Returns:
            Next occurrence timestamp
        """
        if date.weekday() == 4:  # Friday
            # Friday trades continue to Monday same slot
            days_ahead = ConfigManager.FRIDAY_TO_MONDAY_DAYS
            next_date = date + pd.Timedelta(days=days_ahead)
        else:
            # Regular day - next day same slot
            next_date = date + pd.Timedelta(days=1)
        
        # Set the time to the same slot next day
        hour, minute = map(int, time_label.split(":"))
        next_time = next_date.replace(hour=hour, minute=minute, second=0, microsecond=0)
        
        return next_time
    
    def get_expiry_time(self, date: pd.Timestamp, time_label: str, session: str) -> pd.Timestamp:
        """
        Calculate trade expiry time
        
        Args:
            date: Trading date (should be timezone-aware, Chicago time)
            time_label: Time slot (e.g., "08:00")
            session: Session (S1 or S2)
            
        Returns:
            Expiry timestamp in Chicago timezone (next trading day same slot, or Monday if Friday)
            For ES2 (11:00 slot), expires at Monday 10:59 (1 minute before 11:00)
        """
        # Ensure date is timezone-aware (Chicago time)
        if date.tz is None:
            # If naive, assume it's Chicago time and localize it
            date = pd.Timestamp(date).tz_localize("America/Chicago")
        elif str(date.tz) != "America/Chicago":
            # If different timezone, convert to Chicago
            date = date.tz_convert("America/Chicago")
        
        # Calculate expiry time (next trading day same slot)
        if date.weekday() == 4:  # Friday
            # Friday trades expire Monday (skip weekend)
            days_ahead = ConfigManager.FRIDAY_TO_MONDAY_DAYS
        else:
            # Regular day trades expire next day
            days_ahead = 1
        
        expiry_date = date + pd.Timedelta(days=days_ahead)
        hour, minute = map(int, time_label.split(":"))
        
        # For TIME exits, expire 1 minute before the slot time
        # e.g., 11:00 slot expires at 10:59
        if minute > 0:
            expiry_minute = minute - 1
        else:
            # If minute is 0, go to previous hour's 59th minute
            expiry_minute = 59
            hour = hour - 1 if hour > 0 else 23
        
        expiry_time = expiry_date.replace(
            hour=hour, 
            minute=expiry_minute, 
            second=59,  # End of the minute
            microsecond=0
        )
        
        # Ensure expiry_time is in Chicago timezone
        if expiry_time.tz is None:
            expiry_time = expiry_time.tz_localize("America/Chicago")
        elif str(expiry_time.tz) != "America/Chicago":
            expiry_time = expiry_time.tz_convert("America/Chicago")
        
        return expiry_time
    
    def create_slot_range(self, date: pd.Timestamp, session: str, end_label: str) -> TimeSlot:
        """
        Create time slot range
        
        Args:
            date: Trading date
            session: Session (S1 or S2)
            end_label: Slot end time
            
        Returns:
            TimeSlot object
        """
        # Get session start time
        start_time_str = self.slot_starts[session]
        
        # Create timestamps (data already has correct bar timing)
        start_time = self.create_date_timestamp(date, start_time_str)
        end_time = self.create_date_timestamp(date, end_label)
        
        return TimeSlot(start_time, end_time, session, end_label)
    
    def filter_data_by_time_range(self, df: pd.DataFrame, start_time: pd.Timestamp, 
                                 end_time: pd.Timestamp) -> pd.DataFrame:
        """
        Filter DataFrame by time range
        
        Args:
            df: DataFrame with timestamp column
            start_time: Start timestamp
            end_time: End timestamp
            
        Returns:
            Filtered DataFrame
        """
        return df[(df["timestamp"] >= start_time) & (df["timestamp"] < end_time)].copy()
    
    def filter_data_after_time(self, df: pd.DataFrame, after_time: pd.Timestamp) -> pd.DataFrame:
        """
        Filter DataFrame to include only data after specified time
        
        Args:
            df: DataFrame with timestamp column
            after_time: Cutoff timestamp
            
        Returns:
            Filtered DataFrame
        """
        return df[df["timestamp"] >= after_time].copy()
    
    def get_trading_days_mask(self, df: pd.DataFrame, trade_days: List[int]) -> pd.Series:
        """
        Get boolean mask for trading days
        
        Args:
            df: DataFrame with timestamp column
            trade_days: List of day-of-week indices (0=Monday, 4=Friday)
            
        Returns:
            Boolean Series for trading days
        """
        return df["timestamp"].dt.weekday.isin(trade_days)
    
    def add_date_column(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Add date column to DataFrame
        
        Args:
            df: DataFrame with timestamp column
            
        Returns:
            DataFrame with added date column
        """
        df = df.copy()
        df["date_ct"] = df["timestamp"].dt.date
        return df
    
    def get_time_label_from_timestamp(self, timestamp: pd.Timestamp) -> str:
        """
        Get time label from timestamp
        
        Args:
            timestamp: Timestamp to convert
            
        Returns:
            Time string in HH:MM format
        """
        return timestamp.strftime("%H:%M")
    
    def is_weekend(self, timestamp: pd.Timestamp) -> bool:
        """
        Check if timestamp is weekend
        
        Args:
            timestamp: Timestamp to check
            
        Returns:
            True if weekend
        """
        return timestamp.weekday() >= 5  # Saturday=5, Sunday=6
    
    def get_next_business_day(self, date: pd.Timestamp) -> pd.Timestamp:
        """
        Get next business day
        
        Args:
            date: Current date
            
        Returns:
            Next business day
        """
        next_day = date + pd.Timedelta(days=1)
        while self.is_weekend(next_day):
            next_day += pd.Timedelta(days=1)
        return next_day
    
    def get_previous_business_day(self, date: pd.Timestamp) -> pd.Timestamp:
        """
        Get previous business day
        
        Args:
            date: Current date
            
        Returns:
            Previous business day
        """
        prev_day = date - pd.Timedelta(days=1)
        while self.is_weekend(prev_day):
            prev_day -= pd.Timedelta(days=1)
        return prev_day
    
    def calculate_time_difference(self, start_time: pd.Timestamp, end_time: pd.Timestamp) -> pd.Timedelta:
        """
        Calculate time difference between two timestamps
        
        Args:
            start_time: Start timestamp
            end_time: End timestamp
            
        Returns:
            Time difference as Timedelta
        """
        return end_time - start_time
    
    def format_timedelta(self, timedelta: pd.Timedelta) -> str:
        """
        Format timedelta as human-readable string
        
        Args:
            timedelta: Time difference
            
        Returns:
            Formatted time string
        """
        total_seconds = int(timedelta.total_seconds())
        hours, remainder = divmod(total_seconds, 3600)
        minutes, seconds = divmod(remainder, 60)
        
        if hours > 0:
            return f"{hours}h {minutes}m"
        elif minutes > 0:
            return f"{minutes}m {seconds}s"
        else:
            return f"{seconds}s"
