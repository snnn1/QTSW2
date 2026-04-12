"""
Monitor Live Events - Real-time event viewer
Shows events as they're published to EventBus (what the dashboard sees)
"""

import sys
import asyncio
import json
from pathlib import Path
from datetime import datetime

# Add project root to path
qtsw2_root = Path(__file__).parent.parent.parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

from modules.orchestrator.config import OrchestratorConfig
from modules.orchestrator.events import EventBus


async def monitor_events():
    """Monitor events from EventBus in real-time"""
    print("=" * 80)
    print("Live Events Monitor")
    print("=" * 80)
    print("This shows events as they're published to EventBus (what the dashboard sees)")
    print("Press Ctrl+C to stop")
    print("=" * 80)
    print()
    
    # Create config and EventBus
    config = OrchestratorConfig(qtsw2_root=qtsw2_root)
    event_bus = EventBus(
        event_logs_dir=config.event_logs_dir,
        logger=None  # Use default logger
    )
    
    # Get recent events first (snapshot)
    recent = event_bus.get_recent_events(limit=20)
    if recent:
        print(f"Recent events (last {len(recent)}):")
        print("-" * 80)
        for event in recent[-10:]:  # Show last 10
            timestamp = event.get("timestamp", "unknown")
            stage = event.get("stage", "unknown")
            event_type = event.get("event", "unknown")
            run_id = event.get("run_id", "unknown")[:8] if event.get("run_id") else "unknown"
            msg = event.get("msg", "")
            print(f"[{timestamp}] {stage}/{event_type} (run: {run_id}) {msg}")
        print("-" * 80)
        print()
    
    print("Waiting for new events...")
    print()
    
    # Subscribe to new events
    event_count = 0
    async for event in event_bus.subscribe():
        event_count += 1
        timestamp = event.get("timestamp", "unknown")
        stage = event.get("stage", "unknown")
        event_type = event.get("event", "unknown")
        run_id = event.get("run_id", "unknown")[:8] if event.get("run_id") else "unknown"
        msg = event.get("msg", "")
        
        # Format timestamp for display
        try:
            if timestamp != "unknown":
                dt = datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
                time_str = dt.strftime("%H:%M:%S.%f")[:-3]  # Show milliseconds
            else:
                time_str = "unknown"
        except:
            time_str = timestamp
        
        print(f"[{time_str}] #{event_count} | {stage}/{event_type} | run: {run_id} | {msg}")


if __name__ == "__main__":
    try:
        asyncio.run(monitor_events())
    except KeyboardInterrupt:
        print("\n\nStopped monitoring.")
    except Exception as e:
        print(f"\nError: {e}")
        import traceback
        traceback.print_exc()



