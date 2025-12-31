"""
Streamlit application launcher endpoints
"""
import subprocess
import logging
import sys
from pathlib import Path
from fastapi import APIRouter, HTTPException
from typing import Dict

router = APIRouter(prefix="/api/apps", tags=["apps"])
logger = logging.getLogger(__name__)

# Get project root (assuming this file is in dashboard/backend/routers/)
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent

# Streamlit app scripts
TRANSLATOR_APP = QTSW2_ROOT / "scripts" / "translate_raw_app.py"
ANALYZER_APP = QTSW2_ROOT / "modules" / "analyzer" / "analyzer_app" / "app.py"
SEQUENTIAL_APP = QTSW2_ROOT / "sequential_processor" / "sequential_processor_app.py"

# Parallel analyzer script (the one used in the pipeline)
PARALLEL_ANALYZER_SCRIPT = QTSW2_ROOT / "tools" / "run_analyzer_parallel.py"

# React app (matrix timetable app)
MATRIX_APP_DIR = QTSW2_ROOT / "matrix_timetable_app" / "frontend"


@router.post("/translator/start")
async def start_translator_app() -> Dict:
    """Start the translator Streamlit app"""
    try:
        if not TRANSLATOR_APP.exists():
            raise HTTPException(status_code=404, detail="Translator app not found")
        
        subprocess.Popen(
            ["streamlit", "run", str(TRANSLATOR_APP)],
            cwd=str(QTSW2_ROOT)
        )
        return {"status": "started", "app": "translator"}
    except Exception as e:
        logger.error(f"Failed to start translator app: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@router.post("/analyzer/start")
async def start_analyzer_app() -> Dict:
    """Start the parallel analyzer script (same as used in pipeline)"""
    try:
        if not PARALLEL_ANALYZER_SCRIPT.exists():
            raise HTTPException(status_code=404, detail=f"Parallel analyzer script not found at {PARALLEL_ANALYZER_SCRIPT}")
        
        # Detect available instruments from translated data
        translated_dir = QTSW2_ROOT / "data" / "translated"
        instruments = set()
        
        if translated_dir.exists():
            # Look for instrument directories (e.g., data/translated/ES/, data/translated/NQ/)
            for item in translated_dir.iterdir():
                if item.is_dir() and len(item.name) <= 4:
                    # Check if it has parquet files
                    parquet_files = list(item.rglob("*.parquet"))
                    if parquet_files:
                        instruments.add(item.name.upper())
        
        if not instruments:
            # Fallback: try data/processed or data/data_processed
            for alt_dir in [QTSW2_ROOT / "data" / "processed", QTSW2_ROOT / "data" / "data_processed"]:
                if alt_dir.exists():
                    for item in alt_dir.iterdir():
                        if item.is_dir() and len(item.name) <= 4:
                            parquet_files = list(item.rglob("*.parquet"))
                            if parquet_files:
                                instruments.add(item.name.upper())
        
        if not instruments:
            raise HTTPException(
                status_code=400,
                detail="No instruments found in translated/processed data. Please run the translator first."
            )
        
        instruments_list = sorted(list(instruments))
        logger.info(f"Starting parallel analyzer for instruments: {', '.join(instruments_list)}")
        
        # Build command for parallel analyzer
        analyzer_cmd = [
            sys.executable,
            str(PARALLEL_ANALYZER_SCRIPT),
            "--folder", str(translated_dir if translated_dir.exists() else QTSW2_ROOT / "data" / "data_processed"),
            "--instruments"
        ] + instruments_list
        
        # Start the analyzer process
        process = subprocess.Popen(
            analyzer_cmd,
            cwd=str(QTSW2_ROOT),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True
        )
        
        logger.info(f"Started parallel analyzer process (PID: {process.pid}) for {len(instruments_list)} instrument(s)")
        
        return {
            "status": "started",
            "app": "analyzer",
            "instruments": instruments_list,
            "process_id": process.pid
        }
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Failed to start analyzer: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Failed to start analyzer: {str(e)}")


@router.post("/sequential/start")
async def start_sequential_app() -> Dict:
    """Start the sequential processor Streamlit app"""
    try:
        if not SEQUENTIAL_APP.exists():
            raise HTTPException(status_code=404, detail="Sequential app not found")
        
        subprocess.Popen(
            ["streamlit", "run", str(SEQUENTIAL_APP)],
            cwd=str(QTSW2_ROOT)
        )
        return {"status": "started", "app": "sequential"}
    except Exception as e:
        logger.error(f"Failed to start sequential app: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@router.post("/matrix/start")
async def start_matrix_app() -> Dict:
    """Start the master matrix React app"""
    try:
        if not MATRIX_APP_DIR.exists():
            raise HTTPException(
                status_code=404,
                detail=f"Matrix app directory not found at {MATRIX_APP_DIR}"
            )
        
        package_json = MATRIX_APP_DIR / "package.json"
        if not package_json.exists():
            raise HTTPException(
                status_code=404,
                detail=f"package.json not found in {MATRIX_APP_DIR}"
            )
        
        # Start the React app using npm
        # On Windows, use shell=True to handle npm properly
        subprocess.Popen(
            ["npm", "run", "dev"],
            cwd=str(MATRIX_APP_DIR),
            shell=True
        )
        
        # Matrix app runs on port 5174 (see vite.config.js)
        return {
            "status": "started",
            "app": "matrix",
            "url": "http://localhost:5174"
        }
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Failed to start matrix app: {e}")
        raise HTTPException(status_code=500, detail=str(e))
