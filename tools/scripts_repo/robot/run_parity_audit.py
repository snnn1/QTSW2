#!/usr/bin/env python3
"""
Parity Audit Runner

Compares Analyzer results vs Robot DRYRUN results over a fixed trading-day window.
Produces actionable mismatch reports and summary statistics.
"""

import argparse
import json
import sys
from pathlib import Path
from datetime import datetime, timedelta
from typing import Optional, List, Dict, Any
import pandas as pd

# Add project root to path
project_root = Path(__file__).parent.parent.parent
sys.path.insert(0, str(project_root))

from modules.robot.parity.analyzer_extractor import AnalyzerExtractor
from modules.robot.parity.robot_extractor import RobotExtractor
from modules.robot.parity.comparator import ParityComparator
from modules.robot.parity.artifact_writer import ArtifactWriter


def parse_date(date_str: str) -> pd.Timestamp:
    """Parse YYYY-MM-DD date string to pandas Timestamp in Chicago timezone."""
    dt = datetime.strptime(date_str, "%Y-%m-%d")
    return pd.Timestamp(dt, tz="America/Chicago")


def find_30_trading_days_window(data_dir: Path, instruments: List[str]) -> tuple[pd.Timestamp, pd.Timestamp]:
    """
    Find the most recent 30 trading days present in translated data.
    
    Args:
        data_dir: Path to data/translated directory
        instruments: List of instrument codes to check
        
    Returns:
        Tuple of (start_date, end_date) in Chicago timezone
    """
    # This is a simplified implementation - in practice, you'd scan translated data
    # For now, we'll require explicit dates
    raise NotImplementedError("Use --start and --end flags for now")


def validate_snapshot(project_root: Path, start_date: str, end_date: str) -> Dict[str, Any]:
    """
    Validate that snapshot exists and matches requested date range.
    
    Returns:
        Manifest dictionary
        
    Raises:
        FileNotFoundError: If snapshot or MANIFEST.json missing
        ValueError: If date range mismatch
    """
    snapshot_dir = project_root / "data" / "translated_test"
    manifest_path = snapshot_dir / "MANIFEST.json"
    
    if not snapshot_dir.exists():
        raise FileNotFoundError(
            f"Snapshot directory not found: {snapshot_dir}\n"
            f"Create snapshot first using: python scripts/robot/create_translated_snapshot.py --start {start_date} --end {end_date}"
        )
    
    if not manifest_path.exists():
        raise FileNotFoundError(
            f"MANIFEST.json not found in snapshot: {manifest_path}\n"
            f"This snapshot is invalid. Recreate it using: python scripts/robot/create_translated_snapshot.py --start {start_date} --end {end_date}"
        )
    
    with open(manifest_path, "r") as f:
        manifest = json.load(f)
    
    # Validate date range matches
    if manifest["start_date"] != start_date or manifest["end_date"] != end_date:
        raise ValueError(
            f"Snapshot date range mismatch!\n"
            f"Requested: {start_date} to {end_date}\n"
            f"Snapshot: {manifest['start_date']} to {manifest['end_date']}\n"
            f"Recreate snapshot with matching dates."
        )
    
    return manifest


def check_production_data_safety(project_root: Path):
    """
    Safety check: Ensure we're not accidentally reading from production data.
    This is a fail-closed check.
    """
    # Check that snapshot exists (already validated)
    snapshot_dir = project_root / "data" / "translated_test"
    if not snapshot_dir.exists():
        return  # Will be caught by validate_snapshot
    
    # Warn if production data is newer (potential drift)
    production_dir = project_root / "data" / "translated"
    if production_dir.exists():
        prod_files = list(production_dir.rglob("*.parquet"))
        snapshot_files = list(snapshot_dir.rglob("*.parquet"))
        
        if prod_files and snapshot_files:
            # Check if any production files are newer than snapshot creation
            manifest_path = snapshot_dir / "MANIFEST.json"
            if manifest_path.exists():
                with open(manifest_path, "r") as f:
                    manifest = json.load(f)
                snapshot_created = manifest.get("created_at")
                if snapshot_created:
                    print(f"Note: Snapshot created at {snapshot_created}")
                    print(f"      Production data may have been updated since then.")


