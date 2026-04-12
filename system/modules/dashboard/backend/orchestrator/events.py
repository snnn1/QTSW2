"""
DEPRECATED: This EventBus has been consolidated into modules/orchestrator/events.py

This file is kept for backward compatibility but should not be used.
All code should import from modules.orchestrator.events instead.

The main EventBus now includes all features:
- Snapshot caching (get_snapshot_cached)
- Cleanup methods (cleanup_run_events)
- All original EventBus functionality
"""

# Re-export from main EventBus for backward compatibility
from modules.orchestrator.events import EventBus

__all__ = ["EventBus"]
