"""
Analyzer Intent Extractor

Stable parity surface — do not change without updating parity spec.

Extracts intent-level trade decisions from Analyzer outputs.
Read-only, deterministic, replay-safe.

Output: One row per (trading_date, stream) intent with canonical fields matching Robot intent schema.
"""

import pandas as pd
from pathlib import Path
from typing import Dict, Any, Optional, List, Tuple
from collections import defaultdict
import pytz
import argparse


class AnalyzerIntentExtractor:
    """
    Extracts Analyzer intents from CSV/parquet outputs.
    
    Maps Analyzer output schema to Robot intent schema (15 columns, exact match).
    Groups by (trading_date, stream) to match Robot intent grouping.
    """
    
    # Required Robot intent columns (exact order)
    REQUIRED_COLUMNS = [
        "trading_date", "stream", "instrument", "session", "slot_time_chicago", "slot_time_utc",
        "direction", "entry_price", "stop_price", "target_price", "be_trigger",
        "decision_ts_utc", "decision_ts_chicago", "completeness_flag", "error_flag"
    ]
    
    def __init__(self, input_path: Path, output_path: Optional[Path] = None, run_id: Optional[str] = None, project_root: Optional[Path] = None):
        """
        Initialize extractor.
        
        Args:
            input_path: Path to Analyzer output file (CSV or parquet) or directory containing files
            output_path: Output CSV path (if None, auto-determine from run_id or input_path)
            run_id: Optional parity run ID (for auto-determining output path)
            project_root: Project root directory (for auto-determining output path)
        """
        self.input_path = Path(input_path)
        self.run_id = run_id
        self.project_root = project_root or Path.cwd()
        
        # Determine output path
        if output_path:
            self.output_path = Path(output_path)
        elif run_id:
            # Place next to robot_intents.csv in parity run directory
            self.output_path = self.project_root / "docs" / "robot" / "parity_runs" / run_id / "analyzer_intents.csv"
        else:
            # Place next to input file
            if self.input_path.is_file():
                self.output_path = self.input_path.parent / "analyzer_intents.csv"
            else:
                self.output_path = self.input_path / "analyzer_intents.csv"
        
        self.chicago_tz = pytz.timezone("America/Chicago")
    
    def extract(self) -> pd.DataFrame:
        """
        Extract intents from Analyzer outputs.
        
        Returns:
            DataFrame with one row per intent, columns in exact order matching Robot intents:
            trading_date, stream, instrument, session, slot_time_chicago, slot_time_utc,
            direction, entry_price, stop_price, target_price, be_trigger,
            decision_ts_utc, decision_ts_chicago, completeness_flag, error_flag
        """
        # Load Analyzer output files
        analyzer_dfs = self._load_analyzer_files()
        
        if not analyzer_dfs:
            # Return empty DataFrame with correct columns
            return pd.DataFrame(columns=self.REQUIRED_COLUMNS)
        
        # Combine all dataframes
        combined_df = pd.concat(analyzer_dfs, ignore_index=True)
        
        # Group by (trading_date, stream) to match Robot intent grouping
        intent_groups: Dict[Tuple[str, str], Dict[str, Any]] = defaultdict(lambda: {
            "trading_date": None,
            "stream": None,
            "instrument": None,
            "session": None,
            "slot_time_chicago": None,
            "slot_time_utc": None,
            "direction": None,
            "entry_price": None,
            "stop_price": None,
            "target_price": None,
            "be_trigger": None,
            "decision_ts_utc": None,
            "decision_ts_chicago": None,
            "rows": [],
            "errors": []
        })
        
        # Process each row
        for _, row in combined_df.iterrows():
            self._process_row(row, intent_groups)
        
        # Build intents from groups
        intents = []
        for (trading_date, stream), group in sorted(intent_groups.items()):
            intent = self._build_intent(trading_date, stream, group)
            if intent:
                intents.append(intent)
        
        # Create DataFrame with exact column order
        if not intents:
            return pd.DataFrame(columns=self.REQUIRED_COLUMNS)
        
        df = pd.DataFrame(intents)
        
        # Ensure exact column order
        df = df[[c for c in self.REQUIRED_COLUMNS if c in df.columns]]
        
        # Sort for determinism: trading_date, then stream
        df = df.sort_values(["trading_date", "stream"], kind="stable").reset_index(drop=True)
        
        return df
    
    def _load_analyzer_files(self) -> List[pd.DataFrame]:
        """Load Analyzer output files (CSV or parquet)."""
        analyzer_dfs = []
        
        if self.input_path.is_file():
            # Single file
            files = [self.input_path]
        else:
            # Directory - find all CSV and parquet files
            files = list(self.input_path.rglob("*.csv")) + list(self.input_path.rglob("*.parquet"))
        
        for file_path in files:
            try:
                if file_path.suffix.lower() == ".csv":
                    df = pd.read_csv(file_path)
                elif file_path.suffix.lower() == ".parquet":
                    df = pd.read_parquet(file_path)
                else:
                    continue
                
                # Verify required columns
                required_cols = ["Date", "Stream", "Instrument", "Session", "Time"]
                if not all(col in df.columns for col in required_cols):
                    print(f"Warning: Skipping {file_path.name} - missing required columns")
                    continue
                
                analyzer_dfs.append(df)
            except Exception as e:
                print(f"Warning: Could not read {file_path}: {e}")
                continue
        
        return analyzer_dfs
    
    def _derive_stream_name(self, instrument: str, session: str, time: str) -> str:
        """Derive stream name from instrument, session, and time to match Robot naming.
        
        S1 slots: 07:30 -> <INSTRUMENT>1, 08:00 -> <INSTRUMENT>2, 09:00 -> <INSTRUMENT>3
        S2 slots: 09:30 -> <INSTRUMENT>4, 10:00 -> <INSTRUMENT>5, 10:30 -> <INSTRUMENT>6, 11:00 -> <INSTRUMENT>7
        """
        if session == "S1":
            if time == "07:30":
                return f"{instrument}1"
            elif time == "08:00":
                return f"{instrument}2"
            elif time == "09:00":
                return f"{instrument}3"
        elif session == "S2":
            if time == "09:30":
                return f"{instrument}4"
            elif time == "10:00":
                return f"{instrument}5"
            elif time == "10:30":
                return f"{instrument}6"
            elif time == "11:00":
                return f"{instrument}7"
        
        # Fallback: use original Stream column if mapping not found
        return None
    
    def _process_row(self, row: pd.Series, intent_groups: Dict[Tuple[str, str], Dict[str, Any]]):
        """Process a single Analyzer row and update intent groups."""
        # Extract grouping key
        trading_date = self._normalize_date(row["Date"])
        instrument = str(row.get("Instrument", ""))
        session = str(row.get("Session", ""))
        time = str(row.get("Time", ""))
        
        # Derive stream name from (instrument, session, time) to match Robot naming
        stream = self._derive_stream_name(instrument, session, time)
        if stream is None:
            # Fallback to original Stream column if derivation fails
            stream = str(row.get("Stream", ""))
        
        if not trading_date or not stream:
            return
        
        key = (trading_date, stream)
        group = intent_groups[key]
        
        # Store row for processing (we'll select the canonical row later)
        group["rows"].append(row)
        
    def _extract_from_row(self, row: pd.Series, group: Dict[str, Any], trading_date: str):
        """Extract intent fields from a single canonical row."""
        # Initialize group fields from canonical row
        group["instrument"] = str(row["Instrument"])
        group["session"] = str(row["Session"])
        group["slot_time_chicago"] = str(row["Time"])
        
        # Derive slot_time_utc deterministically
        slot_time_utc = self._derive_slot_time_utc(trading_date, group["slot_time_chicago"])
        group["slot_time_utc"] = slot_time_utc
        
        # Extract direction
        direction = str(row.get("Direction", "")).strip()
        result = str(row.get("Result", "")).strip()
        
        # Check for NoTrade
        if direction.upper() == "NA" or result == "NoTrade":
            # NoTrade case - null direction
            group["direction"] = None
        else:
            # Normalize direction to uppercase
            direction_upper = direction.upper()
            if direction_upper not in ["LONG", "SHORT"]:
                group["errors"].append(f"Invalid direction: {direction}")
                group["direction"] = None
            else:
                group["direction"] = direction_upper
        
        # Extract entry price
        entry_price = self._safe_float(row.get("EntryPrice"))
        group["entry_price"] = entry_price
        
        # StopLoss is in points - convert to stop_price
        stop_loss_points = self._safe_float(row.get("StopLoss"))
        if stop_loss_points is not None and group["direction"] and group["entry_price"]:
            if group["direction"] == "LONG":
                stop_price = group["entry_price"] - stop_loss_points
            elif group["direction"] == "SHORT":
                stop_price = group["entry_price"] + stop_loss_points
            else:
                stop_price = None
            group["stop_price"] = stop_price
        else:
            group["stop_price"] = None
        
        # Target price
        target_points = self._safe_float(row.get("Target"))
        if target_points is not None and group["direction"] and group["entry_price"]:
            if group["direction"] == "LONG":
                target_price = group["entry_price"] + target_points
            elif group["direction"] == "SHORT":
                target_price = group["entry_price"] - target_points
            else:
                target_price = None
            group["target_price"] = target_price
        else:
            group["target_price"] = None
        
        # BE trigger (65% of target)
        if target_points is not None and group["direction"] and group["entry_price"]:
            be_trigger_points = target_points * 0.65
            if group["direction"] == "LONG":
                be_trigger = group["entry_price"] + be_trigger_points
            elif group["direction"] == "SHORT":
                be_trigger = group["entry_price"] - be_trigger_points
            else:
                be_trigger = None
            group["be_trigger"] = be_trigger
        else:
            group["be_trigger"] = None
        
        # Decision timestamp (from EntryTime if available, otherwise derive from slot_time)
        entry_time_str = row.get("EntryTime")
        if entry_time_str and pd.notna(entry_time_str) and str(entry_time_str).strip():
            try:
                # Parse EntryTime (format may vary: "10/08/18 08:03" or ISO)
                entry_time_chicago = self._parse_entry_time(str(entry_time_str), trading_date)
                if entry_time_chicago:
                    group["decision_ts_chicago"] = entry_time_chicago.isoformat()
                    # Convert to UTC
                    entry_time_utc = entry_time_chicago.astimezone(pytz.UTC)
                    group["decision_ts_utc"] = entry_time_utc.isoformat()
            except Exception as e:
                group["errors"].append(f"Failed to parse EntryTime '{entry_time_str}': {e}")
        
        # If no EntryTime, derive from slot_time_chicago + trading_date
        if not group.get("decision_ts_chicago"):
            slot_ts = self._derive_slot_timestamp(trading_date, group["slot_time_chicago"])
            if slot_ts:
                group["decision_ts_chicago"] = slot_ts.isoformat()
                slot_ts_utc = slot_ts.astimezone(pytz.UTC)
                group["decision_ts_utc"] = slot_ts_utc.isoformat()
    
    def _build_intent(self, trading_date: str, stream: str, group: Dict[str, Any]) -> Optional[Dict[str, Any]]:
        """Build canonical intent from group."""
        if not group["rows"]:
            return None
        
        # Select canonical row: prefer first trade row (by Time), otherwise first row
        rows = group["rows"]
        
        # Sort by Time to ensure deterministic selection
        rows_sorted = sorted(rows, key=lambda r: str(r.get("Time", "")))
        
        # Find first trade row (Direction != "NA" and Result != "NoTrade")
        canonical_row = None
        trade_rows = []
        for row in rows_sorted:
            direction = str(row.get("Direction", "")).strip().upper()
            result = str(row.get("Result", "")).strip()
            if direction != "NA" and result != "NOTRADE":
                trade_rows.append(row)
                if canonical_row is None:
                    canonical_row = row
        
        # If no trade row found, use first row (NoTrade case)
        if canonical_row is None:
            canonical_row = rows_sorted[0]
        
        # Flag if multiple trade rows exist
        if len(trade_rows) > 1:
            group["errors"].append(f"Multiple trade rows for (trading_date, stream): {len(trade_rows)} trades")
        
        # Flag if multiple rows total
        if len(rows) > 1:
            group["errors"].append(f"Multiple rows for (trading_date, stream): {len(rows)} rows")
        
        # Process canonical row
        self._extract_from_row(canonical_row, group, trading_date)
        
        # Determine completeness
        has_entry = group["entry_price"] is not None
        has_stop = group["stop_price"] is not None
        has_target = group["target_price"] is not None
        has_be = group["be_trigger"] is not None
        
        completeness = "COMPLETE" if (has_entry and has_stop and has_target and has_be) else "PARTIAL"
        
        # Build error flag
        error_flag = "; ".join(group["errors"]) if group["errors"] else None
        
        # Validate required fields
        if not group["direction"] and completeness == "COMPLETE":
            # Only flag missing direction if we expected a complete intent
            if error_flag:
                error_flag = f"{error_flag}; Missing direction"
            else:
                error_flag = "Missing direction"
        
        # Build intent
        intent = {
            "trading_date": trading_date,
            "stream": stream,
            "instrument": group["instrument"],
            "session": group["session"],
            "slot_time_chicago": group["slot_time_chicago"],
            "slot_time_utc": group["slot_time_utc"],
            "direction": group["direction"],
            "entry_price": group["entry_price"],
            "stop_price": group["stop_price"],
            "target_price": group["target_price"],
            "be_trigger": group["be_trigger"],
            "decision_ts_utc": group["decision_ts_utc"],
            "decision_ts_chicago": group["decision_ts_chicago"],
            "completeness_flag": completeness,
            "error_flag": error_flag
        }
        
        return intent
    
    def _normalize_date(self, date_value: Any) -> Optional[str]:
        """Normalize date to YYYY-MM-DD format."""
        if pd.isna(date_value):
            return None
        
        if isinstance(date_value, pd.Timestamp):
            return date_value.strftime("%Y-%m-%d")
        elif isinstance(date_value, str):
            # Try parsing
            try:
                ts = pd.to_datetime(date_value)
                return ts.strftime("%Y-%m-%d")
            except:
                return None
        else:
            return str(date_value)
    
    def _derive_slot_time_utc(self, trading_date: str, slot_time_chicago: str) -> Optional[str]:
        """Derive slot_time_utc deterministically from slot_time_chicago + trading_date."""
        slot_ts = self._derive_slot_timestamp(trading_date, slot_time_chicago)
        if slot_ts:
            slot_ts_utc = slot_ts.astimezone(pytz.UTC)
            return slot_ts_utc.isoformat()
        return None
    
    def _derive_slot_timestamp(self, trading_date: str, slot_time_chicago: str) -> Optional[pd.Timestamp]:
        """Derive timestamp from trading_date + slot_time_chicago."""
        try:
            # Parse slot time (format: "07:30" or "7:30")
            parts = slot_time_chicago.split(":")
            if len(parts) != 2:
                return None
            
            hour = int(parts[0])
            minute = int(parts[1])
            
            # Create timestamp in Chicago timezone
            date_obj = pd.to_datetime(trading_date).date()
            time_obj = pd.Timestamp.min.time().replace(hour=hour, minute=minute, second=0, microsecond=0)
            chicago_dt = self.chicago_tz.localize(
                pd.Timestamp.combine(date_obj, time_obj)
            )
            
            return pd.Timestamp(chicago_dt)
        except Exception:
            return None
    
    def _parse_entry_time(self, entry_time_str: str, trading_date: str) -> Optional[pd.Timestamp]:
        """Parse EntryTime string to Chicago timestamp."""
        try:
            # Try ISO format first
            try:
                ts = pd.to_datetime(entry_time_str)
                if ts.tz is None:
                    # Assume Chicago timezone
                    ts = self.chicago_tz.localize(ts)
                else:
                    ts = ts.tz_convert(self.chicago_tz)
                # Normalize to trading_date if date doesn't match
                if ts.date() != pd.to_datetime(trading_date).date():
                    # Use trading_date but keep the time
                    date_obj = pd.to_datetime(trading_date).date()
                    time_obj = ts.time()
                    ts = self.chicago_tz.localize(pd.Timestamp.combine(date_obj, time_obj))
                return ts
            except:
                pass
            
            # Try format like "10/08/18 08:03" (DD/MM/YY HH:MM)
            # Note: This format is ambiguous - we'll use trading_date as the authoritative date
            parts = entry_time_str.strip().split()
            if len(parts) == 2:
                time_part = parts[1]
                time_parts = time_part.split(":")
                if len(time_parts) == 2:
                    hour = int(time_parts[0])
                    minute = int(time_parts[1])
                    
                    # Use trading_date as the authoritative date (not the date in EntryTime string)
                    date_obj = pd.to_datetime(trading_date).date()
                    time_obj = pd.Timestamp.min.time().replace(hour=hour, minute=minute, second=0, microsecond=0)
                    chicago_dt = self.chicago_tz.localize(
                        pd.Timestamp.combine(date_obj, time_obj)
                    )
                    return pd.Timestamp(chicago_dt)
        except Exception:
            pass
        
        return None
    
    def _safe_float(self, value: Any) -> Optional[float]:
        """Safely convert value to float."""
        if pd.isna(value):
            return None
        try:
            return float(value)
        except (ValueError, TypeError):
            return None
    
    def save(self, df: pd.DataFrame) -> Path:
        """Save intents to CSV file."""
        self.output_path.parent.mkdir(parents=True, exist_ok=True)
        df.to_csv(self.output_path, index=False)
        return self.output_path


