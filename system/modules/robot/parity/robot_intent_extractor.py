"""
Robot Intent Extractor

Stable parity surface — do not change without updating parity spec.

Extracts intent-level trade decisions from Robot DRYRUN logs.
Read-only, deterministic, replay-safe.

Output: One row per (trading_date, stream) intent with canonical fields.
"""

import json
import pandas as pd
from pathlib import Path
from typing import Dict, Any, Optional, List, Tuple
from collections import defaultdict
import pytz


class RobotIntentExtractor:
    """
    Extracts Robot intents from DRYRUN JSONL logs.
    
    An intent is considered complete when all of the following have been observed:
    - Intended entry
    - Intended protective stop
    - Intended target (if applicable)
    - Intended BE trigger (if applicable)
    """
    
    # Event types to extract
    INTENT_EVENT_TYPES = {
        "DRYRUN_INTENDED_ENTRY",
        "DRYRUN_INTENDED_PROTECTIVE",
        "DRYRUN_INTENDED_TARGET",  # If present in system
        "DRYRUN_INTENDED_BE"
    }
    
    def __init__(self, run_id: str, project_root: Optional[Path] = None):
        """
        Initialize extractor.
        
        Args:
            run_id: Parity run ID (e.g., "replay_2025-11-15__2025-12-31")
            project_root: Project root directory (defaults to current working directory)
        """
        self.run_id = run_id
        self.project_root = project_root or Path.cwd()
        
        # Input: robot_logs/*.jsonl
        self.logs_dir = self.project_root / "docs" / "robot" / "parity_runs" / run_id / "robot_logs"
        
        # Output: robot_intents.csv
        self.output_dir = self.project_root / "docs" / "robot" / "parity_runs" / run_id
        self.output_path = self.output_dir / "robot_intents.csv"
        
        self.chicago_tz = pytz.timezone("America/Chicago")
    
    def extract(self) -> pd.DataFrame:
        """
        Extract intents from Robot logs.
        
        Returns:
            DataFrame with one row per intent, columns in exact order:
            trading_date, stream, instrument, session, slot_time_chicago, slot_time_utc,
            direction, entry_price, stop_price, target_price, be_trigger,
            decision_ts_utc, decision_ts_chicago, completeness_flag, error_flag
        """
        # Find all JSONL files in logs directory
        log_files = list(self.logs_dir.glob("*.jsonl"))
        if not log_files:
            raise FileNotFoundError(f"No JSONL files found in {self.logs_dir}")
        
        # Group events by (trading_date, stream)
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
            "be_trigger": None,  # Will store be_trigger_price or be_trigger_pts
            "decision_ts_utc": None,
            "decision_ts_chicago": None,
            "entry_events": [],
            "protective_events": [],
            "be_events": [],
            "errors": []
        })
        
        # Parse all log files
        for log_file in sorted(log_files):
            self._parse_log_file(log_file, intent_groups)
        
        # Convert groups to canonical intents
        intents = []
        for (trading_date, stream), group in sorted(intent_groups.items()):
            intent = self._build_intent(trading_date, stream, group)
            if intent:
                intents.append(intent)
        
        # Create DataFrame with exact column order
        if not intents:
            # Return empty DataFrame with correct columns
            columns = [
                "trading_date", "stream", "instrument", "session", "slot_time_chicago", "slot_time_utc",
                "direction", "entry_price", "stop_price", "target_price", "be_trigger",
                "decision_ts_utc", "decision_ts_chicago", "completeness_flag", "error_flag"
            ]
            return pd.DataFrame(columns=columns)
        
        df = pd.DataFrame(intents)
        
        # Ensure exact column order
        columns = [
            "trading_date", "stream", "instrument", "session", "slot_time_chicago", "slot_time_utc",
            "direction", "entry_price", "stop_price", "target_price", "be_trigger",
            "decision_ts_utc", "decision_ts_chicago", "completeness_flag", "error_flag"
        ]
        df = df[[c for c in columns if c in df.columns]]
        
        # Sort for determinism: trading_date, then stream
        df = df.sort_values(["trading_date", "stream"], kind="stable").reset_index(drop=True)
        
        return df
    
    def _parse_log_file(self, log_file: Path, intent_groups: Dict[Tuple[str, str], Dict[str, Any]]):
        """Parse a single JSONL log file and update intent groups."""
        content = log_file.read_text(encoding="utf-8")
        
        # Parse JSON objects - they're separated by }\n{ but may be multi-line
        decoder = json.JSONDecoder()
        idx = 0
        content = content.strip()
        line_num = 1
        
        while idx < len(content):
            # Skip whitespace
            while idx < len(content) and content[idx] in " \n\r\t":
                if content[idx] == "\n":
                    line_num += 1
                idx += 1
            if idx >= len(content):
                break
            
            # Find next {
            next_brace = content.find("{", idx)
            if next_brace == -1:
                break
            idx = next_brace
            
            try:
                obj, end_idx = decoder.raw_decode(content, idx)
                self._process_event(obj, intent_groups)
                idx = end_idx
            except json.JSONDecodeError as e:
                # Skip malformed events - log first few errors only
                if line_num <= 10:
                    print(f"Warning: Could not parse JSON at line ~{line_num} in {log_file.name}: {e}")
                # Try to find next object
                next_brace = content.find("{", idx + 1)
                if next_brace == -1:
                    break
                idx = next_brace
    
    def _process_event(self, event: Dict[str, Any], intent_groups: Dict[Tuple[str, str], Dict[str, Any]]):
        """Process a single event and update intent groups."""
        event_type = event.get("event_type", "")
        
        # Filter: only process intent events
        if event_type not in self.INTENT_EVENT_TYPES:
            return
        
        # Extract grouping key fields
        trading_date = event.get("trading_date", "")
        stream = event.get("stream", "")
        
        # Skip engine-level events
        if stream == "__engine__" or not trading_date:
            return
        
        # Create grouping key
        key = (trading_date, stream)
        
        # Initialize group if needed
        group = intent_groups[key]
        if group["trading_date"] is None:
            group["trading_date"] = trading_date
            group["stream"] = stream
            group["instrument"] = event.get("instrument", "")
            group["session"] = event.get("session", "")
            group["slot_time_chicago"] = event.get("slot_time_chicago", "")
            slot_time_utc = event.get("slot_time_utc")
            if slot_time_utc:
                group["slot_time_utc"] = slot_time_utc
        
        data = event.get("data", {})
        
        # Process by event type
        if event_type == "DRYRUN_INTENDED_ENTRY":
            group["entry_events"].append(event)
            
            intended_trade = data.get("intended_trade", False)
            if intended_trade:
                direction = data.get("direction")
                # Normalize direction to uppercase (LONG/SHORT)
                if direction:
                    direction = direction.upper()
                
                # Validate direction consistency
                if group["direction"] is not None and group["direction"] != direction:
                    group["errors"].append(f"Direction conflict: {group['direction']} vs {direction}")
                else:
                    group["direction"] = direction
                
                # Extract entry price and decision timestamp
                entry_price = data.get("entry_price")
                if entry_price is not None:
                    group["entry_price"] = float(entry_price)
                
                entry_time_utc_str = data.get("entry_time_utc")
                if entry_time_utc_str:
                    group["decision_ts_utc"] = entry_time_utc_str
                
                entry_time_chicago_str = data.get("entry_time_chicago")
                if entry_time_chicago_str:
                    group["decision_ts_chicago"] = entry_time_chicago_str
        
        elif event_type == "DRYRUN_INTENDED_PROTECTIVE":
            group["protective_events"].append(event)
            
            stop_price = data.get("stop_price")
            if stop_price is not None:
                if group["stop_price"] is not None and group["stop_price"] != stop_price:
                    group["errors"].append(f"Stop price conflict: {group['stop_price']} vs {stop_price}")
                else:
                    group["stop_price"] = float(stop_price)
            
            target_price = data.get("target_price")
            if target_price is not None:
                if group["target_price"] is not None and group["target_price"] != target_price:
                    group["errors"].append(f"Target price conflict: {group['target_price']} vs {target_price}")
                else:
                    group["target_price"] = float(target_price)
        
        elif event_type == "DRYRUN_INTENDED_BE":
            group["be_events"].append(event)
            
            # Prefer be_trigger_price, fall back to be_trigger_pts
            be_trigger_price = data.get("be_trigger_price")
            be_trigger_pts = data.get("be_trigger_pts")
            
            if be_trigger_price is not None:
                be_value = float(be_trigger_price)
            elif be_trigger_pts is not None:
                be_value = float(be_trigger_pts)
            else:
                be_value = None
            
            if be_value is not None:
                if group["be_trigger"] is not None and group["be_trigger"] != be_value:
                    group["errors"].append(f"BE trigger conflict: {group['be_trigger']} vs {be_value}")
                else:
                    group["be_trigger"] = be_value
        
        elif event_type == "DRYRUN_INTENDED_TARGET":
            # Handle if present in system
            target_price = data.get("target_price")
            if target_price is not None:
                if group["target_price"] is not None and group["target_price"] != target_price:
                    group["errors"].append(f"Target price conflict: {group['target_price']} vs {target_price}")
                else:
                    group["target_price"] = float(target_price)
    
    def _build_intent(self, trading_date: str, stream: str, group: Dict[str, Any]) -> Optional[Dict[str, Any]]:
        """
        Build canonical intent from group.
        
        Returns None if no entry event found (invalid intent).
        """
        # Validation: exactly one entry event
        entry_events = group["entry_events"]
        if len(entry_events) == 0:
            return None  # No entry, skip
        
        if len(entry_events) > 1:
            group["errors"].append(f"Multiple entry events: {len(entry_events)}")
        
        # Check for intended_trade = false (NoTrade)
        first_entry = entry_events[0]
        entry_data = first_entry.get("data", {})
        intended_trade = entry_data.get("intended_trade", False)
        
        if not intended_trade:
            # NoTrade case - still emit but with null prices
            return {
                "trading_date": trading_date,
                "stream": stream,
                "instrument": group["instrument"],
                "session": group["session"],
                "slot_time_chicago": group["slot_time_chicago"],
                "slot_time_utc": group["slot_time_utc"],
                "direction": None,
                "entry_price": None,
                "stop_price": None,
                "target_price": None,
                "be_trigger": None,
                "decision_ts_utc": None,
                "decision_ts_chicago": None,
                "completeness_flag": "PARTIAL",
                "error_flag": None
            }
        
        # Validate: at most one protective and one BE event
        if len(group["protective_events"]) > 1:
            group["errors"].append(f"Multiple protective events: {len(group['protective_events'])}")
        
        if len(group["be_events"]) > 1:
            group["errors"].append(f"Multiple BE events: {len(group['be_events'])}")
        
        # Determine completeness
        has_entry = group["entry_price"] is not None
        has_stop = group["stop_price"] is not None
        has_target = group["target_price"] is not None
        has_be = group["be_trigger"] is not None
        
        completeness = "COMPLETE" if (has_entry and has_stop and has_target and has_be) else "PARTIAL"
        
        # Build error flag
        error_flag = "; ".join(group["errors"]) if group["errors"] else None
        
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
    
    def save(self, df: pd.DataFrame) -> Path:
        """
        Save intents to CSV file.
        
        Args:
            df: DataFrame with intents
            
        Returns:
            Path to saved file
        """
        self.output_dir.mkdir(parents=True, exist_ok=True)
        df.to_csv(self.output_path, index=False)
        return self.output_path


def main():
    """CLI entry point."""
    import argparse
    
    parser = argparse.ArgumentParser(description="Extract Robot intents from DRYRUN logs")
    parser.add_argument("run_id", help="Parity run ID (e.g., 'replay_2025-11-15__2025-12-31')")
    parser.add_argument("--project-root", type=Path, help="Project root directory")
    parser.add_argument("--output", type=Path, help="Output CSV path (default: docs/robot/parity_runs/<run_id>/robot_intents.csv)")
    
    args = parser.parse_args()
    
    extractor = RobotIntentExtractor(args.run_id, args.project_root)
    
    print(f"Extracting intents from: {extractor.logs_dir}")
    df = extractor.extract()
    
    if len(df) == 0:
        print("Warning: No intents extracted")
        return
    
    output_path = args.output or extractor.output_path
    extractor.save(df)
    
    print(f"Extracted {len(df)} intents")
    print(f"Complete intents: {len(df[df['completeness_flag'] == 'COMPLETE'])}")
    print(f"Partial intents: {len(df[df['completeness_flag'] == 'PARTIAL'])}")
    print(f"Intents with errors: {len(df[df['error_flag'].notna()])}")
    print(f"Saved to: {output_path}")


if __name__ == "__main__":
    main()
