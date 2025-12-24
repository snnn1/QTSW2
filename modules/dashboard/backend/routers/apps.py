"""
Streamlit application launcher endpoints
"""
import subprocess
import logging
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
    """Start the analyzer Streamlit app"""
    try:
        if not ANALYZER_APP.exists():
            raise HTTPException(status_code=404, detail="Analyzer app not found")
        
        subprocess.Popen(
            ["streamlit", "run", str(ANALYZER_APP)],
            cwd=str(QTSW2_ROOT)
        )
        return {"status": "started", "app": "analyzer"}
    except Exception as e:
        logger.error(f"Failed to start analyzer app: {e}")
        raise HTTPException(status_code=500, detail=str(e))


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
