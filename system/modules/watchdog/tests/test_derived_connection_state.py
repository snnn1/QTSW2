#!/usr/bin/env python3
"""
Unit tests for derived_connection_state: LOST | RECOVERING | STABLE.

Deterministic, timestamp-driven logic. Run: python -m pytest modules/watchdog/tests/test_derived_connection_state.py -v
"""
from datetime import datetime, timezone, timedelta
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent.parent.parent))

from modules.watchdog.state_manager import WatchdogStateManager
from modules.watchdog.config import CONNECTION_STABLE_WINDOW_SECONDS


def _utc(offset_seconds: float = 0) -> datetime:
    return datetime.now(timezone.utc) + timedelta(seconds=offset_seconds)


def test_disconnect_only_state_lost():
    """Disconnect only -> state = LOST."""
    sm = WatchdogStateManager()
    sm.update_connection_status("ConnectionLost", _utc(0))
    now = _utc(10)
    state = sm._compute_derived_connection_state(now)
    assert state == "LOST", f"Expected LOST, got {state}"
    print("  [OK] Disconnect only -> LOST")


def test_recovery_just_happened_state_recovering():
    """Recovery just happened (<45s) -> state = RECOVERING."""
    sm = WatchdogStateManager()
    sm.update_connection_status("ConnectionLost", _utc(-100))
    sm.update_connection_status("Connected", _utc(-30))  # 30s ago
    now = _utc(0)
    state = sm._compute_derived_connection_state(now)
    assert state == "RECOVERING", f"Expected RECOVERING, got {state}"
    print("  [OK] Recovery <45s -> RECOVERING")


def test_recovery_plus_window_elapsed_state_stable():
    """Recovery + 45s elapsed -> state = STABLE."""
    sm = WatchdogStateManager()
    sm.update_connection_status("ConnectionLost", _utc(-120))
    sm.update_connection_status("Connected", _utc(-50))  # 50s ago
    now = _utc(0)
    state = sm._compute_derived_connection_state(now)
    assert state == "STABLE", f"Expected STABLE, got {state}"
    print("  [OK] Recovery + 45s -> STABLE")


def test_recovery_then_disconnect_before_window_state_lost():
    """Recovery then disconnect before window -> state = LOST."""
    sm = WatchdogStateManager()
    sm.update_connection_status("ConnectionLost", _utc(-100))
    sm.update_connection_status("Connected", _utc(-30))  # Recovered 30s ago
    sm.update_connection_status("ConnectionLost", _utc(-5))  # Lost again 5s ago
    now = _utc(0)
    state = sm._compute_derived_connection_state(now)
    assert state == "LOST", f"Expected LOST, got {state}"
    print("  [OK] Recovery then disconnect before window -> LOST")


def test_multiple_disconnect_recover_cycles():
    """Multiple disconnect/recover cycles -> always resolves correctly."""
    sm = WatchdogStateManager()
    base = _utc(-500)

    # Cycle 1: Lost -> Recover -> Stable
    sm.update_connection_status("ConnectionLost", base)
    sm.update_connection_status("Connected", base + timedelta(seconds=10))
    now1 = base + timedelta(seconds=10 + CONNECTION_STABLE_WINDOW_SECONDS + 1)
    assert sm._compute_derived_connection_state(now1) == "STABLE"

    # Cycle 2: Lost again
    sm.update_connection_status("ConnectionLost", base + timedelta(seconds=100))
    now2 = base + timedelta(seconds=101)
    assert sm._compute_derived_connection_state(now2) == "LOST"

    # Cycle 3: Recover, still in window
    sm.update_connection_status("Connected", base + timedelta(seconds=110))
    now3 = base + timedelta(seconds=120)  # 10s since recovery
    assert sm._compute_derived_connection_state(now3) == "RECOVERING"

    # Cycle 4: Window satisfied
    now4 = base + timedelta(seconds=110 + CONNECTION_STABLE_WINDOW_SECONDS + 1)
    assert sm._compute_derived_connection_state(now4) == "STABLE"

    print("  [OK] Multiple cycles resolve correctly")


def test_connection_stable_derived_from_state():
    """connection_stable = (derived_connection_state == STABLE)."""
    sm = WatchdogStateManager()
    sm.update_connection_status("ConnectionLost", _utc(-100))
    sm.update_connection_status("Connected", _utc(-50))
    now = _utc(0)
    status = sm.compute_watchdog_status()
    assert status["derived_connection_state"] == "STABLE"
    assert status["connection_stable"] is True

    sm.update_connection_status("ConnectionLost", _utc(1))
    now2 = _utc(2)
    status2 = sm.compute_watchdog_status()
    assert status2["derived_connection_state"] == "LOST"
    assert status2["connection_stable"] is False
    print("  [OK] connection_stable derived from derived_connection_state")


def test_stable_since_after_last_lost():
    """STABLE requires _connection_stable_since_utc > _last_connection_lost_utc when both exist."""
    sm = WatchdogStateManager()
    lost_ts = _utc(-100)
    recover_ts = _utc(-50)  # After lost
    sm.update_connection_status("ConnectionLost", lost_ts)
    sm.update_connection_status("Connected", recover_ts)
    now = _utc(0)
    state = sm._compute_derived_connection_state(now)
    assert state == "STABLE", f"Expected STABLE (recover after lost), got {state}"
    print("  [OK] STABLE when stable_since > last_lost")


def test_lost_when_stable_since_before_last_lost():
    """LOST when _connection_stable_since_utc <= _last_connection_lost_utc (stale recovery)."""
    sm = WatchdogStateManager()
    # Simulate: we had recovery at -100, then lost at -50 (recovery is stale)
    sm._connection_stable_since_utc = _utc(-100)
    sm._last_connection_lost_utc = _utc(-50)
    now = _utc(0)
    state = sm._compute_derived_connection_state(now)
    assert state == "LOST", f"Expected LOST (stale recovery), got {state}"
    print("  [OK] LOST when stable_since <= last_lost (stale)")


def run_all():
    """Run all tests."""
    print("Derived connection state tests:")
    test_disconnect_only_state_lost()
    test_recovery_just_happened_state_recovering()
    test_recovery_plus_window_elapsed_state_stable()
    test_recovery_then_disconnect_before_window_state_lost()
    test_multiple_disconnect_recover_cycles()
    test_connection_stable_derived_from_state()
    test_stable_since_after_last_lost()
    test_lost_when_stable_since_before_last_lost()
    print("All tests passed.")


if __name__ == "__main__":
    run_all()
