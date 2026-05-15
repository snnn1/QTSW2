from __future__ import annotations

import json
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.alert_ledger import AlertLedger
from modules.watchdog.config import ALERT_LEDGER_PATH, NOTIFICATIONS_CONFIG_PATH, NOTIFICATIONS_SECRETS_PATH
from modules.watchdog.incident_recorder import IncidentRecorder
from modules.watchdog.notifications.notification_service import ALERT_TYPE_LABELS, NotificationService


def _workspace_temp_dir() -> Path:
    base = Path.cwd() / "tmp" / "pytest_watchdog_notifications"
    base.mkdir(parents=True, exist_ok=True)
    path = base / uuid.uuid4().hex
    path.mkdir(parents=True, exist_ok=False)
    return path


def test_notification_service_default_paths_use_repo_config() -> None:
    temp_dir = _workspace_temp_dir()
    ledger = AlertLedger(ledger_path=temp_dir / "alert_ledger.jsonl")

    service = NotificationService(ledger=ledger)

    assert service._config_path == NOTIFICATIONS_CONFIG_PATH
    assert service._secrets_path == NOTIFICATIONS_SECRETS_PATH
    assert service.is_alert_suppressed("CONNECTION_LOST_SUSTAINED") is True
    assert service.is_alert_suppressed("ROBOT_HEARTBEAT_LOST") is False


def test_alert_ledger_default_path_uses_repo_data_root() -> None:
    ledger = AlertLedger()

    assert ledger._ledger_path == ALERT_LEDGER_PATH


def test_alert_ledger_ignores_non_object_records_during_rehydrate() -> None:
    temp_dir = _workspace_temp_dir()
    ledger_path = temp_dir / "alert_ledger.jsonl"
    valid = {
        "alert_id": "alert-1",
        "alert_type": "ROBOT_HEARTBEAT_LOST",
        "severity": "high",
        "first_seen_utc": "2026-05-06T00:00:00+00:00",
        "last_seen_utc": "2026-05-06T00:00:00+00:00",
        "active": True,
        "resolved_utc": None,
        "dedupe_key": "ROBOT_HEARTBEAT_LOST",
        "context": {},
        "delivery_status": "pending",
        "delivery_channel": None,
        "delivery_attempts": 0,
        "last_delivery_utc": None,
    }
    ledger_path.write_text(
        json.dumps("bad-record") + "\n" + json.dumps(valid) + "\n",
        encoding="utf-8",
    )

    ledger = AlertLedger(ledger_path=ledger_path)

    assert ledger.is_alert_active("ROBOT_HEARTBEAT_LOST") is True


def test_alert_ledger_read_recent_folds_resolution_and_delivery_updates() -> None:
    temp_dir = _workspace_temp_dir()
    ledger = AlertLedger(ledger_path=temp_dir / "alert_ledger.jsonl")

    ledger.append_alert(
        "alert-order-stuck",
        "ORDER_STUCK_DETECTED",
        "medium",
        {"sample": {"broker_order_id": "order-1"}},
        "ORDER_STUCK_DETECTED_BATCH",
    )
    ledger.update_delivery(
        "ORDER_STUCK_DETECTED_BATCH",
        delivery_status="failed",
        delivery_channel="pushover",
        delivery_attempts=2,
    )
    ledger.resolve_alert("ORDER_STUCK_DETECTED_BATCH")

    recent = ledger.read_recent()

    assert len(recent) == 1
    assert recent[0]["alert_id"] == "alert-order-stuck"
    assert recent[0]["active"] is False
    assert recent[0]["resolved_utc"]
    assert recent[0]["delivery_status"] == "failed"
    assert recent[0]["delivery_channel"] == "pushover"
    assert recent[0]["delivery_attempts"] == 2
    assert ledger.read_recent(active_only=True) == []


