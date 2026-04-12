"""Unit tests for Timetable Supervisor (validator-only; no auto-publish)."""

import json
from unittest.mock import patch

import pandas as pd
import pytest


@pytest.fixture
def project_root(tmp_path):
    (tmp_path / "data" / "master_matrix").mkdir(parents=True)
    (tmp_path / "data" / "analyzed").mkdir(parents=True)
    return tmp_path


def _install_session_authority_auto(project_root, session: str) -> None:
    """Model A: supervisor requires persisted auto authority matching canonical CME for the test."""
    p = project_root / "data" / "session" / "session_authority.json"
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(
        json.dumps(
            {
                "mode": "auto",
                "session_trading_date": session,
                "source": "system",
                "locked": False,
                "set_at_utc": "2026-01-01T00:00:00Z",
                "set_by": "test",
                "reason": "test",
                "version": 1,
            }
        ),
        encoding="utf-8",
    )


def test_supervisor_quiet_when_file_matches_cme(project_root):
    from modules.timetable import timetable_supervisor as ts

    _install_session_authority_auto(project_root, "2026-04-07")
    tt_file = project_root / "data" / "timetable" / "timetable_current.json"
    tt_file.parent.mkdir(parents=True)
    tt_file.write_text(
        json.dumps({"session_trading_date": "2026-04-07", "streams": []}),
        encoding="utf-8",
    )

    with patch("modules.timetable.cme_session.get_cme_trading_date", return_value="2026-04-07"):
        with patch(
            "modules.timetable.timetable_engine.TimetableEngine.write_execution_timetable_from_master_matrix",
        ) as w:
            ts._supervisor_cycle(project_root)
            w.assert_not_called()


def test_supervisor_drift_logs_no_engine_publish(project_root):
    """Phase 4: mismatch is logged; TimetableEngine is never invoked from supervisor."""
    from modules.timetable import timetable_supervisor as ts

    _install_session_authority_auto(project_root, "2026-04-07")
    tt_file = project_root / "data" / "timetable" / "timetable_current.json"
    tt_file.parent.mkdir(parents=True)
    tt_file.write_text(
        json.dumps({"session_trading_date": "2026-04-06", "streams": [{"stream": "ES1"}]}),
        encoding="utf-8",
    )

    with patch("modules.timetable.cme_session.get_cme_trading_date", return_value="2026-04-07"):
        with patch(
            "modules.timetable.timetable_engine.TimetableEngine.write_execution_timetable_from_master_matrix",
        ) as w:
            ts._supervisor_cycle(project_root)
            w.assert_not_called()


def test_supervisor_drift_no_matrix_lookup(project_root):
    """Validator does not load matrix (repair is out-of-band)."""
    from modules.timetable import timetable_supervisor as ts

    _install_session_authority_auto(project_root, "2026-04-07")
    tt_file = project_root / "data" / "timetable" / "timetable_current.json"
    tt_file.parent.mkdir(parents=True)
    tt_file.write_text(
        json.dumps({"session_trading_date": "2026-04-06", "streams": []}),
        encoding="utf-8",
    )

    with patch("modules.timetable.cme_session.get_cme_trading_date", return_value="2026-04-07"):
        with patch("modules.matrix.file_manager.get_current_master_matrix_df") as gmdf:
            with patch(
                "modules.timetable.timetable_engine.TimetableEngine.write_execution_timetable_from_master_matrix",
            ) as w:
                ts._supervisor_cycle(project_root)
                w.assert_not_called()
                gmdf.assert_not_called()


def test_supervisor_skips_replay_document(project_root):
    from modules.timetable import timetable_supervisor as ts

    _install_session_authority_auto(project_root, "2026-04-07")
    tt_file = project_root / "data" / "timetable" / "timetable_current.json"
    tt_file.parent.mkdir(parents=True)
    tt_file.write_text(
        json.dumps({"replay": True, "session_trading_date": "2020-01-01", "streams": []}),
        encoding="utf-8",
    )

    with patch("modules.timetable.cme_session.get_cme_trading_date", return_value="2026-04-07"):
        with patch(
            "modules.timetable.timetable_engine.TimetableEngine.write_execution_timetable_from_master_matrix",
        ) as w:
            ts._supervisor_cycle(project_root)
            w.assert_not_called()


def test_supervisor_drift_under_publish_lock_still_no_engine(project_root):
    """Supervisor does not participate in publish lock for writes (validator only)."""
    from modules.timetable import timetable_supervisor as ts

    _install_session_authority_auto(project_root, "2026-04-07")
    tt_file = project_root / "data" / "timetable" / "timetable_current.json"
    tt_file.parent.mkdir(parents=True)
    tt_file.write_text(
        json.dumps({"session_trading_date": "2026-04-06", "streams": []}),
        encoding="utf-8",
    )

    with patch("modules.timetable.cme_session.get_cme_trading_date", return_value="2026-04-07"):
        with ts.timetable_publish_blocking():
            with patch(
                "modules.timetable.timetable_engine.TimetableEngine.write_execution_timetable_from_master_matrix",
            ) as w:
                ts._supervisor_cycle(project_root)
                w.assert_not_called()
