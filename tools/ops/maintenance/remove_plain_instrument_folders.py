#!/usr/bin/env python3
"""
Remove plain instrument folders (CL, ES, etc.) from runs directories.
Only instrument-session folders (CL1, CL2, ES1, ES2, etc.) should exist.
"""

import shutil
from pathlib import Path

# Base paths
BASE_DIR = Path(__file__).resolve().parent.parent
ANALYZER_RUNS_DIR = BASE_DIR / "data" / "analyzer_runs"
SEQUENCER_RUNS_DIR = BASE_DIR / "data" / "sequencer_runs"

# Plain instrument names (without session suffix)
INSTRUMENTS = ["ES", "NQ", "YM", "CL", "GC", "NG"]

def remove_plain_folders(runs_dir: Path, dir_name: str):
    """Remove plain instrument folders from a runs directory."""
    if not runs_dir.exists():
        print(f"{dir_name} directory does not exist: {runs_dir}")
        return
    
    removed = []
    for instrument in INSTRUMENTS:
        folder = runs_dir / instrument
        if folder.exists():
            # Check if folder is empty or has content
            try:
                files = list(folder.rglob('*'))
                file_count = len([f for f in files if f.is_file()])
                dir_count = len([f for f in files if f.is_dir()]) - 1  # Subtract 1 for the folder itself
                
                print(f"Found '{instrument}' folder in {dir_name}: {file_count} files, {dir_count} subdirectories")
                
                # Remove the folder
                shutil.rmtree(folder)
                removed.append(instrument)
                print(f"  -> Removed '{instrument}' folder")
            except Exception as e:
                print(f"  -> Error removing '{instrument}' folder: {e}")
    
    if removed:
        print(f"\nRemoved {len(removed)} plain instrument folder(s) from {dir_name}: {removed}")
    else:
        print(f"\nNo plain instrument folders found in {dir_name}")

if __name__ == "__main__":
    print("=" * 60)
    print("Removing plain instrument folders from runs directories")
    print("=" * 60)
    print()
    
    remove_plain_folders(ANALYZER_RUNS_DIR, "analyzer_runs")
    print()
    remove_plain_folders(SEQUENCER_RUNS_DIR, "sequencer_runs")
    
    print()
    print("=" * 60)
    print("Done!")
    print("=" * 60)



