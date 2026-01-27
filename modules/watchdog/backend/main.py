"""
Watchdog Backend - Separate FastAPI server for trading execution watchdog

THIS IS THE WATCHDOG BACKEND - Port 8002
NOT Dashboard (8001) or Matrix (8000)
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
from starlette.routing import WebSocketRoute
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
        is_set = watchdog_router.aggregator_instance is not None
        print(f"   [OK] Aggregator instance set in router: {is_set}")
        if not is_set:
            print("   [ERROR] Aggregator instance is None after assignment!")
        logger.info(f"Watchdog Aggregator started successfully (instance set: {is_set})")
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
print("=" * 60)
print("WATCHDOG BACKEND API - Initializing")
print("=" * 60)
app = FastAPI(
    title="Watchdog Backend API",
    description="Trading execution watchdog monitoring backend",
    lifespan=lifespan
)

# ============================================================================
# MIDDLEWARE CONFIGURATION
# ============================================================================
# CORS middleware - REQUIRED for Vite proxy to work
# Even though Vite proxies requests, CORS headers are still needed
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],  # Allow all origins in development
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Gzip compression middleware - DISABLED (can interfere with WebSocket)
# app.add_middleware(GZipMiddleware, minimum_size=1000)
# ============================================================================

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

# Ensure WebSocket routes are registered - add explicit logging
print("=" * 60)
print("WATCHDOG BACKEND - All Routes Registered:")
print("=" * 60)
all_routes = []
ws_routes = []
http_routes = []

for route in app.routes:
    if hasattr(route, 'path'):
        # Check if this is a WebSocket route by checking route type
        is_websocket = isinstance(route, WebSocketRoute) or 'ws' in route.path.lower()
        
        route_info = {
            'path': route.path,
            'type': 'websocket' if is_websocket else 'http',
            'methods': getattr(route, 'methods', set())
        }
        all_routes.append(route_info)
        
        if is_websocket:
            ws_routes.append(route.path)
        else:
            http_routes.append(route.path)

print(f"Total routes: {len(all_routes)}")
print()
print("WebSocket Routes:")
if ws_routes:
    for route in ws_routes:
        print(f"  ✓ {route}")
else:
    print("  ⚠️  NO WEBSOCKET ROUTES FOUND!")
print()
print("HTTP Routes (sample, first 10):")
for route in http_routes[:10]:
    print(f"  • {route}")
if len(http_routes) > 10:
    print(f"  ... and {len(http_routes) - 10} more")
print("=" * 60)

# Critical check: /ws/events must be present
if '/ws/events' not in ws_routes:
    print("=" * 60)
    print("⚠️  CRITICAL: /ws/events route NOT found!")
    print("WebSocket will NOT work!")
    print("=" * 60)
else:
    print("✓ /ws/events route confirmed registered")

logger.info(f"WS_ROUTES_REGISTERED {ws_routes}")
logger.info(f"Total routes: {len(all_routes)}, WebSocket: {len(ws_routes)}, HTTP: {len(http_routes)}")


@app.get("/health")
async def health_check():
    """Health check endpoint - does not require aggregator"""
    return {
        "status": "healthy",
        "timestamp": datetime.datetime.now().isoformat(),
        "service": "Watchdog Backend API",
        "aggregator_initialized": watchdog_aggregator_instance is not None
    }

@app.get("/test-simple")
async def test_simple():
    """Simple test endpoint that doesn't use aggregator"""
    return {
        "message": "Backend is responding",
        "timestamp": datetime.datetime.now().isoformat()
    }


@app.get("/test-ws-route")
async def test_ws_route():
    """Test endpoint to verify WebSocket routes are registered."""
    ws_routes = [r.path for r in app.routes if hasattr(r, 'path') and 'ws' in r.path.lower()]
    return {
        "websocket_routes": ws_routes,
        "total_routes": len([r for r in app.routes if hasattr(r, 'path')]),
        "backend_port": int(os.getenv("WATCHDOG_PORT", "8002"))
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
    
    print("\n" + "=" * 60)
    print("STARTING WATCHDOG BACKEND")
    print("=" * 60)
    print(f"Module: modules.watchdog.backend.main:app")
    print(f"Port: {WATCHDOG_PORT}")
    print(f"Service: Watchdog Backend API (NOT Dashboard, NOT Matrix)")
    print("=" * 60 + "\n")
    
    uvicorn.run(
        "modules.watchdog.backend.main:app",
        host="0.0.0.0",
        port=WATCHDOG_PORT,
        reload=False,
        log_level="info"
    )
