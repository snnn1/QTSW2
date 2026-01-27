"""
WebSocket Connection Tracker

Tracks WebSocket connection metrics for health monitoring and debugging.
Thread-safe for use with asyncio.
"""
import asyncio
import logging
from typing import Dict, Optional
from datetime import datetime, timezone
from collections import defaultdict

logger = logging.getLogger(__name__)


class WebSocketTracker:
    """Thread-safe WebSocket connection tracker."""
    
    def __init__(self):
        # Use asyncio.Lock for thread-safe access
        self._lock = asyncio.Lock()
        
        # Active connections tracking
        self._active_connections: set = set()  # Set of connection IDs
        self._connections_by_path: Dict[str, int] = defaultdict(int)
        self._connections_by_run_id: Dict[str, int] = defaultdict(int)
        self._connection_metadata: Dict[str, Dict] = {}  # connection_id -> {path, run_id, connect_time}
        
        # Connection statistics
        self._connect_attempt_count = 0
        self._accept_success_count = 0
        self._accept_fail_count = 0
        
        # Timestamps
        self._last_connect_utc: Optional[datetime] = None
        self._last_disconnect_utc: Optional[datetime] = None
        self._last_message_sent_utc: Optional[datetime] = None
        
        # Error tracking
        self._last_accept_error: Optional[str] = None
        self._last_accept_error_utc: Optional[datetime] = None
        self._last_error: Optional[str] = None
        self._last_error_utc: Optional[datetime] = None
        
        # Backpressure tracking
        self._dropped_events_total = 0
    
    def _generate_connection_id(self, path: str, run_id: Optional[str] = None) -> str:
        """Generate unique connection ID."""
        run_id_part = run_id[:8] if run_id else "all"
        timestamp = datetime.now(timezone.utc).isoformat()
        return f"{path}:{run_id_part}:{timestamp}"
    
    async def register_connection(self, path: str, run_id: Optional[str] = None) -> str:
        """Register a new connection. Returns connection ID."""
        async with self._lock:
            connection_id = self._generate_connection_id(path, run_id)
            self._active_connections.add(connection_id)
            self._connections_by_path[path] += 1
            if run_id:
                self._connections_by_run_id[run_id] += 1
            
            self._connection_metadata[connection_id] = {
                "path": path,
                "run_id": run_id,
                "connect_time": datetime.now(timezone.utc)
            }
            
            self._connect_attempt_count += 1
            self._accept_success_count += 1
            self._last_connect_utc = datetime.now(timezone.utc)
            
            return connection_id
    
    async def unregister_connection(self, connection_id: str):
        """Unregister a connection."""
        async with self._lock:
            if connection_id not in self._active_connections:
                return
            
            self._active_connections.discard(connection_id)
            
            if connection_id in self._connection_metadata:
                metadata = self._connection_metadata.pop(connection_id)
                path = metadata["path"]
                run_id = metadata.get("run_id")
                
                self._connections_by_path[path] = max(0, self._connections_by_path[path] - 1)
                if self._connections_by_path[path] == 0:
                    del self._connections_by_path[path]
                
                if run_id:
                    self._connections_by_run_id[run_id] = max(0, self._connections_by_run_id[run_id] - 1)
                    if self._connections_by_run_id[run_id] == 0:
                        del self._connections_by_run_id[run_id]
            
            self._last_disconnect_utc = datetime.now(timezone.utc)
    
    async def record_accept_failure(self, error_message: str):
        """Record a failed accept attempt."""
        async with self._lock:
            self._connect_attempt_count += 1
            self._accept_fail_count += 1
            self._last_accept_error = error_message
            self._last_accept_error_utc = datetime.now(timezone.utc)
    
    async def record_error(self, error_message: str):
        """Record a general error."""
        async with self._lock:
            self._last_error = error_message
            self._last_error_utc = datetime.now(timezone.utc)
    
    async def record_dropped_events(self, count: int):
        """Record dropped events due to backpressure."""
        async with self._lock:
            self._dropped_events_total += count
    
    async def record_message_sent(self):
        """Record that a message was sent."""
        async with self._lock:
            self._last_message_sent_utc = datetime.now(timezone.utc)
    
    async def get_snapshot(self) -> Dict:
        """Get current snapshot of all metrics."""
        async with self._lock:
            return {
                "ws_enabled": True,
                "active_connections_total": len(self._active_connections),
                "active_connections_by_path": dict(self._connections_by_path),
                "active_connections_by_run_id": dict(self._connections_by_run_id),
                "last_connect_utc": self._last_connect_utc.isoformat() if self._last_connect_utc else None,
                "last_disconnect_utc": self._last_disconnect_utc.isoformat() if self._last_disconnect_utc else None,
                "accept_fail_count": self._accept_fail_count,
                "last_accept_error": self._last_accept_error,
                "last_accept_error_utc": self._last_accept_error_utc.isoformat() if self._last_accept_error_utc else None,
                "last_error": self._last_error,
                "last_error_utc": self._last_error_utc.isoformat() if self._last_error_utc else None,
                "dropped_events_total": self._dropped_events_total,
            }


# Global tracker instance
_tracker_instance: Optional[WebSocketTracker] = None


def get_tracker() -> WebSocketTracker:
    """Get global tracker instance."""
    global _tracker_instance
    if _tracker_instance is None:
        _tracker_instance = WebSocketTracker()
    return _tracker_instance