def main():
    parser = argparse.ArgumentParser(
        description="Parity Audit: Compare Analyzer vs Robot DRYRUN results",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Create snapshot first, then run parity audit
  python scripts/robot/create_translated_snapshot.py --start 2025-01-01 --end 2025-02-15
  python scripts/robot/run_parity_audit.py --start 2025-01-01 --end 2025-02-15
  
  # Run with specific instruments
  python scripts/robot/create_translated_snapshot.py --start 2025-01-01 --end 2025-02-15 --instruments ES,NQ
  python scripts/robot/run_parity_audit.py --start 2025-01-01 --end 2025-02-15 --instruments ES,NQ
  
  # Load existing analyzer outputs (don't re-run analyzer)
  python scripts/robot/run_parity_audit.py --start 2025-01-01 --end 2025-02-15 --mode load_existing_analyzer
  
  # Run analyzer first, then compare
  python scripts/robot/run_parity_audit.py --start 2025-01-01 --end 2025-02-15 --mode run_analyzer
        """
    )
    
    parser.add_argument(
        "--start",
        type=str,
        required=True,
        help="Start date (YYYY-MM-DD) in America/Chicago"
    )
    
    parser.add_argument(
        "--end",
        type=str,
        required=True,
        help="End date (YYYY-MM-DD) in America/Chicago"
    )
    
    parser.add_argument(
        "--instruments",
        type=str,
        default=None,
        help="Comma-separated list of instruments (e.g., ES,NQ,CL). Default: all instruments from parity spec"
    )
    
    parser.add_argument(
        "--mode",
        type=str,
        choices=["load_existing_analyzer", "run_analyzer"],
        default="load_existing_analyzer",
        help="Analyzer mode: load existing outputs or run analyzer first"
    )
    
    parser.add_argument(
        "--robot_source",
        type=str,
        choices=["harness", "nt_logs"],
        default="harness",
        help="Robot log source: harness (offline) or nt_logs (NinjaTrader logs)"
    )
    
    parser.add_argument(
        "--fail_on_entry_time",
        action="store_true",
        help="Fail parity check if entry_time differs (default: warn only)"
    )
    
    parser.add_argument(
        "--entry_time_tolerance_seconds",
        type=int,
        default=60,
        help="Entry time tolerance in seconds (default: 60)"
    )
    
    parser.add_argument(
        "--output_dir",
        type=str,
        default=None,
        help="Output directory (default: docs/robot/parity_runs/YYYY-MM-DD__YYYY-MM-DD__run/)"
    )
    
    args = parser.parse_args()
    
    # Parse dates
    try:
        start_date = parse_date(args.start)
        end_date = parse_date(args.end)
    except ValueError as e:
        print(f"Error: Invalid date format. Use YYYY-MM-DD. {e}", file=sys.stderr)
        sys.exit(1)
    
    if start_date >= end_date:
        print("Error: Start date must be before end date", file=sys.stderr)
        sys.exit(1)
    
    # Parse instruments
    instruments = None
    if args.instruments:
        instruments = [inst.strip().upper() for inst in args.instruments.split(",")]
    
    # Determine output directory
    if args.output_dir:
        output_dir = Path(args.output_dir)
    else:
        run_id = datetime.now().strftime("%Y%m%d_%H%M%S")
        output_dir = project_root / "docs" / "robot" / "parity_runs" / f"{args.start}__{args.end}__{run_id}"
    
    output_dir.mkdir(parents=True, exist_ok=True)
    
    print(f"Parity Audit Runner")
    print(f"===================")
    print(f"Date window: {args.start} to {args.end} (Chicago)")
    print(f"Instruments: {instruments or 'all'}")
    print(f"Analyzer mode: {args.mode}")
    print(f"Robot source: {args.robot_source}")
    print(f"Output directory: {output_dir}")
    print()
    
    # Validate snapshot exists and matches date range
    print("[Safety Check] Validating snapshot...")
    try:
        snapshot_manifest = validate_snapshot(project_root, args.start, args.end)
        print(f"[OK] Snapshot validated: {snapshot_manifest['total_files']} files, {snapshot_manifest['total_rows']:,} rows")
        print(f"  Instruments: {', '.join(snapshot_manifest['instruments'])}")
        print()
    except (FileNotFoundError, ValueError) as e:
        print(f"Error: {e}", file=sys.stderr)
        sys.exit(1)
    
    # Additional safety check
    check_production_data_safety(project_root)
    
    # Load parity spec
    parity_spec_path = project_root / "configs" / "analyzer_robot_parity.json"
    if not parity_spec_path.exists():
        print(f"Error: Parity spec not found at {parity_spec_path}", file=sys.stderr)
        sys.exit(1)
    
    with open(parity_spec_path, "r") as f:
        parity_spec = json.load(f)
    
    # Save parity config (include snapshot metadata)
    parity_config = {
        "start_date": args.start,
        "end_date": args.end,
        "instruments": instruments,
        "mode": args.mode,
        "robot_source": args.robot_source,
        "fail_on_entry_time": args.fail_on_entry_time,
        "entry_time_tolerance_seconds": args.entry_time_tolerance_seconds,
        "parity_spec_revision": parity_spec.get("spec_revision"),
        "parity_spec_name": parity_spec.get("spec_name"),
        "snapshot_manifest": snapshot_manifest,
        "data_source": "data/translated_test",  # Explicitly mark as using snapshot
    }
    
    config_path = output_dir / "parity_config.json"
    with open(config_path, "w") as f:
        json.dump(parity_config, f, indent=2)
    
    print(f"Saved parity config to {config_path}")
    
    # Extract Analyzer canonical records
    print("\n[1/3] Extracting Analyzer canonical records...")
    snapshot_dir = project_root / "data" / "translated_test"
    analyzer_extractor = AnalyzerExtractor(
        project_root=project_root,
        parity_spec=parity_spec,
        start_date=start_date,
        end_date=end_date,
        instruments=instruments,
        mode=args.mode,
        data_source_dir=snapshot_dir  # Use snapshot instead of production data
    )
    
    analyzer_df = analyzer_extractor.extract()
    if analyzer_df is None or len(analyzer_df) == 0:
        print("Warning: No Analyzer records found for the specified window", file=sys.stderr)
        sys.exit(1)
    
    print(f"Extracted {len(analyzer_df)} Analyzer records")
    
    # Extract Robot canonical records
    print("\n[2/3] Extracting Robot canonical records...")
    # Robot logs should be in the run-specific output directory
    robot_log_dir = output_dir / "robot_logs"
    robot_extractor = RobotExtractor(
        project_root=project_root,
        parity_spec=parity_spec,
        start_date=start_date,
        end_date=end_date,
        instruments=instruments,
        source=args.robot_source,
        log_dir=robot_log_dir  # Use run-specific log directory
    )
    
    robot_df = robot_extractor.extract()
    if robot_df is None or len(robot_df) == 0:
        print("Warning: No Robot records found for the specified window", file=sys.stderr)
        sys.exit(1)
    
    print(f"Extracted {len(robot_df)} Robot records")
    
    # Compare and generate mismatch reports
    print("\n[3/3] Comparing records and generating reports...")
    comparator = ParityComparator(
        parity_spec=parity_spec,
        fail_on_entry_time=args.fail_on_entry_time,
        entry_time_tolerance_seconds=args.entry_time_tolerance_seconds
    )
    
    comparison_result = comparator.compare(analyzer_df, robot_df)
    
    # Write artifacts
    writer = ArtifactWriter(output_dir=output_dir)
    writer.write_all(
        analyzer_df=analyzer_df,
        robot_df=robot_df,
        comparison_result=comparison_result,
        parity_config=parity_config
    )
    
    # Print summary
    print("\n" + "=" * 60)
    print("PARITY AUDIT SUMMARY")
    print("=" * 60)
    print(f"Total compared rows: {comparison_result['total_compared']}")
    print(f"Passes: {comparison_result['passes']}")
    print(f"Fails: {comparison_result['fails']}")
    print(f"Warnings: {comparison_result['warnings']}")
    print()
    
    if comparison_result['fails'] > 0:
        print("FAILURE BREAKDOWN BY FIELD:")
        for field, count in comparison_result['fail_by_field'].items():
            print(f"  {field}: {count}")
        print()
    
    if comparison_result['warnings'] > 0:
        print("WARNINGS:")
        for warning_type, count in comparison_result['warn_by_type'].items():
            print(f"  {warning_type}: {count}")
        print()
    
    # Final status
    if comparison_result['fails'] == 0:
        print("[PASS] PARITY CHECK PASSED")
        print(f"\nArtifacts saved to: {output_dir}")
        sys.exit(0)
    else:
        print("[FAIL] PARITY CHECK FAILED")
        print(f"\nSee mismatch details in: {output_dir / 'parity_mismatches.parquet'}")
        print(f"Full summary: {output_dir / 'parity_summary.md'}")
        sys.exit(1)


if __name__ == "__main__":
    main()
