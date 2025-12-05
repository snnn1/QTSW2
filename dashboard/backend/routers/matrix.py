"""
Master Matrix and Timetable endpoints
Note: This is a simplified version - the full implementation is complex and may need to stay in main.py
for now due to module reloading requirements.
"""
from pathlib import Path
from typing import Optional, Dict, List
from datetime import datetime
from fastapi import APIRouter, HTTPException
import logging
import pandas as pd
import numpy as np

from ..config import QTSW2_ROOT

router = APIRouter(prefix="/api", tags=["matrix"])
logger = logging.getLogger(__name__)

# Import models - these are complex and may need to stay in main.py
# For now, we'll keep the endpoints here but they may reference models from main
# This is a placeholder - full implementation would require moving the complex build logic


@router.get("/matrix/files")
async def list_matrix_files():
    """List available master matrix files."""
    try:
        backend_dir = Path(__file__).parent.parent  # dashboard/backend/
        backend_matrix_dir = backend_dir / "data" / "master_matrix"
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        
        parquet_files = []
        if backend_matrix_dir.exists():
            parquet_files.extend(backend_matrix_dir.glob("master_matrix_*.parquet"))
        if root_matrix_dir.exists():
            parquet_files.extend(root_matrix_dir.glob("master_matrix_*.parquet"))
        
        parquet_files = sorted(set(parquet_files), key=lambda p: p.stat().st_mtime, reverse=True)
        
        files = []
        for file_path in parquet_files:
            files.append({
                "name": file_path.name,
                "path": str(file_path),
                "size": file_path.stat().st_size,
                "modified": datetime.fromtimestamp(file_path.stat().st_mtime).isoformat()
            })
        
        return {"files": files[:20]}
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to list matrix files: {str(e)}")


@router.get("/timetable/files")
async def list_timetable_files():
    """List available timetable files."""
    try:
        timetable_dir = QTSW2_ROOT / "data" / "timetable"
        if not timetable_dir.exists():
            return {"files": []}
        
        files = []
        for file_path in sorted(timetable_dir.glob("*.parquet"), key=lambda p: p.stat().st_mtime, reverse=True):
            files.append({
                "name": file_path.name,
                "path": str(file_path),
                "size": file_path.stat().st_size,
                "modified": datetime.fromtimestamp(file_path.stat().st_mtime).isoformat()
            })
        
        return {"files": files[:20]}
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to list timetable files: {str(e)}")



