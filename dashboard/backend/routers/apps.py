"""
Streamlit app launcher endpoints
"""
import os
import subprocess
import asyncio
import logging
from fastapi import APIRouter, HTTPException

from ..config import QTSW2_ROOT, TRANSLATOR_APP, ANALYZER_APP, SEQUENTIAL_APP

router = APIRouter(prefix="/api/apps", tags=["apps"])
logger = logging.getLogger(__name__)


@router.post("/translator/start")
async def start_translator_app():
    """Start the Translator Streamlit app."""
    try:
        # Check if already running by checking if port 8501 is in use
        import socket
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        result = sock.connect_ex(('localhost', 8501))
        sock.close()
        
        if result == 0:
            return {"status": "already_running", "url": "http://localhost:8501"}
        
        # Start Streamlit app in new console window (Windows)
        if os.name == 'nt':
            # Use start command to open in new window
            translator_path = QTSW2_ROOT / "scripts" / "translate_raw_app.py"
            app_path = str(translator_path).replace('/', '\\')
            subprocess.Popen(
                f'start "Translator App" cmd /k "streamlit run \"{app_path}\" --server.port 8501"',
                shell=True,
                cwd=str(QTSW2_ROOT)
            )
        else:
            # Linux/Mac
            subprocess.Popen(
                ["streamlit", "run", str(TRANSLATOR_APP), "--server.port", "8501"],
                cwd=str(QTSW2_ROOT)
            )
        
        # Wait a moment for the app to start
        await asyncio.sleep(3)
        
        return {"status": "started", "url": "http://localhost:8501"}
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to start translator app: {str(e)}")


@router.post("/analyzer/start")
async def start_analyzer_app():
    """Start the Analyzer Streamlit app."""
    try:
        # Check if already running
        import socket
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        result = sock.connect_ex(('localhost', 8502))
        sock.close()
        
        if result == 0:
            return {"status": "already_running", "url": "http://localhost:8502"}
        
        # Start Streamlit app in new console window (Windows)
        if os.name == 'nt':
            analyzer_path = QTSW2_ROOT / "scripts" / "breakout_analyzer" / "analyzer_app" / "app.py"
            app_path = str(analyzer_path).replace('/', '\\')
            subprocess.Popen(
                f'start "Analyzer App" cmd /k "streamlit run \"{app_path}\" --server.port 8502"',
                shell=True,
                cwd=str(QTSW2_ROOT)
            )
        else:
            # Linux/Mac
            subprocess.Popen(
                ["streamlit", "run", str(ANALYZER_APP), "--server.port", "8502"],
                cwd=str(QTSW2_ROOT)
            )
        
        # Wait a moment for the app to start
        await asyncio.sleep(3)
        
        return {"status": "started", "url": "http://localhost:8502"}
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to start analyzer app: {str(e)}")


@router.post("/sequential/start")
async def start_sequential_app():
    """Start the Sequential Processor Streamlit app."""
    try:
        # Check if already running
        import socket
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        result = sock.connect_ex(('localhost', 8503))
        sock.close()
        
        if result == 0:
            return {"status": "already_running", "url": "http://localhost:8503"}
        
        # Start Streamlit app in new console window (Windows)
        if os.name == 'nt':
            sequential_path = QTSW2_ROOT / "sequential_processor" / "sequential_processor_app.py"
            app_path = str(sequential_path).replace('/', '\\')
            subprocess.Popen(
                f'start "Sequential Processor App" cmd /k "streamlit run \"{app_path}\" --server.port 8503"',
                shell=True,
                cwd=str(QTSW2_ROOT)
            )
        else:
            # Linux/Mac
            subprocess.Popen(
                ["streamlit", "run", str(SEQUENTIAL_APP), "--server.port", "8503"],
                cwd=str(QTSW2_ROOT)
            )
        
        # Wait a moment for the app to start
        await asyncio.sleep(3)
        
        return {"status": "started", "url": "http://localhost:8503"}
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to start sequential app: {str(e)}")



