"""
Unit tests for Watchdog Phase 1/2: AlertLedger, ProcessMonitor, NotificationService.
"""
import json
import tempfile
from datetime import datetime, timezone, timedelta
from pathlib import Path

import pytest

# Ensure project root in path
import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from modules.watchdog.alert_ledger import AlertLedger, generate_alert_id
from modules.watchdog.process_monitor import (
    is_supervision_window_open,
    check_ninjatrader_running,
    ProcessMonitor,
    ProcessState,
)


class TestAlertLedger:
    """Tests for AlertLedger."""

    def test_append_and_resolve(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "ledger.jsonl"
            ledger = AlertLedger(ledger_path=path)

            ledger.append_alert("a1", "TEST", "critical", {"x": 1}, "KEY1")
            assert ledger.is_alert_active("KEY1")
            assert len(ledger.get_active_alerts()) == 1

            ledger.resolve_alert("KEY1")
            assert not ledger.is_alert_active("KEY1")
            assert len(ledger.get_active_alerts()) == 0

    def test_rehydrate_restores_unresolved(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "ledger.jsonl"
            now = datetime.now(timezone.utc).isoformat()
            with open(path, "w") as f:
                f.write(json.dumps({
                    "alert_id": "a1", "alert_type": "X", "severity": "critical",
                    "first_seen_utc": now, "last_seen_utc": now, "dedupe_key": "KEY1",
                    "context": {},
                }) + "\n")
                f.write(json.dumps({"event": "resolved", "dedupe_key": "KEY1", "resolved_utc": now}) + "\n")
                f.write(json.dumps({
                    "alert_id": "a2", "alert_type": "Y", "severity": "warning",
                    "first_seen_utc": now, "last_seen_utc": now, "dedupe_key": "KEY2",
                    "context": {},
                }) + "\n")

            ledger = AlertLedger(ledger_path=path)
            assert not ledger.is_alert_active("KEY1")
            assert ledger.is_alert_active("KEY2")
            assert len(ledger.get_active_alerts()) == 1

    def test_generate_alert_id(self):
        uid = generate_alert_id()
        assert isinstance(uid, str)
        assert len(uid) == 36
        assert uid.count("-") == 4


class TestIsSupervisionWindowOpen:
    """Tests for is_supervision_window_open."""

    def test_market_open(self):
        assert is_supervision_window_open(True, 0, None, datetime.now(timezone.utc)) is True

    def test_market_closed_no_intents_no_recent_tick(self):
        assert is_supervision_window_open(
            False, 0,
            datetime.now(timezone.utc) - timedelta(hours=3),
            datetime.now(timezone.utc),
        ) is False

    def test_active_intents(self):
        assert is_supervision_window_open(False, 1, None, datetime.now(timezone.utc)) is True

    def test_recent_engine_tick(self):
        recent = datetime.now(timezone.utc) - timedelta(minutes=30)
        assert is_supervision_window_open(False, 0, recent, datetime.now(timezone.utc)) is True


class TestProcessMonitor:
    """Tests for ProcessMonitor."""

    def test_check_process_up_initial(self):
        down_calls = []
        restored_calls = []

        def on_down(ctx):
            down_calls.append(ctx)

        def on_restored(key):
            restored_calls.append(key)

        monitor = ProcessMonitor(on_process_down=on_down, on_process_restored=on_restored)
        state = monitor.check(True, 0, None)
        assert state in (ProcessState.PROCESS_UP, ProcessState.PROCESS_MISSING)
        # Callbacks only fire on state transitions
        assert len(down_calls) == 0 or state == ProcessState.PROCESS_MISSING
        assert len(restored_calls) == 0

    def test_state_property(self):
        monitor = ProcessMonitor()
        assert monitor.state in (ProcessState.PROCESS_UP, ProcessState.PROCESS_MISSING)
        assert monitor.is_process_up() == (monitor.state == ProcessState.PROCESS_UP)


class TestNotificationService:
    """Tests for NotificationService (no network)."""

    def test_send_restored_when_disabled(self):
        from modules.watchdog.notifications.notification_service import NotificationService

        svc = NotificationService(ledger=AlertLedger())
        svc._enabled = False
        svc.send_restored_notification("Test", "Message")  # no-op, no exception

    def test_raise_alert_when_disabled(self):
        from modules.watchdog.notifications.notification_service import NotificationService

        svc = NotificationService(ledger=AlertLedger())
        svc._enabled = False
        svc.raise_alert("TEST", "critical", {}, "TEST", min_resend_interval_seconds=300)
        assert not svc.get_ledger().is_alert_active("TEST")
