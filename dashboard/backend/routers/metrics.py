"""
Metrics endpoints
"""
from pathlib import Path
from fastapi import APIRouter

from ..config import QTSW2_ROOT

router = APIRouter(prefix="/api/metrics", tags=["metrics"])


@router.get("/files")
async def get_file_counts():
    """Get file counts from data directories."""
    # These paths should match your actual data directories
    data_raw = QTSW2_ROOT / "data" / "raw"  # Fixed: QTSW2/data/raw (where DataExporter writes)
    data_processed = QTSW2_ROOT / "data" / "processed"  # QTSW2/data/processed (where files should go)
    analyzer_runs = QTSW2_ROOT / "data" / "analyzer_runs"  # Analyzer output folder
    
    # Count raw CSV files (exclude subdirectories like logs folder)
    raw_count = 0
    if data_raw.exists():
        raw_files = list(data_raw.glob("*.csv"))
        raw_count = len([f for f in raw_files if f.parent == data_raw])
    
    # Count processed files
    processed_count = 0
    if data_processed.exists():
        processed_files = list(data_processed.glob("*.parquet"))
        processed_files.extend(list(data_processed.glob("*.csv")))
        processed_count = len([f for f in processed_files if f.parent == data_processed])
    
    # Count analyzed files (monthly consolidated files in instrument/year folders)
    analyzed_count = 0
    if analyzer_runs.exists():
        # Count monthly parquet files in instrument/year subfolders
        analyzed_files = list(analyzer_runs.rglob("*.parquet"))
        analyzed_count = len(analyzed_files)
    
    return {
        "raw_files": raw_count,
        "processed_files": processed_count,
        "analyzed_files": analyzed_count
    }



