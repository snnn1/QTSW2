"""
Pipeline metrics endpoints
"""
import logging
from pathlib import Path
from fastapi import APIRouter, HTTPException
from typing import List, Dict

router = APIRouter(prefix="/api/metrics", tags=["metrics"])
logger = logging.getLogger(__name__)

# Get project root
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent


@router.get("/list-files")
async def get_metrics_files() -> Dict:
    """Get list of available metrics files (renamed from /files to avoid conflict with file counts endpoint)"""
    # Use DEBUG level to reduce log noise (polling endpoint)
    logger.debug("Metrics files requested")
    try:
        metrics_dir = QTSW2_ROOT / "data" / "analyzer_runs"
        if not metrics_dir.exists():
            return {"files": []}
        
        # Get all parquet files
        files = list(metrics_dir.rglob("*.parquet"))
        files.sort(key=lambda f: f.stat().st_mtime, reverse=True)
        
        return {
            "files": [str(f.relative_to(QTSW2_ROOT)) for f in files[:20]]  # Last 20 files
        }
    except Exception as e:
        logger.error(f"Failed to get metrics files: {e}")
        raise HTTPException(status_code=500, detail=str(e))
