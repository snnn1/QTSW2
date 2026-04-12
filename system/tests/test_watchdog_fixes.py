#!/usr/bin/env python3
"""Test watchdog fixes"""

import sys
from pathlib import Path
from datetime import datetime, timezone
from unittest import mock

import pytest

qtsw2_root = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(qtsw2_root))

from modules.watchdog.state_manager import WatchdogStateManager


@pytest.mark.parametrize("engine_ticks", [True, False])
def test_feed_health_never_market_closed_when_session_open(engine_ticks):
    """UNKNOWN / no bars yet must not set feed_health to MARKET_CLOSED while is_market_open is True."""
    sm = WatchdogStateManager()
    if engine_ticks:
        sm.update_engine_tick(datetime.now(timezone.utc))
    with mock.patch("modules.watchdog.state_manager.is_market_open", return_value=True):
        status = sm.compute_watchdog_status()
    assert status["market_open"] is True
    assert status["feed_health_classification"] != "MARKET_CLOSED"
    if engine_ticks:
        assert status["feed_health_classification"] == "DATA_FLOWING"
    else:
        assert status["feed_health_classification"] == "DATA_STALLED"

def test_auto_clear():
    print("Testing auto-clear logic...")
    
    sm = WatchdogStateManager()
    
    # Set stuck states
    sm._recovery_state = 'DISCONNECT_FAIL_CLOSED'
    sm._connection_status = 'ConnectionLost'
    
    print(f"Before: recovery_state={sm._recovery_state}, connection_status={sm._connection_status}")
    
    # Update engine tick (simulating engine is alive)
    sm.update_engine_tick(datetime.now(timezone.utc))
    
    # Compute status (this should trigger auto-clear)
    status = sm.compute_watchdog_status()
    
    print(f"After: recovery_state={status['recovery_state']}, connection_status={status['connection_status']}")
    
    if status['recovery_state'] == 'CONNECTED_OK' and status['connection_status'] == 'Connected':
        print("[OK] Auto-clear logic working correctly")
        return True
    else:
        print("[FAIL] Auto-clear logic not working")
        return False

if __name__ == "__main__":
    test_auto_clear()
