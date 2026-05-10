from __future__ import annotations

import json
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.alert_ledger import AlertLedger
from modules.watchdog.config import ALERT_LEDGER_PATH, NOTIFICATIONS_CONFIG_PATH, NOTIFICATIONS_SECRETS_PATH
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