def main():
    """CLI entry point."""
    parser = argparse.ArgumentParser(description="Extract Analyzer intents from CSV/parquet outputs")
    parser.add_argument("--input", type=Path, required=True, help="Input Analyzer output file (CSV/parquet) or directory")
    parser.add_argument("--out", type=Path, help="Output CSV path (default: next to input or in parity run directory)")
    parser.add_argument("--run-id", type=str, help="Parity run ID (for auto-determining output path)")
    parser.add_argument("--project-root", type=Path, help="Project root directory")
    
    args = parser.parse_args()
    
    extractor = AnalyzerIntentExtractor(
        input_path=args.input,
        output_path=args.out,
        run_id=args.run_id,
        project_root=args.project_root
    )
    
    print(f"Extracting intents from: {extractor.input_path}")
    df = extractor.extract()
    
    if len(df) == 0:
        print("Warning: No intents extracted")
        return
    
    extractor.save(df)
    
    print(f"Extracted {len(df)} intents")
    print(f"Complete intents: {len(df[df['completeness_flag'] == 'COMPLETE'])}")
    print(f"Partial intents: {len(df[df['completeness_flag'] == 'PARTIAL'])}")
    print(f"Intents with errors: {len(df[df['error_flag'].notna()])}")
    print(f"Saved to: {extractor.output_path}")


if __name__ == "__main__":
    main()
