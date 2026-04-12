"""Read-only CME session check for timetable_current (watchdog); no writes."""

import json
import sys
from pathlib import Path

import pytest

QTSW2_ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(QTSW2_ROOT))


def test_session_waiting_logged_when_session_lags(tmp_path, monkeypatch, caplog):
    monkeypatch.chdir(tmp_path)
    root = tmp_path
    (root / "data" / "timetable").mkdir(parents=True)

    def _cme(_utc=None):
        return "2026-04-02"

    monkeypatch.setattr("modules.timetable.cme_session.get_cme_trading_date", _cme)

    p = root / "data" / "timetable" / "timetable_current.json"
    p.write_text(
        json.dumps({"session_trading_date": "2026-04-01", "streams": []}),
        encoding="utf-8",
    )

    from modules.timetable.timetable_auto_roll import ensure_current_session_timetable

    with caplog.at_level("INFO"):
        ensure_current_session_timetable(root)

    assert any("SESSION_WAITING_FOR_VALID_TIMETABLE" in r.message for r in caplog.records)
    # No write: content unchanged
    doc = json.loads(p.read_text(encoding="utf-8"))
    assert doc["session_trading_date"] == "2026-04-01"


def test_clamp_ahead_no_session_waiting(tmp_path, monkeypatch, caplog):
    """File one calendar day ahead before 18:00 CT: aligned to canonical CME — no wait log."""
    monkeypatch.chdir(tmp_path)
    root = tmp_path
    (root / "data" / "timetable").mkdir(parents=True)

    def _cme(_utc=None):
        return "2026-04-01"

    monkeypatch.setattr("modules.timetable.cme_session.get_cme_trading_date", _cme)
    monkeypatch.setattr(
        "modules.timetable.cme_session.is_past_cme_rollover", lambda _utc=None: False
    )

    p = root / "data" / "timetable" / "timetable_current.json"
    p.write_text(
        json.dumps({"session_trading_date": "2026-04-02", "streams": []}),
        encoding="utf-8",
    )

    from modules.timetable.timetable_auto_roll import ensure_current_session_timetable

    with caplog.at_level("INFO"):
        ensure_current_session_timetable(root)

    assert not any("SESSION_WAITING_FOR_VALID_TIMETABLE" in r.message for r in caplog.records)


def test_no_log_when_session_matches(tmp_path, monkeypatch, caplog):
    monkeypatch.chdir(tmp_path)
    root = tmp_path
    (root / "data" / "timetable").mkdir(parents=True)

    def _cme(_utc=None):
        return "2026-04-02"

    monkeypatch.setattr("modules.timetable.cme_session.get_cme_trading_date", _cme)

    p = root / "data" / "timetable" / "timetable_current.json"
    p.write_text(
        json.dumps({"session_trading_date": "2026-04-02", "streams": []}),
        encoding="utf-8",
    )

    from modules.timetable.timetable_auto_roll import ensure_current_session_timetable

    with caplog.at_level("INFO"):
        ensure_current_session_timetable(root)

    assert not any("SESSION_WAITING_FOR_VALID_TIMETABLE" in r.message for r in caplog.records)


def test_missing_file_logs_waiting(tmp_path, monkeypatch, caplog):
    monkeypatch.chdir(tmp_path)
    root = tmp_path
    (root / "data" / "timetable").mkdir(parents=True)

    def _cme(_utc=None):
        return "2026-04-02"

    monkeypatch.setattr("modules.timetable.cme_session.get_cme_trading_date", _cme)

    from modules.timetable.timetable_auto_roll import ensure_current_session_timetable

    with caplog.at_level("INFO"):
        ensure_current_session_timetable(root)

    assert any("SESSION_WAITING_FOR_VALID_TIMETABLE" in r.message for r in caplog.records)


def test_replay_timetable_skips_check(tmp_path, monkeypatch, caplog):
    monkeypatch.chdir(tmp_path)
    root = tmp_path
    (root / "data" / "timetable").mkdir(parents=True)

    def _cme(_utc=None):
        return "2026-04-02"

    monkeypatch.setattr("modules.timetable.cme_session.get_cme_trading_date", _cme)

    p = root / "data" / "timetable" / "timetable_current.json"
    p.write_text(
        json.dumps(
            {
                "session_trading_date": "2026-04-01",
                "metadata": {"replay": True},
                "streams": [],
            }
        ),
        encoding="utf-8",
    )

    from modules.timetable.timetable_auto_roll import ensure_current_session_timetable

    with caplog.at_level("INFO"):
        ensure_current_session_timetable(root)

    assert not any("SESSION_WAITING_FOR_VALID_TIMETABLE" in r.message for r in caplog.records)
