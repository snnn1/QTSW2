"""
Robot Canonical Extractor

Extracts canonical comparison records from Robot DRYRUN JSONL logs.
One row per (trading_date, instrument, stream, slot_time).
"""

import json
import pandas as pd
from pathlib import Path
from typing import Optional, List, Dict, Any
from datetime import datetime
import pytz


class RobotExtractor:
    """Extracts canonical records from Robot DRYRUN logs for parity comparison."""
    
    def __init__(
        self,
        project_root: Path,
        parity_spec: Dict[str, Any],
        start_date: pd.Timestamp,
        end_date: pd.Timestamp,
        instruments: Optional[List[str]] = None,
        source: str = "harness",
        log_dir: Optional[Path] = None
    ):
        self.project_root = project_root
        self.parity_spec = parity_spec
        self.start_date = start_date
        self.end_date = end_date
        self.instruments = instruments
        self.source = source
        self.log_dir = log_dir  # Run-specific log directory
        
        # Get all instruments from parity spec if not specified
        if self.instruments is None:
            self.instruments = list(parity_spec.get("instruments", {}).keys())
        
        # Load instrument configs
        self.instrument_configs = parity_spec.get("instruments", {})
        
        # Chicago timezone
        self.chicago_tz = pytz.timezone("America/Chicago")
    
    def extract(self) -> Optional[pd.DataFrame]:
        """
        Extract canonical records from Robot JSONL logs.
        
        Returns:
            DataFrame with columns:
            - trading_date, instrument, stream, session, slot_time
            - brk_long_rounded, brk_short_rounded
            - intended_trade, direction
            - entry_time_chicago, entry_price
            - stop_price, target_price
            - be_stop_price, be_trigger_pts
        """
        # Determine log file path
        # For parity runs, prefer run-specific log directory
        if self.log_dir is not None and self.log_dir.exists():
            # Look for log file in run-specific directory
            log_path = self.log_dir / "robot_skeleton.jsonl"
            if not log_path.exists():
                # Try alternative name
                log_path = self.log_dir / "robot_dryrun.jsonl"
        elif self.source == "harness":
            # Fallback to default harness log location
            log_path = self.project_root / "logs" / "robot" / "robot_skeleton.jsonl"
        else:
            # NT logs - would be in a different location
            log_path = self.project_root / "logs" / "robot" / "robot_nt.jsonl"
        
        if not log_path.exists():
            print(f"Warning: Robot log file not found at {log_path}")
            if self.log_dir:
                print(f"  Expected in run-specific directory: {self.log_dir}")
            return None
        
        # Parse JSONL and extract records
        records_by_key = {}  # Key: (trading_date, instrument, stream, slot_time)
        
        with open(log_path, "r", encoding="utf-8") as f:
            for line_num, line in enumerate(f, 1):
                line = line.strip()
                if not line:
                    continue
                
                try:
                    event = json.loads(line)
                    self._process_event(event, records_by_key)
                except json.JSONDecodeError as e:
                    print(f"Warning: Could not parse JSONL line {line_num}: {e}")
                    continue
        
        if not records_by_key:
            return None
        
        # Convert to DataFrame
        canonical_records = []
        for key, record in records_by_key.items():
            canonical_records.append(record)
        
        return pd.DataFrame(canonical_records)
    
    def _process_event(self, event: Dict[str, Any], records_by_key: Dict[tuple, Dict[str, Any]]):
        """Process a single JSONL event and update records."""
        event_type = event.get("event_type", "")
        stream = event.get("stream", "")
        trading_date = event.get("trading_date", "")
        
        # Skip engine-level events
        if stream == "__engine__" or not trading_date:
            return
        
        # Filter by date range
        try:
            event_date = pd.Timestamp(trading_date)
            if event_date.tz is None:
                event_date = event_date.tz_localize("America/Chicago")
            
            if event_date < self.start_date or event_date > self.end_date:
                return
        except:
            return
        
        # Filter by instruments (check stream prefix)
        instrument = event.get("instrument", "")
        if self.instruments and instrument not in self.instruments:
            return
        
        session = event.get("session", "")
        slot_time = event.get("slot_time_chicago", "")
        
        if not all([trading_date, instrument, stream, session, slot_time]):
            return
        
        # Create key
        key = (trading_date, instrument, stream, slot_time)
        
        # Initialize record if not exists
        if key not in records_by_key:
            records_by_key[key] = {
                "trading_date": trading_date,
                "instrument": instrument,
                "stream": stream,
                "session": session,
                "slot_time": slot_time,
                "brk_long_rounded": None,
                "brk_short_rounded": None,
                "intended_trade": None,
                "direction": None,
                "entry_time_chicago": None,
                "entry_price": None,
                "stop_price": None,
                "target_price": None,
                "be_stop_price": None,
                "be_trigger_pts": None,
            }
        
        record = records_by_key[key]
        data = event.get("data", {})
        
        # Process different event types
        if event_type == "DRYRUN_BREAKOUT_LEVELS":
            record["brk_long_rounded"] = data.get("brk_long_rounded")
            record["brk_short_rounded"] = data.get("brk_short_rounded")
        
        elif event_type == "DRYRUN_INTENDED_ENTRY":
            intended_trade = data.get("intended_trade", False)
            record["intended_trade"] = intended_trade
            
            if intended_trade:
                record["direction"] = data.get("direction")
                
                # Parse entry time
                entry_time_utc_str = data.get("entry_time_utc")
                if entry_time_utc_str:
                    try:
                        entry_time_utc = pd.Timestamp(entry_time_utc_str)
                        # Convert UTC to Chicago
                        if entry_time_utc.tz is None:
                            entry_time_utc = entry_time_utc.tz_localize("UTC")
                        entry_time_chicago = entry_time_utc.tz_convert("America/Chicago")
                        record["entry_time_chicago"] = entry_time_chicago
                    except:
                        pass
                
                record["entry_price"] = data.get("entry_price")
            else:
                # NoTrade
                record["direction"] = None
                record["entry_price"] = None
        
        elif event_type == "DRYRUN_INTENDED_PROTECTIVE":
            record["stop_price"] = data.get("stop_price")
            record["target_price"] = data.get("target_price")
        
        elif event_type == "DRYRUN_INTENDED_BE":
            record["be_stop_price"] = data.get("be_stop_price")
            record["be_trigger_pts"] = data.get("be_trigger_pts")
    
    def _get_commit_point_record(self, records_by_key: Dict[tuple, Dict[str, Any]]) -> Dict[tuple, Dict[str, Any]]:
        """
        For each stream/day, select the final committed record.
        In DRYRUN mode, the final record is the one with intended_trade set.
        """
        # Group by (trading_date, stream) to find commit points
        committed_records = {}
        
        for key, record in records_by_key.items():
            trading_date, instrument, stream, slot_time = key
            
            # Use the record with intended_trade set (final state)
            if record.get("intended_trade") is not None:
                stream_key = (trading_date, stream)
                if stream_key not in committed_records:
                    committed_records[stream_key] = record
                else:
                    # Keep the most recent one (by checking if this one has more complete data)
                    existing = committed_records[stream_key]
                    if record.get("entry_price") is not None:
                        committed_records[stream_key] = record
        
        return committed_records