def test_stale_active_incident_expires_without_new_notification() -> None:
    temp_dir = _workspace_temp_dir()
    recorder = IncidentRecorder(
        incidents_path=temp_dir / "incidents.jsonl",
        active_incidents_path=temp_dir / "active_incidents.json",
    )
    notifications = []
    recorder.set_on_incident_callback(lambda record: notifications.append(record))
    recorder.process_event(
        {
            "event_type": "FORCED_FLATTEN_TRIGGERED",
            "timestamp_utc": "2026-05-01T12:00:00Z",
            "data": {"instrument": "MNG"},
        }
    )

    closed = recorder.reconcile_stale_active_incidents(
        now=datetime(2026, 5, 3, 12, 0, 1, tzinfo=timezone.utc),
        max_age_seconds=24 * 60 * 60,
    )

    assert len(closed) == 1
    assert closed[0]["type"] == "FORCED_FLATTEN"
    assert closed[0]["end_reason"] == "STALE_ACTIVE_INCIDENT_EXPIRED"
    assert closed[0]["stale_active_incident"] is True
    assert recorder.get_active_incidents() == {}
    assert notifications == []


def test_watchdog_alert_labels_cover_generated_alerts() -> None:
    expected = {
        "NINJATRADER_PROCESS_STOPPED",
        "ROBOT_HEARTBEAT_LOST",
        "CONNECTION_LOST_SUSTAINED",
        "POTENTIAL_ORPHAN_POSITION",
        "CONFIRMED_ORPHAN_POSITION",
        "RECOVERY_LOOP_DETECTED",
        "FEED_INGESTION_DELAY",
        "WATCHDOG_LOOP_SLOW",
        "ANOMALY_RATE_EXCEEDED",
        "ORDER_STUCK_DETECTED",
        "LOG_FILE_STALLED",
        "TIMETABLE_DRIFT",
        "SESSION_FLATTEN_NOT_CONFIRMED_CRITICAL",
        "SESSION_FLATTEN_AT_RISK_WARNING",
        "INCIDENT_RECONCILIATION_GATE_FAIL_CLOSED",
        "INCIDENT_ADOPTION_GRACE_EXPIRED",
    }

    missing = sorted(expected - set(ALERT_TYPE_LABELS))

    assert missing == []


def test_raise_alert_respects_resend_interval_and_keeps_one_active_alert() -> None:
    temp_dir = _workspace_temp_dir()
    config_path = temp_dir / "notifications.json"
    secrets_path = temp_dir / "notifications.secrets.json"
    ledger = AlertLedger(ledger_path=temp_dir / "alert_ledger.jsonl")
    config_path.write_text(
        json.dumps(
            {
                "enabled": True,
                "channels": {"pushover": {"enabled": True}},
                "rate_limits": {"max_alerts_per_hour": 30},
            }
        ),
        encoding="utf-8",
    )
    secrets_path.write_text(
        json.dumps({"pushover": {"user_key": "user", "app_token": "token"}}),
        encoding="utf-8",
    )
    service = NotificationService(config_path=config_path, secrets_path=secrets_path, ledger=ledger)

    service.raise_alert(
        alert_type="ROBOT_HEARTBEAT_LOST",
        severity="high",
        context={"n": 1},
        dedupe_key="ROBOT_HEARTBEAT_LOST",
        min_resend_interval_seconds=300,
    )
    service.raise_alert(
        alert_type="ROBOT_HEARTBEAT_LOST",
        severity="high",
        context={"n": 2},
        dedupe_key="ROBOT_HEARTBEAT_LOST",
        min_resend_interval_seconds=300,
    )

    assert service._queue.qsize() == 1
    active = ledger.get_active_alert("ROBOT_HEARTBEAT_LOST")
    assert active is not None
    assert active["context"] == {"n": 2}


def test_raise_alert_min_resend_zero_is_fail_loud() -> None:
    temp_dir = _workspace_temp_dir()
    config_path = temp_dir / "notifications.json"
    secrets_path = temp_dir / "notifications.secrets.json"
    ledger = AlertLedger(ledger_path=temp_dir / "alert_ledger.jsonl")
    config_path.write_text(
        json.dumps(
            {
                "enabled": True,
                "channels": {"pushover": {"enabled": True}},
                "rate_limits": {"max_alerts_per_hour": 30},
            }
        ),
        encoding="utf-8",
    )
    secrets_path.write_text(
        json.dumps({"pushover": {"user_key": "user", "app_token": "token"}}),
        encoding="utf-8",
    )
    service = NotificationService(config_path=config_path, secrets_path=secrets_path, ledger=ledger)

    for n in range(2):
        service.raise_alert(
            alert_type="TIMETABLE_DRIFT",
            severity="critical",
            context={"n": n},
            dedupe_key="TIMETABLE_DRIFT",
            min_resend_interval_seconds=0,
        )

    assert service._queue.qsize() == 2
