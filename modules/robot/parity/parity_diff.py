"""
Parity Diff Tool

Stable parity surface — do not change without updating parity spec.

Compares Analyzer and Robot intent-level trade decisions and produces an auditable parity result.
No tolerance logic - measures ground truth.
"""

import pandas as pd
from pathlib import Path
from typing import Dict, Any, Optional
import argparse
from datetime import datetime

# Required columns (exact order)
REQUIRED_COLUMNS = [
    "trading_date", "stream", "instrument", "session", "slot_time_chicago", "slot_time_utc",
    "direction", "entry_price", "stop_price", "target_price", "be_trigger",
    "decision_ts_utc", "decision_ts_chicago", "completeness_flag", "error_flag"
]

# Tick sizes per instrument (from configs/analyzer_robot_parity.json)
TICK_SIZES = {
    "ES": 0.25, "NQ": 0.25, "YM": 1.0, "CL": 0.01, "NG": 0.001, "GC": 0.1, "RTY": 0.10,
    "MES": 0.25, "MNQ": 0.25, "MYM": 1.0, "MCL": 0.01, "MNG": 0.001, "MGC": 0.1, "M2K": 0.10
}


class ParityDiff:
    """Compares Analyzer and Robot intents."""
    
    def __init__(self, run_id: str, project_root: Optional[Path] = None):
        """
        Initialize parity diff.
        
        Args:
            run_id: Parity run ID
            project_root: Project root directory
        """
        self.run_id = run_id
        self.project_root = project_root or Path.cwd()
        
        # Input paths
        self.run_dir = self.project_root / "docs" / "robot" / "parity_runs" / run_id
        self.robot_intents_path = self.run_dir / "robot_intents.csv"
        self.analyzer_intents_path = self.run_dir / "analyzer_intents.csv"
        
        # Output paths
        self.diff_path = self.run_dir / "parity_diff.csv"
        self.summary_path = self.run_dir / "parity_summary.md"
    
    def run(self) -> Dict[str, Any]:
        """
        Run parity diff comparison.
        
        Returns:
            Dictionary with summary statistics
        """
        # Step 1: Load inputs
        print("Loading intents...")
        robot_df = self._load_intents(self.robot_intents_path, "Robot")
        analyzer_df = self._load_intents(self.analyzer_intents_path, "Analyzer")
        
        # Step 2: Join
        print("Joining intents...")
        joined_df = self._join_intents(robot_df, analyzer_df)
        
        # Step 3: Compute diffs
        print("Computing diffs...")
        diff_df = self._compute_diffs(joined_df)
        
        # Step 4: Classify parity status
        print("Classifying parity status...")
        diff_df = self._classify_parity_status(diff_df)
        
        # Step 5: Sort and save
        print("Saving results...")
        
        # Ensure we have trading_date and stream columns for sorting
        # Use robot or analyzer version (fillna handles Robot-only and Analyzer-only rows)
        if "trading_date_robot" in diff_df.columns:
            if "trading_date_analyzer" in diff_df.columns:
                diff_df["_sort_trading_date"] = diff_df["trading_date_robot"].fillna(diff_df["trading_date_analyzer"])
            else:
                diff_df["_sort_trading_date"] = diff_df["trading_date_robot"]
        elif "trading_date_analyzer" in diff_df.columns:
            diff_df["_sort_trading_date"] = diff_df["trading_date_analyzer"]
        else:
            # Fallback - shouldn't happen
            diff_df["_sort_trading_date"] = ""
        
        if "stream_robot" in diff_df.columns:
            if "stream_analyzer" in diff_df.columns:
                diff_df["_sort_stream"] = diff_df["stream_robot"].fillna(diff_df["stream_analyzer"])
            else:
                diff_df["_sort_stream"] = diff_df["stream_robot"]
        elif "stream_analyzer" in diff_df.columns:
            diff_df["_sort_stream"] = diff_df["stream_analyzer"]
        else:
            # Fallback - shouldn't happen
            diff_df["_sort_stream"] = ""
        
        diff_df = diff_df.sort_values(["_sort_trading_date", "_sort_stream"], kind="stable").reset_index(drop=True)
        
        # Remove sort helper columns
        diff_df = diff_df.drop(columns=["_sort_trading_date", "_sort_stream"], errors="ignore")
        
        diff_df.to_csv(self.diff_path, index=False)
        
        # Step 6: Generate summary
        summary = self._generate_summary(robot_df, analyzer_df, diff_df)
        self._write_summary(summary)
        
        print(f"\nParity diff complete:")
        print(f"  Diff CSV: {self.diff_path}")
        print(f"  Summary: {self.summary_path}")
        print(f"  Total rows: {len(diff_df)}")
        
        return summary
    
    def _load_intents(self, path: Path, source: str) -> pd.DataFrame:
        """Load intents CSV with schema validation."""
        if not path.exists():
            raise FileNotFoundError(f"{source} intents file not found: {path}")
        
        df = pd.read_csv(path)
        
        # Validate schema
        if len(df.columns) != 15:
            raise ValueError(
                f"{source} intents schema mismatch: expected 15 columns, got {len(df.columns)}\n"
                f"  Columns: {list(df.columns)}"
            )
        
        if list(df.columns) != REQUIRED_COLUMNS:
            raise ValueError(
                f"{source} intents schema mismatch: column names or order incorrect\n"
                f"  Expected: {REQUIRED_COLUMNS}\n"
                f"  Got:      {list(df.columns)}"
            )
        
        print(f"  Loaded {len(df)} {source} intents")
        return df
    
    def _join_intents(self, robot_df: pd.DataFrame, analyzer_df: pd.DataFrame) -> pd.DataFrame:
        """
        Join Robot and Analyzer intents.
        
        Join keys: trading_date, stream, direction
        - Full outer join to capture all rows
        - Use indicator to identify Robot-only and Analyzer-only rows
        """
        # Prepare join keys
        robot_df = robot_df.copy()
        analyzer_df = analyzer_df.copy()
        
        # Normalize direction (handle nulls for join)
        robot_df["_join_direction"] = robot_df["direction"].fillna("__NULL__")
        analyzer_df["_join_direction"] = analyzer_df["direction"].fillna("__NULL__")
        
        # Full outer join
        # Note: We join on trading_date, stream, and _join_direction
        # But pandas will keep trading_date and stream without suffixes
        # We'll rename them to add prefixes
        joined_df = pd.merge(
            robot_df,
            analyzer_df,
            on=["trading_date", "stream", "_join_direction"],
            how="outer",
            suffixes=("_robot", "_analyzer"),
            indicator=True
        )
        
        # Rename join key columns to add prefixes
        if "trading_date" in joined_df.columns:
            # Check if both robot and analyzer versions exist
            if "trading_date_robot" not in joined_df.columns:
                # Rename the join key column
                joined_df = joined_df.rename(columns={"trading_date": "trading_date_robot"})
            if "trading_date_analyzer" not in joined_df.columns:
                # Copy robot version to analyzer version for matched rows
                joined_df["trading_date_analyzer"] = joined_df.get("trading_date_robot", joined_df.get("trading_date"))
        
        if "stream" in joined_df.columns:
            if "stream_robot" not in joined_df.columns:
                joined_df = joined_df.rename(columns={"stream": "stream_robot"})
            if "stream_analyzer" not in joined_df.columns:
                joined_df["stream_analyzer"] = joined_df.get("stream_robot", joined_df.get("stream"))
        
        # Clean up join helper columns
        if "_join_direction" in joined_df.columns:
            joined_df = joined_df.drop(columns=["_join_direction"])
        
        return joined_df
    
    def _compute_diffs(self, joined_df: pd.DataFrame) -> pd.DataFrame:
        """Compute price and timestamp diffs."""
        diff_df = joined_df.copy()
        
        # Price diffs (Robot - Analyzer)
        diff_df["entry_diff"] = diff_df["entry_price_robot"] - diff_df["entry_price_analyzer"]
        diff_df["stop_diff"] = diff_df["stop_price_robot"] - diff_df["stop_price_analyzer"]
        diff_df["target_diff"] = diff_df["target_price_robot"] - diff_df["target_price_analyzer"]
        diff_df["be_diff"] = diff_df["be_trigger_robot"] - diff_df["be_trigger_analyzer"]
        
        # Tick-based diffs
        diff_df["entry_diff_ticks"] = None
        diff_df["stop_diff_ticks"] = None
        diff_df["target_diff_ticks"] = None
        diff_df["be_diff_ticks"] = None
        
        # Compute tick diffs where tick size is available
        for idx, row in diff_df.iterrows():
            instrument = row.get("instrument_robot") or row.get("instrument_analyzer")
            if instrument and instrument in TICK_SIZES:
                tick_size = TICK_SIZES[instrument]
                
                # Entry diff in ticks
                if pd.notna(row["entry_diff"]):
                    diff_df.at[idx, "entry_diff_ticks"] = row["entry_diff"] / tick_size
                
                # Stop diff in ticks
                if pd.notna(row["stop_diff"]):
                    diff_df.at[idx, "stop_diff_ticks"] = row["stop_diff"] / tick_size
                
                # Target diff in ticks
                if pd.notna(row["target_diff"]):
                    diff_df.at[idx, "target_diff_ticks"] = row["target_diff"] / tick_size
                
                # BE diff in ticks
                if pd.notna(row["be_diff"]):
                    diff_df.at[idx, "be_diff_ticks"] = row["be_diff"] / tick_size
        
        # Timestamp diff (in seconds)
        diff_df["decision_ts_diff_seconds"] = None
        
        for idx, row in diff_df.iterrows():
            robot_ts = row.get("decision_ts_utc_robot")
            analyzer_ts = row.get("decision_ts_utc_analyzer")
            
            if pd.notna(robot_ts) and pd.notna(analyzer_ts):
                try:
                    robot_dt = pd.to_datetime(robot_ts)
                    analyzer_dt = pd.to_datetime(analyzer_ts)
                    diff_seconds = (robot_dt - analyzer_dt).total_seconds()
                    diff_df.at[idx, "decision_ts_diff_seconds"] = diff_seconds
                except Exception:
                    pass
        
        return diff_df
    
    def _classify_parity_status(self, diff_df: pd.DataFrame) -> pd.DataFrame:
        """Classify parity status for each row."""
        statuses = []
        
        for idx, row in diff_df.iterrows():
            # Check join indicator
            merge_indicator = row.get("_merge", "")
            
            if merge_indicator == "left_only":
                statuses.append("ROBOT_ONLY")
                continue
            elif merge_indicator == "right_only":
                statuses.append("ANALYZER_ONLY")
                continue
            
            # Both present (both or left_only/right_only not set) - check for matches/mismatches
            # Check direction match (should match if join key is correct, but verify)
            robot_dir = row.get("direction_robot")
            analyzer_dir = row.get("direction_analyzer")
            
            if robot_dir != analyzer_dir:
                statuses.append("HARD_MISMATCH")
                continue
            
            # Check for missing required fields
            has_robot_entry = pd.notna(row.get("entry_price_robot"))
            has_analyzer_entry = pd.notna(row.get("entry_price_analyzer"))
            
            if not has_robot_entry or not has_analyzer_entry:
                statuses.append("HARD_MISMATCH")
                continue
            
            # Check price diffs (strict - no tolerance)
            entry_diff = row.get("entry_diff")
            stop_diff = row.get("stop_diff")
            target_diff = row.get("target_diff")
            be_diff = row.get("be_diff")
            
            # Check if all diffs are exactly zero (or both null)
            entry_match = (pd.isna(entry_diff) or abs(entry_diff) < 1e-10)
            stop_match = (pd.isna(stop_diff) or abs(stop_diff) < 1e-10)
            target_match = (pd.isna(target_diff) or abs(target_diff) < 1e-10)
            be_match = (pd.isna(be_diff) or abs(be_diff) < 1e-10)
            
            if entry_match and stop_match and target_match and be_match:
                statuses.append("MATCH")
            else:
                statuses.append("HARD_MISMATCH")
        
        diff_df["parity_status"] = statuses
        
        # Remove merge indicator column
        if "_merge" in diff_df.columns:
            diff_df = diff_df.drop(columns=["_merge"])
        
        return diff_df
    
    def _generate_summary(self, robot_df: pd.DataFrame, analyzer_df: pd.DataFrame, diff_df: pd.DataFrame) -> Dict[str, Any]:
        """Generate summary statistics."""
        total_robot = len(robot_df)
        total_analyzer = len(analyzer_df)
        
        matched = len(diff_df[diff_df["parity_status"] == "MATCH"])
        robot_only = len(diff_df[diff_df["parity_status"] == "ROBOT_ONLY"])
        analyzer_only = len(diff_df[diff_df["parity_status"] == "ANALYZER_ONLY"])
        hard_mismatch = len(diff_df[diff_df["parity_status"] == "HARD_MISMATCH"])
        tolerance_warning = len(diff_df[diff_df["parity_status"] == "TOLERANCE_WARNING"])
        
        # Ensure numeric types for diff columns
        for col in ["entry_diff", "stop_diff", "target_diff", "be_diff", "decision_ts_diff_seconds"]:
            if col in diff_df.columns:
                diff_df[col] = pd.to_numeric(diff_df[col], errors="coerce")
        
        # Count mismatch reasons
        entry_diff_nonzero = len(diff_df[(diff_df["parity_status"] == "HARD_MISMATCH") & 
                                         (diff_df["entry_diff"].notna()) & 
                                         (diff_df["entry_diff"].abs() > 1e-10)])
        stop_diff_nonzero = len(diff_df[(diff_df["parity_status"] == "HARD_MISMATCH") & 
                                        (diff_df["stop_diff"].notna()) & 
                                        (diff_df["stop_diff"].abs() > 1e-10)])
        target_diff_nonzero = len(diff_df[(diff_df["parity_status"] == "HARD_MISMATCH") & 
                                          (diff_df["target_diff"].notna()) & 
                                          (diff_df["target_diff"].abs() > 1e-10)])
        be_diff_nonzero = len(diff_df[(diff_df["parity_status"] == "HARD_MISMATCH") & 
                                      (diff_df["be_diff"].notna()) & 
                                      (diff_df["be_diff"].abs() > 1e-10)])
        ts_diff_nonzero = len(diff_df[(diff_df["parity_status"] == "HARD_MISMATCH") & 
                                      (diff_df["decision_ts_diff_seconds"].notna()) & 
                                      (diff_df["decision_ts_diff_seconds"].abs() > 1e-6)])
        
        return {
            "total_robot": total_robot,
            "total_analyzer": total_analyzer,
            "matched": matched,
            "robot_only": robot_only,
            "analyzer_only": analyzer_only,
            "hard_mismatch": hard_mismatch,
            "tolerance_warning": tolerance_warning,
            "entry_diff_nonzero": entry_diff_nonzero,
            "stop_diff_nonzero": stop_diff_nonzero,
            "target_diff_nonzero": target_diff_nonzero,
            "be_diff_nonzero": be_diff_nonzero,
            "ts_diff_nonzero": ts_diff_nonzero
        }
    
    def _write_summary(self, summary: Dict[str, Any]):
        """Write markdown summary."""
        lines = [
            "# Parity Diff Summary",
            "",
            f"**Run ID**: {self.run_id}",
            f"**Generated**: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}",
            "",
            "## Overview",
            "",
            f"- **Total Robot Intents**: {summary['total_robot']}",
            f"- **Total Analyzer Intents**: {summary['total_analyzer']}",
            "",
            "## Parity Status",
            "",
            f"- **MATCH**: {summary['matched']}",
            f"- **ROBOT_ONLY**: {summary['robot_only']}",
            f"- **ANALYZER_ONLY**: {summary['analyzer_only']}",
            f"- **HARD_MISMATCH**: {summary['hard_mismatch']}",
            f"- **TOLERANCE_WARNING**: {summary['tolerance_warning']}",
            "",
            "## Top Mismatch Reasons",
            "",
            "Counts of non-zero diffs in HARD_MISMATCH rows:",
            "",
            f"- **entry_diff ≠ 0**: {summary['entry_diff_nonzero']}",
            f"- **stop_diff ≠ 0**: {summary['stop_diff_nonzero']}",
            f"- **target_diff ≠ 0**: {summary['target_diff_nonzero']}",
            f"- **be_diff ≠ 0**: {summary['be_diff_nonzero']}",
            f"- **timestamp_diff ≠ 0**: {summary['ts_diff_nonzero']}",
            "",
            "---",
            "",
            "*This is a ground truth comparison with no tolerance applied.*"
        ]
        
        self.summary_path.write_text("\n".join(lines), encoding="utf-8")


def main():
    """CLI entry point."""
    parser = argparse.ArgumentParser(description="Compare Analyzer and Robot intents")
    parser.add_argument("--run-id", type=str, required=True, help="Parity run ID")
    parser.add_argument("--project-root", type=Path, help="Project root directory")
    
    args = parser.parse_args()
    
    diff_tool = ParityDiff(args.run_id, args.project_root)
    summary = diff_tool.run()
    
    print("\nSummary:")
    print(f"  Matched: {summary['matched']}")
    print(f"  Robot-only: {summary['robot_only']}")
    print(f"  Analyzer-only: {summary['analyzer_only']}")
    print(f"  Hard mismatches: {summary['hard_mismatch']}")


if __name__ == "__main__":
    main()
