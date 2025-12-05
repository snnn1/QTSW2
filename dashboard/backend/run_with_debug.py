"""
Run Dashboard Backend with Full Debug Logging
"""

import sys
import logging
from pathlib import Path

# Add project root to path
qtsw2_root = Path(__file__).parent.parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

# Configure detailed logging
logging.basicConfig(
    level=logging.DEBUG,
    format='%(asctime)s | %(name)s | %(levelname)s | %(message)s',
    handlers=[
        logging.StreamHandler(sys.stdout),
        logging.FileHandler(
            qtsw2_root / "logs" / "backend_debug_full.log",
            mode='a',
            encoding='utf-8'
        )
    ]
)

# Set all loggers to DEBUG
logging.getLogger().setLevel(logging.DEBUG)
logging.getLogger("uvicorn").setLevel(logging.DEBUG)
logging.getLogger("uvicorn.access").setLevel(logging.INFO)
logging.getLogger("uvicorn.error").setLevel(logging.DEBUG)
logging.getLogger("fastapi").setLevel(logging.DEBUG)
logging.getLogger("dashboard").setLevel(logging.DEBUG)
logging.getLogger("orchestrator").setLevel(logging.DEBUG)

print("="*60)
print("  STARTING DASHBOARD BACKEND WITH DEBUG LOGGING")
print("="*60)
print(f"Project root: {qtsw2_root}")
print(f"Debug log: {qtsw2_root / 'logs' / 'backend_debug_full.log'}")
print("="*60)
print()

# Import and run uvicorn
import uvicorn

if __name__ == "__main__":
    try:
        uvicorn.run(
            "dashboard.backend.main:app",
            host="0.0.0.0",
            port=8001,
            reload=True,
            log_level="debug"
        )
    except KeyboardInterrupt:
        print("\nShutting down...")
    except Exception as e:
        print(f"\nFATAL ERROR: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)

