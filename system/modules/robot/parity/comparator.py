"""
Parity Comparator

Compares Analyzer and Robot canonical records and identifies mismatches.
"""

import pandas as pd
import numpy as np
from typing import Dict, Any, List, Optional
from datetime import timedelta


class ParityComparator:
    """Compares Analyzer and Robot records for parity."""
    
    def __init__(
        self,
        parity_spec: Dict[str, Any],
        fail_on_entry_time: bool = False,
        entry_time_tolerance_seconds: int = 60
    ):
        self.parity_spec = parity_spec
        self.fail_on_entry_time = fail_on_entry_time
        self.entry_time_tolerance_seconds = entry_time_tolerance_seconds
    
    def compare(
        self,
        analyzer_df: pd.DataFrame,
        robot_df: pd.DataFrame
    ) -> Dict[str, Any]:
        """
        Compare Analyzer and Robot records.
        
        Args:
            analyzer_df: Analyzer canonical records
            robot_df: Robot canonical records
            
        Returns:
            Dictionary with comparison results:
            - total_compared: int
            - passes: int
            - fails: int
            - warnings: int
            - fail_by_field: Dict[str, int]
            - warn_by_type: Dict[str, int]
            - mismatches: pd.DataFrame
            - warnings_df: pd.DataFrame
        """
        # Join on keys: trading_date, instrument, stream, slot_time
        join_keys = ["trading_date", "instrument", "stream", "slot_time"]
        
        # Ensure join keys exist
        for key in join_keys:
            if key not in analyzer_df.columns:
                raise ValueError(f"Missing join key '{key}' in Analyzer dataframe")
            if key not in robot_df.columns:
                raise ValueError(f"Missing join key '{key}' in Robot dataframe")
        
        # Perform left join (keep all analyzer records)
        merged = analyzer_df.merge(
            robot_df,
            on=join_keys,
            how="outer",
            suffixes=("_analyzer", "_robot"),
            indicator=True
        )
        
        # Identify missing records
        analyzer_only = merged[merged["_merge"] == "left_only"]
        robot_only = merged[merged["_merge"] == "right_only"]
        both = merged[merged["_merge"] == "both"]
        
        mismatches = []
        warnings = []
        
        # Compare fields for records present in both
        for _, row in both.iterrows():
            mismatch_flags = {}
            warning_flags = {}
            
            # Compare exact-match fields
            exact_fields = [
                "intended_trade",
                "direction",
                "brk_long_rounded",
                "brk_short_rounded",
                "stop_price",
                "target_price",
                "be_stop_price",
                "be_trigger_pts",
            ]
            
            for field in exact_fields:
                analyzer_val = row.get(f"{field}_analyzer")
                robot_val = row.get(f"{field}_robot")
                
                # Handle NaN/None comparisons
                analyzer_is_nan = pd.isna(analyzer_val) or analyzer_val is None
                robot_is_nan = pd.isna(robot_val) or robot_val is None
                
                if analyzer_is_nan and robot_is_nan:
                    continue  # Both missing, skip
                elif analyzer_is_nan or robot_is_nan:
                    mismatch_flags[field] = "MISSING"
                else:
                    # Numeric comparison with tolerance
                    if isinstance(analyzer_val, (int, float)) and isinstance(robot_val, (int, float)):
                        # Use small tolerance for floating point
                        tolerance = 1e-6
                        if abs(analyzer_val - robot_val) > tolerance:
                            mismatch_flags[field] = f"{analyzer_val} != {robot_val}"
                    elif analyzer_val != robot_val:
                        mismatch_flags[field] = f"{analyzer_val} != {robot_val}"
            
            # Compare entry_time with tolerance
            entry_time_analyzer = row.get("entry_time_chicago_analyzer")
            entry_time_robot = row.get("entry_time_chicago_robot")
            
            if entry_time_analyzer is not None and entry_time_robot is not None:
                if isinstance(entry_time_analyzer, pd.Timestamp) and isinstance(entry_time_robot, pd.Timestamp):
                    time_diff = abs((entry_time_analyzer - entry_time_robot).total_seconds())
                    
                    if time_diff > self.entry_time_tolerance_seconds:
                        if self.fail_on_entry_time:
                            mismatch_flags["entry_time"] = f"Delta: {time_diff:.0f}s"
                        else:
                            warning_flags["entry_time"] = f"Delta: {time_diff:.0f}s"
            
            # Compare entry_price (may differ due to fill assumptions)
            entry_price_analyzer = row.get("entry_price_analyzer")
            entry_price_robot = row.get("entry_price_robot")
            
            if entry_price_analyzer is not None and entry_price_robot is not None:
                price_diff = abs(entry_price_analyzer - entry_price_robot)
                # If prices differ but trigger levels match, it's a warning (fill assumption)
                brk_long_match = (
                    row.get("brk_long_rounded_analyzer") == row.get("brk_long_rounded_robot")
                )
                brk_short_match = (
                    row.get("brk_short_rounded_analyzer") == row.get("brk_short_rounded_robot")
                )
                
                if price_diff > 1e-6 and (brk_long_match or brk_short_match):
                    warning_flags["entry_price"] = f"Fill assumption diff: {price_diff:.4f}"
            
            # Record mismatches
            if mismatch_flags:
                mismatch_row = {
                    "trading_date": row["trading_date"],
                    "instrument": row["instrument"],
                    "stream": row["stream"],
                    "slot_time": row["slot_time"],
                }
                
                # Add analyzer values
                for field in exact_fields + ["entry_time_chicago", "entry_price"]:
                    analyzer_col = f"{field}_analyzer"
                    if analyzer_col in row:
                        mismatch_row[f"analyzer_{field}"] = row[analyzer_col]
                
                # Add robot values
                for field in exact_fields + ["entry_time_chicago", "entry_price"]:
                    robot_col = f"{field}_robot"
                    if robot_col in row:
                        mismatch_row[f"robot_{field}"] = row[robot_col]
                
                # Add mismatch flags
                for field, reason in mismatch_flags.items():
                    mismatch_row[f"mismatch_{field}"] = reason
                
                mismatches.append(mismatch_row)
            
            # Record warnings
            if warning_flags:
                warning_row = {
                    "trading_date": row["trading_date"],
                    "instrument": row["instrument"],
                    "stream": row["stream"],
                    "slot_time": row["slot_time"],
                }
                
                for field, reason in warning_flags.items():
                    warning_row[f"warning_{field}"] = reason
                
                warnings.append(warning_row)
        
        # Handle missing records
        for _, row in analyzer_only.iterrows():
            mismatches.append({
                "trading_date": row["trading_date"],
                "instrument": row["instrument"],
                "stream": row["stream"],
                "slot_time": row["slot_time"],
                "mismatch_status": "MISSING_IN_ROBOT",
            })
        
        for _, row in robot_only.iterrows():
            mismatches.append({
                "trading_date": row["trading_date"],
                "instrument": row["instrument"],
                "stream": row["stream"],
                "slot_time": row["slot_time"],
                "mismatch_status": "MISSING_IN_ANALYZER",
            })
        
        # Count failures by field
        fail_by_field = {}
        for mismatch in mismatches:
            for key, value in mismatch.items():
                if key.startswith("mismatch_") and value:
                    field = key.replace("mismatch_", "")
                    fail_by_field[field] = fail_by_field.get(field, 0) + 1
        
        # Count warnings by type
        warn_by_type = {}
        for warning in warnings:
            for key, value in warning.items():
                if key.startswith("warning_") and value:
                    warn_type = key.replace("warning_", "")
                    warn_by_type[warn_type] = warn_by_type.get(warn_type, 0) + 1
        
        # Create DataFrames
        mismatches_df = pd.DataFrame(mismatches) if mismatches else pd.DataFrame()
        warnings_df = pd.DataFrame(warnings) if warnings else pd.DataFrame()
        
        # Calculate totals
        total_compared = len(both)
        passes = total_compared - len(mismatches)
        fails = len(mismatches)
        warnings_count = len(warnings)
        
        return {
            "total_compared": total_compared,
            "passes": passes,
            "fails": fails,
            "warnings": warnings_count,
            "fail_by_field": fail_by_field,
            "warn_by_type": warn_by_type,
            "mismatches": mismatches_df,
            "warnings": warnings_df,
            "analyzer_only_count": len(analyzer_only),
            "robot_only_count": len(robot_only),
        }
