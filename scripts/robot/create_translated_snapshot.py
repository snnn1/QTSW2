#!/usr/bin/env python3
"""
Create Translated Data Test Snapshot

Creates a read-only snapshot of translated data for parity audit testing.
Copies files whose timestamps fall within a Chicago-date window.
"""

import argparse
import json
import shutil
import sys
from pathlib import Path
from datetime import datetime
from typing import List, Set, Dict, Any, Optional
import pandas as pd
import pytz


def get_commit_hash(project_root: Path) -> Optional[str]:
    """Get git commit hash if available."""
    try:
        import subprocess
        result = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=project_root,
            capture_output=True,
            text=True,
            check=True
        )
        return result.stdout.strip()
    except:
        return None


def extract_date_from_filename(filename: str) -> Optional[str]:
    """
    Extract date from filename like ES_1m_2025-12-15.parquet
    
    Returns YYYY-MM-DD string or None
    """
    try:
        # Pattern: {instrument}_1m_{date}.parquet
        parts = filename.replace(".parquet", "").split("_")
        if len(parts) >= 3 and parts[1] == "1m":
            date_str = parts[2]
            # Validate date format
            datetime.strptime(date_str, "%Y-%m-%d")
            return date_str
    except:
        pass
    return None


def get_file_date_range(file_path: Path, chicago_tz: pytz.timezone) -> tuple[Optional[pd.Timestamp], Optional[pd.Timestamp]]:
    """
    Get date range from file by reading parquet data.
    
    Returns (min_date, max_date) in Chicago timezone, or (None, None) if cannot determine.
    """
    try:
        df = pd.read_parquet(file_path)
        
        if "timestamp" not in df.columns:
            return None, None
        
        # Convert timestamps to Chicago timezone
        timestamps = pd.to_datetime(df["timestamp"])
        if timestamps.dt.tz is None:
            timestamps = timestamps.dt.tz_localize("UTC")
        timestamps = timestamps.dt.tz_convert(chicago_tz)
        
        # Get date range
        min_date = timestamps.min().date()
        max_date = timestamps.max().date()
        
        return pd.Timestamp(min_date, tz=chicago_tz), pd.Timestamp(max_date, tz=chicago_tz)
    except Exception as e:
        print(f"Warning: Could not read date range from {file_path}: {e}")
        return None, None


def should_include_file(
    file_path: Path,
    start_date: pd.Timestamp,
    end_date: pd.Timestamp,
    chicago_tz: pytz.timezone
) -> bool:
    """
    Determine if file should be included in snapshot based on date range.
    
    Checks both filename date and actual data timestamps.
    """
    # First check filename date
    filename_date_str = extract_date_from_filename(file_path.name)
    if filename_date_str:
        try:
            file_date = pd.Timestamp(filename_date_str, tz=chicago_tz)
            if start_date <= file_date <= end_date:
                return True
        except:
            pass
    
    # If filename check fails, check actual data timestamps
    min_date, max_date = get_file_date_range(file_path, chicago_tz)
    if min_date is not None and max_date is not None:
        # Include if any part of the file overlaps the date range
        return not (max_date < start_date or min_date > end_date)
    
    # If we can't determine, exclude for safety
    return False


