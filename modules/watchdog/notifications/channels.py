"""Channel interface for notification backends."""

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Any, Dict, Optional


@dataclass
class DeliveryResult:
    """Result of a notification send attempt."""

    success: bool
    channel: str
    error: Optional[str] = None
    http_status: Optional[int] = None


class NotificationChannel(ABC):
    """Abstract base for notification channels."""

    @abstractmethod
    def send(
        self,
        title: str,
        message: str,
        priority: int = 0,
        context: Optional[Dict[str, Any]] = None,
    ) -> DeliveryResult:
        """Send notification. Returns DeliveryResult."""
        pass
