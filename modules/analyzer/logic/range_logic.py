"""
Range Detection Logic Module
Handles calculation of trading ranges for different time slots and sessions
"""

import pandas as pd
import pytz
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
            # Defensive timezone normalization (translator handles timezone, but ensure consistency)
            # Normalize data timestamps to America/Chicago to handle different timezone object instances
            # Pandas treats different timezone objects as different even if they represent the same timezone
            chicago_tz = pytz.timezone("America/Chicago")
            if df["timestamp"].dt.tz is not None and len(df) > 0:
                # Check if timezone needs normalization by comparing string representation
                first_data_ts = df["timestamp"].iloc[0]
                first_data_tz = first_data_ts.tz if hasattr(first_data_ts, 'tz') else None
                # Normalize if timezone is different (compare by string to handle object instance differences)
                if first_data_tz is not None and str(first_data_tz) != "America/Chicago":
                    # Always normalize to Chicago timezone to ensure timezone objects match
                    df = df.copy()
                    df["timestamp"] = df["timestamp"].dt.tz_convert(chicago_tz)
                elif first_data_tz is not None:
                    # Timezone is already America/Chicago, but might be different object instance
                    # Convert anyway to ensure we use the same timezone object
                    df = df.copy()
                    df["timestamp"] = df["timestamp"].dt.tz_convert(chicago_tz)
            
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
                # Always use America/Chicago timezone object for slot timestamps
                tz = chicago_tz
                
                # Create timestamps directly in Chicago time (slot times are Chicago time)
                # Use direct timestamp creation to ensure timezone is preserved correctly
                date_str = date.strftime("%Y-%m-%d")
                start_ts = pd.Timestamp(f"{date_str} {start_h:02d}:{start_m:02d}:00", tz=tz)
                end_ts = pd.Timestamp(f"{date_str} {end_h:02d}:{end_m:02d}:00", tz=tz)
            else:
                # Data is naive - create naive timestamps
                # WARNING: If data is naive, analyzer will treat timestamps as UTC
                # This can cause incorrect time slot matching
                date_str = date.strftime("%Y-%m-%d")
                start_ts = pd.Timestamp(f"{date_str} {start_h:02d}:{start_m:02d}:00")
                end_ts = pd.Timestamp(f"{date_str} {end_h:02d}:{end_m:02d}:00")
            
            # Filter data for range period
            # Defensive check: Ensure timezone objects match (translator handles timezone, but verify consistency)
            if df["timestamp"].dt.tz is not None and start_ts.tz is not None:
                # Both are timezone-aware - ensure they use same timezone object
                # Convert data timestamps to match filter timezone if needed
                first_data_ts = df["timestamp"].iloc[0]
                first_data_tz = first_data_ts.tz if hasattr(first_data_ts, 'tz') else None
                if first_data_tz is not None and first_data_tz != start_ts.tz:
                    # Timezone objects don't match - convert data to filter timezone
                    df_filter = df.copy()
                    df_filter["timestamp"] = df_filter["timestamp"].dt.tz_convert(start_ts.tz)
                    range_data = df_filter[(df_filter["timestamp"] >= start_ts) & (df_filter["timestamp"] < end_ts)]
                else:
                    # Timezone objects match - can filter directly
                    range_data = df[(df["timestamp"] >= start_ts) & (df["timestamp"] < end_ts)]
            elif df["timestamp"].dt.tz is not None and start_ts.tz is None:
                # Data is timezone-aware but filter is naive - convert filter to data timezone
                start_ts = start_ts.tz_localize(df["timestamp"].dt.tz.iloc[0])
                end_ts = end_ts.tz_localize(df["timestamp"].dt.tz.iloc[0])
                range_data = df[(df["timestamp"] >= start_ts) & (df["timestamp"] < end_ts)]
            elif df["timestamp"].dt.tz is None and start_ts.tz is not None:
                # Data is naive but filter is timezone-aware - remove timezone from filter
                start_ts = start_ts.tz_localize(None)
                end_ts = end_ts.tz_localize(None)
                range_data = df[(df["timestamp"] >= start_ts) & (df["timestamp"] < end_ts)]
            else:
                # Both are naive
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
            
            # Debug: Print range information to stderr so it shows immediately
            import sys
            try:
                print(f"  RANGE CALCULATION:", file=sys.stderr, flush=True)
                print(f"     Period: {start_ts.strftime('%Y-%m-%d %H:%M')} to {end_ts.strftime('%Y-%m-%d %H:%M')} Chicago", file=sys.stderr, flush=True)
                print(f"     Bars: {len(range_data)}", file=sys.stderr, flush=True)
                print(f"     High: {range_high:.2f}", file=sys.stderr, flush=True)
                print(f"     Low: {range_low:.2f}", file=sys.stderr, flush=True)
                print(f"     Size: {range_size:.2f}", file=sys.stderr, flush=True)
                print(f"     Freeze Close: {freeze_close:.2f}", file=sys.stderr, flush=True)
            except Exception as e:
                import sys
                print(f"  Error printing range info: {e}", file=sys.stderr, flush=True)
            
            return RangeResult(
                range_high=range_high,
                range_low=range_low,
                range_size=range_size,
                freeze_close=freeze_close,
                start_time=start_ts,
                end_time=end_ts
            )
            
        except Exception as e:
            import sys
            import traceback
            print(f"Error calculating range for {date} {time_label}: {e}", file=sys.stderr, flush=True)
            print(f"Traceback: {traceback.format_exc()}", file=sys.stderr, flush=True)
            return None
    
    def calculate_breakout_levels(self, range_result: RangeResult, 
                                tick_size: float = 0.25) -> Tuple[float, float]:
        """
        Calculate breakout levels based on range (always 1 tick above/below)
        
        Args:
            range_result: Range calculation result
            tick_size: Tick size for the instrument
            
        Returns:
            Tuple of (long_breakout, short_breakout) levels
        """
        if not range_result:
            return None, None
        
        # Calculate breakout levels (always 1 tick above/below range)
        brk_long = range_result.range_high + tick_size
        brk_short = range_result.range_low - tick_size
        
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
        
        total_dates = len(unique_dates)
        import sys
        def log(msg):
            print(msg, file=sys.stderr, flush=True)
        
        log(f"\n{'='*70}")
        log(f"RANGE DETECTION: Processing {total_dates} unique dates")
        log(f"{'='*70}")
        if total_dates > 0:
            log(f"Date range: {min(unique_dates)} to {max(unique_dates)}")
        log(f"Enabled sessions: {rp.enabled_sessions}")
        log(f"Enabled slots: {rp.enabled_slots}")
        log(f"Trade days: {rp.trade_days} (0=Mon, 4=Fri)")
        log(f"{'='*70}\n")
        
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
            weekday = date.weekday()
            day_names = {0: 'Mon', 1: 'Tue', 2: 'Wed', 3: 'Thu', 4: 'Fri', 5: 'Sat', 6: 'Sun'}
            day_name = day_names.get(weekday, 'Unknown')
            
            if weekday not in rp.trade_days:
                dates_skipped_not_trading_day += 1
                if dates_skipped_not_trading_day <= 5:  # Log first few skips
                    import sys
                    print(f"Skipping {date_str} ({day_name}) - not a trading day", file=sys.stderr, flush=True)
                continue
            
            dates_processed += 1
            
            # Show progress every 100 dates or on first/last
            if dates_processed == 1 or dates_processed % 100 == 0 or dates_processed == total_dates:
                import sys
                print(f"Processing date {dates_processed}/{total_dates}: {date_str} ({day_name})", file=sys.stderr, flush=True)
            
            # Log that we're starting to process this date
            if dates_processed <= 3:
                import sys
                print(f"  Processing {len(rp.enabled_sessions)} sessions for {date_str}...", file=sys.stderr, flush=True)
            
            # Process each enabled session
            slots_processed_this_date = 0
            for sess in rp.enabled_sessions:
                if dates_processed <= 3:
                    import sys
                    print(f"  Processing session {sess}...", file=sys.stderr, flush=True)
                
                if sess not in self.slot_ends:
                    import sys
                    print(f"  WARNING: Session {sess} not in slot_ends", file=sys.stderr, flush=True)
                    continue
                
                # Process each time slot in the session
                for time_label in self.slot_ends[sess]:
                    # Check if this slot is enabled
                    if sess in rp.enabled_slots and rp.enabled_slots[sess] and time_label not in rp.enabled_slots[sess]:
                        if dates_processed <= 3:
                            import sys
                            print(f"    Skipping slot {time_label} (not enabled)", file=sys.stderr, flush=True)
                        continue
                    
                    # Calculate range for this slot
                    if dates_processed <= 3:  # Log first few range calculations
                        import sys
                        print(f"    Calculating range for {date_str} {sess} {time_label}...", file=sys.stderr, flush=True)
                    
                    try:
                        range_result = self.calculate_range(df, date, time_label, sess)
                    except Exception as e:
                        import sys
                        import traceback
                        print(f"    ERROR in calculate_range: {e}", file=sys.stderr, flush=True)
                        print(f"    Traceback: {traceback.format_exc()}", file=sys.stderr, flush=True)
                        range_result = None
                    
                    if dates_processed <= 3:
                        import sys
                        if range_result:
                            print(f"      Range found: {range_result.range_low:.2f}-{range_result.range_high:.2f}", file=sys.stderr, flush=True)
                            slots_processed_this_date += 1
                        else:
                            print(f"      No range found (calculate_range returned None)", file=sys.stderr, flush=True)
                    
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
        
        # Always print summary, not just in debug mode
        import sys
        def log(msg):
            print(msg, file=sys.stderr, flush=True)
        
        log(f"\n{'='*70}")
        log(f"RANGE BUILDING SUMMARY")
        log(f"{'='*70}")
        log(f"Total unique dates in data: {len(unique_dates)}")
        log(f"Dates skipped (not trading day): {dates_skipped_not_trading_day}")
        log(f"Dates processed: {dates_processed}")
        log(f"Total ranges found: {len(ranges)}")
        
        if len(ranges) == 0:
            log(f"\nWARNING: No ranges found!")
            log(f"  Possible reasons:")
            log(f"    - Data doesn't contain dates matching trade_days: {rp.trade_days}")
            log(f"    - Data doesn't contain times matching enabled sessions: {rp.enabled_sessions}")
            log(f"    - Data doesn't contain times matching enabled slots: {rp.enabled_slots}")
            log(f"    - calculate_range() returning None for all slots")
            log(f"    - Data timezone issues (data should be in America/Chicago)")
        
        if debug and len(ranges) > 0:
            # Show sample of first few ranges
            log(f"\nSample ranges (first 5):")
            for i, r in enumerate(ranges[:5]):
                log(f"  {i+1}. Date: {r.date.date()}, Session: {r.session}, Slot: {r.end_label}, Range: {r.range_low:.2f}-{r.range_high:.2f}")
        
        log(f"{'='*70}\n")
        
        return ranges
