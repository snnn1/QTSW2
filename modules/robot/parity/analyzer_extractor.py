"""
Analyzer Canonical Extractor

Extracts canonical comparison records from Analyzer outputs.
One row per (trading_date, instrument, stream, slot_time).
"""

import pandas as pd
import numpy as np
from pathlib import Path
from typing import Optional, List, Dict, Any
import json


class AnalyzerExtractor:
    """Extracts canonical records from Analyzer outputs for parity comparison."""
    
    def __init__(
        self,
        project_root: Path,
        parity_spec: Dict[str, Any],
        start_date: pd.Timestamp,
        end_date: pd.Timestamp,
        instruments: Optional[List[str]] = None,
        mode: str = "load_existing_analyzer",
        data_source_dir: Optional[Path] = None,
        output_dir: Optional[Path] = None
    ):
        self.project_root = project_root
        self.parity_spec = parity_spec
        self.start_date = start_date
        self.end_date = end_date
        self.instruments = instruments
        self.mode = mode
        self.data_source_dir = data_source_dir  # Snapshot directory for parity runs
        self.output_dir = output_dir  # Run-specific output directory
        
        # Get all instruments from parity spec if not specified
        if self.instruments is None:
            self.instruments = list(parity_spec.get("instruments", {}).keys())
        
        # Load instrument configs
        self.instrument_configs = parity_spec.get("instruments", {})
        
        # Safety check: Ensure we're not reading from production data during parity runs
        if self.data_source_dir is not None:
            # Parity run - must use snapshot
            production_dir = project_root / "data" / "translated"
            if str(self.data_source_dir).replace("\\", "/") == str(production_dir).replace("\\", "/"):
                raise ValueError(
                    "SAFETY CHECK FAILED: Parity run attempted to read from production data!\n"
                    f"  Production path: {production_dir}\n"
                    f"  Use snapshot: {project_root / 'data' / 'translated_test'}"
                )
    
    def extract(self) -> Optional[pd.DataFrame]:
        """
        Extract canonical records from Analyzer outputs.
        
        Returns:
            DataFrame with columns:
            - trading_date, instrument, stream, session, slot_time
            - range_high, range_low (optional)
            - brk_long_rounded, brk_short_rounded
            - intended_trade, direction
            - entry_time_chicago, entry_price
            - stop_price, target_price
            - be_stop_price, be_trigger_pts
        """
        if self.mode == "run_analyzer":
            # Run analyzer for the date window using snapshot data
            if self.data_source_dir is None:
                raise ValueError("data_source_dir required for run_analyzer mode")
            
            # Run analyzer with snapshot as input and run-specific output
            analyzer_output = self._run_analyzer_with_snapshot()
            if analyzer_output is None:
                return None
            
            # Load the analyzer output
            analyzer_dfs = [pd.read_parquet(analyzer_output)]
        else:
            # Load existing analyzer outputs
            analyzer_dfs = []
            
            # Search for analyzer output files in run-specific output directory first
            if self.output_dir is not None:
                run_output_dir = self.output_dir / "analyzer_output"
                if run_output_dir.exists():
                    for parquet_file in run_output_dir.rglob("*.parquet"):
                        try:
                            df = pd.read_parquet(parquet_file)
                            if self._is_relevant(df):
                                analyzer_dfs.append(df)
                        except Exception as e:
                            print(f"Warning: Could not read {parquet_file}: {e}")
                            continue
            
            # Fallback to manual_analyzer_runs and analyzer_temp (for backward compatibility)
            search_dirs = [
                self.project_root / "data" / "manual_analyzer_runs",
                self.project_root / "data" / "analyzer_temp"
            ]
        
        for search_dir in search_dirs:
            if not search_dir.exists():
                continue
            
            # Find all parquet files in subdirectories
            for parquet_file in search_dir.rglob("*.parquet"):
                try:
                    df = pd.read_parquet(parquet_file)
                    if self._is_relevant(df):
                        analyzer_dfs.append(df)
                except Exception as e:
                    print(f"Warning: Could not read {parquet_file}: {e}")
                    continue
        
        if not analyzer_dfs:
            return None
        
        # Combine all dataframes
        combined_df = pd.concat(analyzer_dfs, ignore_index=True)
        
        # Filter by date range and instruments
        combined_df = self._filter_dataframe(combined_df)
        
        # Extract canonical records
        canonical_records = []
        
        for _, row in combined_df.iterrows():
            record = self._extract_canonical_record(row)
            if record:
                canonical_records.append(record)
        
        if not canonical_records:
            return None
        
        return pd.DataFrame(canonical_records)
    
    def _run_analyzer_with_snapshot(self) -> Optional[Path]:
        """
        Run analyzer using snapshot data as input.
        Outputs to run-specific directory.
        
        Returns:
            Path to analyzer output file, or None if failed
        """
        if self.output_dir is None:
            raise ValueError("output_dir required for run_analyzer mode")
        
        # Import analyzer runner
        import subprocess
        import sys
        
        # Prepare analyzer command
        # Use the analyzer's run_data_processed.py script
        analyzer_script = self.project_root / "modules" / "analyzer" / "scripts" / "run_data_processed.py"
        
        if not analyzer_script.exists():
            print(f"Warning: Analyzer script not found at {analyzer_script}")
            return None
        
        # Create output directory for analyzer
        analyzer_output_dir = self.output_dir / "analyzer_output"
        analyzer_output_dir.mkdir(parents=True, exist_ok=True)
        
        # Run analyzer for each instrument
        # Note: This is a simplified implementation - in practice you might want to use
        # the parallel analyzer runner or call the analyzer API directly
        print("Running analyzer with snapshot data...")
        print(f"  Input: {self.data_source_dir}")
        print(f"  Output: {analyzer_output_dir}")
        
        # TODO: Implement full analyzer execution
        # For now, return None to indicate analyzer needs to be run separately
        print("Warning: Analyzer execution not yet fully implemented in parity mode")
        print("  Please run analyzer separately and use --mode load_existing_analyzer")
        return None
    
    def _is_relevant(self, df: pd.DataFrame) -> bool:
        """Check if dataframe has required columns."""
        required_cols = ["Date", "Instrument", "Stream", "Session", "Time"]
        return all(col in df.columns for col in required_cols)
    
    def _filter_dataframe(self, df: pd.DataFrame) -> pd.DataFrame:
        """Filter dataframe by date range and instruments."""
        # Convert Date column to Timestamp if needed
        if "Date" in df.columns:
            df["Date"] = pd.to_datetime(df["Date"])
            if df["Date"].dt.tz is None:
                df["Date"] = df["Date"].dt.tz_localize("America/Chicago")
        
        # Filter by date range
        mask = (df["Date"] >= self.start_date) & (df["Date"] <= self.end_date)
        df = df[mask].copy()
        
        # Filter by instruments
        if self.instruments:
            mask = df["Instrument"].isin(self.instruments)
            df = df[mask].copy()
        
        return df
    
    def _extract_canonical_record(self, row: pd.Series) -> Optional[Dict[str, Any]]:
        """
        Extract a canonical record from an Analyzer result row.
        
        Analyzer columns:
        - Date, Time, EntryTime, ExitTime, Target, Peak, Direction, Result, Range, Stream, Instrument, Session, Profit
        - entry_price, exit_price, stop_loss (if available)
        """
        trading_date = row["Date"]
        if isinstance(trading_date, pd.Timestamp):
            trading_date_str = trading_date.strftime("%Y-%m-%d")
        else:
            trading_date_str = str(trading_date)
        
        instrument = str(row["Instrument"])
        stream = str(row["Stream"])
        session = str(row["Session"])
        slot_time = str(row["Time"])
        
        # Determine intended_trade and direction
        direction = str(row.get("Direction", "NA"))
        result = str(row.get("Result", ""))
        
        if direction == "NA" or result == "NoTrade":
            intended_trade = False
            direction_canonical = None
        else:
            intended_trade = True
            direction_canonical = direction
        
        # Get range values (if available)
        range_size = row.get("Range", None)
        range_high = row.get("range_high", None)
        range_low = row.get("range_low", None)
        
        # Compute breakout levels if not directly available
        # We need range_high and range_low to compute breakout levels
        # If not available, we'll need to compute from range_size (approximation)
        tick_size = self.instrument_configs.get(instrument, {}).get("tick_size", 0.25)
        
        brk_long_rounded = None
        brk_short_rounded = None
        
        if range_high is not None and range_low is not None:
            # Compute breakout levels using Analyzer's method
            brk_long_raw = range_high + tick_size
            brk_short_raw = range_low - tick_size
            
            # Round using numpy (matching Analyzer's UtilityManager.round_to_tick)
            brk_long_rounded = float(np.round(brk_long_raw / tick_size) * tick_size)
            brk_short_rounded = float(np.round(brk_short_raw / tick_size) * tick_size)
        
        # Get entry information
        entry_price = row.get("entry_price", None)
        entry_time_chicago = None
        
        if "EntryTime" in row and pd.notna(row["EntryTime"]):
            entry_time = row["EntryTime"]
            if isinstance(entry_time, str):
                # Parse entry time string (format may vary)
                try:
                    entry_time_chicago = pd.to_datetime(entry_time, errors="coerce")
                    if entry_time_chicago.tz is None:
                        entry_time_chicago = entry_time_chicago.tz_localize("America/Chicago")
                except:
                    pass
            elif isinstance(entry_time, pd.Timestamp):
                entry_time_chicago = entry_time
                if entry_time_chicago.tz is None:
                    entry_time_chicago = entry_time_chicago.tz_localize("America/Chicago")
        
        # If entry_price not directly available, try to infer from direction and breakout levels
        if entry_price is None or pd.isna(entry_price):
            if intended_trade and brk_long_rounded is not None and brk_short_rounded is not None:
                # Analyzer uses breakout level as entry price
                if direction_canonical == "Long":
                    entry_price = brk_long_rounded
                elif direction_canonical == "Short":
                    entry_price = brk_short_rounded
        
        # Get stop and target prices
        stop_price = row.get("stop_loss", None)
        target_price = None
        
        base_target = self.instrument_configs.get(instrument, {}).get("base_target", None)
        if base_target is not None and entry_price is not None:
            if direction_canonical == "Long":
                target_price = entry_price + base_target
            elif direction_canonical == "Short":
                target_price = entry_price - base_target
        
        # If stop_price not available, compute it
        if stop_price is None or pd.isna(stop_price):
            if base_target is not None and range_size is not None and entry_price is not None:
                # Stop loss = min(range_size, 3 * target_pts)
                max_sl_points = 3 * base_target
                sl_points = min(range_size, max_sl_points)
                
                if direction_canonical == "Long":
                    stop_price = entry_price - sl_points
                elif direction_canonical == "Short":
                    stop_price = entry_price + sl_points
        
        # Compute break-even values
        be_stop_price = None
        be_trigger_pts = None
        
        if base_target is not None and entry_price is not None:
            be_trigger_pts = base_target * 0.65  # 65% of target
            
            if direction_canonical == "Long":
                be_stop_price = entry_price - tick_size
            elif direction_canonical == "Short":
                be_stop_price = entry_price + tick_size
        
        return {
            "trading_date": trading_date_str,
            "instrument": instrument,
            "stream": stream,
            "session": session,
            "slot_time": slot_time,
            "range_high": range_high,
            "range_low": range_low,
            "brk_long_rounded": brk_long_rounded,
            "brk_short_rounded": brk_short_rounded,
            "intended_trade": intended_trade,
            "direction": direction_canonical,
            "entry_time_chicago": entry_time_chicago,
            "entry_price": entry_price,
            "stop_price": stop_price,
            "target_price": target_price,
            "be_stop_price": be_stop_price,
            "be_trigger_pts": be_trigger_pts,
        }
