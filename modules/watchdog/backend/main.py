"""
Watchdog Backend - Separate FastAPI server for trading execution watchdog
"""

import os
import sys
from pathlib import Path
from typing import Optional
from contextlib import asynccontextmanager
import datetime
import logging

from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from fastapi.middleware.gzip import GZipMiddleware
import uvicorn

# Calculate project root
QTSW2_ROOT = Path(__file__).parent.parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

# Global watchdog aggregator instance
watchdog_aggregator_instance = None

logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Lifespan event handler for startup and shutdown."""
    global watchdog_aggregator_instance
    logger.info("Watchdog Backend API started")
    
    # Initialize watchdog aggregator
    print("\n" + "=" * 60)
    print("INITIALIZING WATCHDOG AGGREGATOR")
    print("=" * 60)
    try:
        print("Step 1: Importing watchdog aggregator...")
        logger.info("Initializing Watchdog Aggregator...")
        try:
            from modules.watchdog.aggregator import WatchdogAggregator
        except ImportError:
            import sys
            sys.path.insert(0, str(QTSW2_ROOT))
            from modules.watchdog.aggregator import WatchdogAggregator
        print("   [OK] Imported")
        
        print("Step 2: Creating aggregator instance...")
        watchdog_aggregator_instance = WatchdogAggregator()
        print("   [OK] Instance created")
        
        print("Step 3: Starting aggregator...")
        await watchdog_aggregator_instance.start()
        print("   [OK] Aggregator started successfully!")
        print("=" * 60 + "\n")
        
        # Set aggregator instance in watchdog router
        from .routers import watchdog as watchdog_router
        watchdog_router.aggregator_instance = watchdog_aggregator_instance
        logger.info("Watchdog Aggregator started successfully")
    except Exception as e:
        import traceback
        error_msg = f"Failed to start watchdog aggregator: {e}\nFull traceback:\n{traceback.format_exc()}"
        logger.error(error_msg, exc_info=True)
        print("\n" + "=" * 60)
        print("[WARNING] Watchdog aggregator failed to start!")
        print("=" * 60)
        print(f"Error: {str(e)}")
        print("=" * 60 + "\n")
        watchdog_aggregator_instance = None
    
    yield
    
    # Shutdown
    logger.info("Watchdog Backend API shutting down...")
    if watchdog_aggregator_instance:
        try:
            await watchdog_aggregator_instance.stop()
            logger.info("Watchdog Aggregator stopped")
        except Exception as e:
            logger.error(f"Error stopping watchdog aggregator: {e}")


# Create FastAPI app
app = FastAPI(
    title="Watchdog Backend API",
    description="Trading execution watchdog monitoring backend",
    lifespan=lifespan
)

# CORS middleware
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],  # Allow all origins in development
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Gzip compression middleware
app.add_middleware(GZipMiddleware, minimum_size=1000)

# Import and include watchdog router
try:
    from .routers import watchdog, websocket
except ImportError:
    # Fallback: Add parent directory to path
    import sys
    backend_path = Path(__file__).parent
    if str(backend_path) not in sys.path:
        sys.path.insert(0, str(backend_path))
    try:
        from routers import watchdog, websocket
    except ImportError:
        # Last resort: try absolute import
        from modules.watchdog.backend.routers import watchdog, websocket

app.include_router(watchdog.router)
app.include_router(websocket.router)  # WebSocket endpoint for watchdog events


@app.get("/health")
async def health_check():
    """Health check endpoint"""
    return {
        "status": "healthy",
        "timestamp": datetime.datetime.now().isoformat(),
        "service": "Watchdog Backend API"
    }


@app.get("/")
async def root():
    return {
        "message": "Watchdog Backend API",
        "status": "running",
        "service": "watchdog"
    }


if __name__ == "__main__":
    # Default port for watchdog backend
    WATCHDOG_PORT = int(os.getenv("WATCHDOG_PORT", "8002"))
    
    uvicorn.run(
        "modules.watchdog.backend.main:app",
        host="0.0.0.0",
        port=WATCHDOG_PORT,
        reload=False,
        log_level="info"
    )
