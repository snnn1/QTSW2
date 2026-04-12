"""
NinjaTrader process monitor for Watchdog Phase 1.

Detects when NinjaTrader.exe is not running during supervision window.
Uses psutil when available; gracefully degrades if not installed.
"""
import logging
from datetime import datetime, timezone
from enum import Enum
from typing import Callable, Optional, Tuple

logger = logging.getLogger(__name__)

# Optional psutil
try:
    import psutil
    PSUTIL_AVAILABLE = True
except ImportError:
    PSUTIL_AVAILABLE = False
    psutil = None


class ProcessState(Enum):
    PROCESS_UP = "PROCESS_UP"
    PROCESS_MISSING = "PROCESS_MISSING"
    PROCESS_RESTORED = "PROCESS_RESTORED"


def is_supervision_window_open(
    market_open: bool,
    active_intent_count: int,
    last_engine_tick_utc: Optional[datetime],
    now_utc: datetime,
) -> bool:
    """True if any condition suggests robot should be supervised."""
    if market_open:
        return True
    if active_intent_count > 0:
        return True
    if last_engine_tick_utc and (now_utc - last_engine_tick_utc).total_seconds() < 7200:  # 2 hours
        return True
    return False


def check_ninjatrader_running(process_name: str = "NinjaTrader.exe") -> Tuple[bool, int]:
    """
    Check if NinjaTrader process is running.

    Returns:
        Tuple of (is_running, count). count is number of matching processes.
    """
    if not PSUTIL_AVAILABLE:
        return False, 0
    count = 0
    try:
        for proc in psutil.process_iter(["name"]):
            try:
                name = proc.info.get("name") or ""
                if name and process_name.lower() in name.lower():
                    count += 1
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                continue
    except Exception as e:
        logger.warning(f"Process check failed: {e}")
        return False, 0
    return count > 0, count


class ProcessMonitor:
    """Monitors NinjaTrader process and raises alerts when down during supervision window."""

    def __init__(
        self,
        process_name: str = "NinjaTrader.exe",
        on_process_down: Optional[Callable[[dict], None]] = None,
        on_process_restored: Optional[Callable[[str], None]] = None,
    ):
        self._process_name = process_name
        self._on_process_down = on_process_down
        self._on_process_restored = on_process_restored
        self._state = ProcessState.PROCESS_UP
        self._last_check_utc: Optional[datetime] = None

    def check(
        self,
        market_open: bool,
        active_intent_count: int,
        last_engine_tick_utc: Optional[datetime],
    ) -> ProcessState:
        """
        Check process state. Call from aggregator loop.

        Returns:
            Current ProcessState. May trigger on_process_down or on_process_restored callbacks.
        """
        now = datetime.now(timezone.utc)
        self._last_check_utc = now

        if not PSUTIL_AVAILABLE:
            return self._state

        is_running, count = check_ninjatrader_running(self._process_name)

        if count > 1:
            logger.warning(f"Multiple NinjaTrader instances detected: {count}")

        if is_running:
            if self._state == ProcessState.PROCESS_MISSING:
                # Restored
                self._state = ProcessState.PROCESS_UP
                if self._on_process_restored:
                    try:
                        self._on_process_restored("NINJATRADER_PROCESS_STOPPED")
                    except Exception as e:
                        logger.error(f"on_process_restored callback failed: {e}")
                return ProcessState.PROCESS_RESTORED
            self._state = ProcessState.PROCESS_UP
            return ProcessState.PROCESS_UP

        # Process missing
        if self._state == ProcessState.PROCESS_UP:
            self._state = ProcessState.PROCESS_MISSING
            window_open = is_supervision_window_open(
                market_open, active_intent_count, last_engine_tick_utc, now
            )
            if window_open and self._on_process_down:
                try:
                    self._on_process_down({
                        "market_open": market_open,
                        "active_intent_count": active_intent_count,
                    })
                except Exception as e:
                    logger.error(f"on_process_down callback failed: {e}")
        return ProcessState.PROCESS_MISSING

    @property
    def state(self) -> ProcessState:
        return self._state

    def is_process_up(self) -> bool:
        return self._state == ProcessState.PROCESS_UP