def create_snapshot(
    project_root: Path,
    start_date: str,
    end_date: str,
    instruments: List[str] = None
) -> Dict[str, Any]:
    """
    Create a snapshot of translated data for the given date range.
    
    Args:
        project_root: Project root directory
        start_date: Start date (YYYY-MM-DD) in Chicago timezone
        end_date: End date (YYYY-MM-DD) in Chicago timezone
        instruments: List of instruments to include (None = all)
        
    Returns:
        Manifest dictionary
    """
    chicago_tz = pytz.timezone("America/Chicago")
    
    # Parse dates
    start_ts = pd.Timestamp(start_date, tz=chicago_tz)
    end_ts = pd.Timestamp(end_date, tz=chicago_tz)
    
    if start_ts >= end_ts:
        raise ValueError("Start date must be before end date")
    
    source_dir = project_root / "data" / "translated"
    snapshot_dir = project_root / "data" / "translated_test"
    
    if not source_dir.exists():
        raise FileNotFoundError(f"Source directory not found: {source_dir}")
    
    # Create snapshot directory
    snapshot_dir.mkdir(parents=True, exist_ok=True)
    
    # Find all parquet files in source
    source_files = list(source_dir.rglob("*.parquet"))
    
    if not source_files:
        raise ValueError(f"No parquet files found in {source_dir}")
    
    # Filter files by date range and instruments
    files_to_copy = []
    instruments_found = set()
    total_rows = 0
    
    for source_file in source_files:
        # Extract instrument from path: data/translated/{instrument}/1m/...
        rel_path = source_file.relative_to(source_dir)
        path_parts = rel_path.parts
        if len(path_parts) < 1:
            continue
        
        instrument = path_parts[0]
        
        # Filter by instruments if specified
        if instruments and instrument not in instruments:
            continue
        
        # Check if file should be included based on date range
        if should_include_file(source_file, start_ts, end_ts, chicago_tz):
            files_to_copy.append((source_file, rel_path))
            instruments_found.add(instrument)
            
            # Count rows (optional, may be slow for large files)
            try:
                df = pd.read_parquet(source_file)
                total_rows += len(df)
            except:
                pass
    
    if not files_to_copy:
        raise ValueError(f"No files found in date range {start_date} to {end_date}")
    
    # Copy files preserving directory structure
    files_copied = 0
    for source_file, rel_path in files_to_copy:
        dest_file = snapshot_dir / rel_path
        dest_file.parent.mkdir(parents=True, exist_ok=True)
        
        # Copy file (preserve metadata)
        shutil.copy2(source_file, dest_file)
        files_copied += 1
    
    # Create manifest
    commit_hash = get_commit_hash(project_root)
    
    manifest = {
        "start_date": start_date,
        "end_date": end_date,
        "instruments": sorted(list(instruments_found)),
        "total_files": files_copied,
        "total_rows": total_rows,
        "source_path": str(source_dir.relative_to(project_root)),
        "created_at": datetime.now().isoformat(),
        "commit_hash": commit_hash,
        "read_only": True,
        "note": "This snapshot is read-only. Do not modify files in this directory."
    }
    
    # Write manifest
    manifest_path = snapshot_dir / "MANIFEST.json"
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
    
    # Create README
    readme_path = snapshot_dir / "README.md"
    with open(readme_path, "w", encoding="utf-8") as f:
        f.write("# Translated Data Test Snapshot\n\n")
        f.write("**This directory is READ-ONLY.**\n\n")
        f.write("This snapshot contains frozen market data for parity audit testing.\n")
        f.write("Do not modify, delete, or overwrite files in this directory.\n\n")
        f.write(f"**Date Range:** {start_date} to {end_date}\n")
        f.write(f"**Instruments:** {', '.join(sorted(instruments_found))}\n")
        f.write(f"**Files:** {files_copied}\n")
        f.write(f"**Created:** {manifest['created_at']}\n\n")
        f.write("See `MANIFEST.json` for full details.\n")
    
    return manifest


def main():
    parser = argparse.ArgumentParser(
        description="Create a read-only snapshot of translated data for parity audit testing",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Create snapshot for 30 trading days
  python scripts/robot/create_translated_snapshot.py --start 2025-01-01 --end 2025-02-15
  
  # Create snapshot for specific instruments
  python scripts/robot/create_translated_snapshot.py --start 2025-01-01 --end 2025-02-15 --instruments ES,NQ
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
        help="Comma-separated list of instruments (e.g., ES,NQ,CL). Default: all instruments"
    )
    
    args = parser.parse_args()
    
    # Get project root
    project_root = Path(__file__).parent.parent.parent
    
    # Parse instruments
    instruments = None
    if args.instruments:
        instruments = [inst.strip().upper() for inst in args.instruments.split(",")]
    
    try:
        print(f"Creating translated data snapshot...")
        print(f"Date range: {args.start} to {args.end}")
        print(f"Instruments: {instruments or 'all'}")
        print()
        
        manifest = create_snapshot(
            project_root=project_root,
            start_date=args.start,
            end_date=args.end,
            instruments=instruments
        )
        
        print("[OK] Snapshot created successfully!")
        print()
        print("Manifest:")
        print(json.dumps(manifest, indent=2))
        print()
        print(f"Snapshot location: {project_root / 'data' / 'translated_test'}")
        print(f"Manifest: {project_root / 'data' / 'translated_test' / 'MANIFEST.json'}")
        
    except Exception as e:
        print(f"Error: {e}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
