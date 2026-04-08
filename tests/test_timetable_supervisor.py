"""Unit tests for Timetable Supervisor (session alignment loop)."""

import json
from unittest.mock import MagicMock, patch

import pandas as pd
import pytest


@pytest.fixture
def project_root(tmp_path):
    (tmp_path / "data" / "master_matrix").mkdir(parents=True)
    (tmp_path / "data" / "analyzed").mkdir(parents=True)
    return tmp_path


def test_supervisor_skips_when_file_matches_cme(project_root):
    from modules.timetable import timetable_supervisor as ts

    tt_file = project_root / "data" / "timetable" / "timetable_current.json"
    tt_file.parent.mkdir(parents=True)
    tt_file.write_text(
        json.dumps({"session_trading_date": "2026-04-07", "streams": []}),
        encoding="utf-8",
    )

    with patch("modules.timetable.cme_session.get_cme_trading_date", return_value="2026-04-07"):
        with patch("modules.matrix.file_manager.get_current_master_matrix_df", return_value=pd.DataFrame({"x": [1]})):
            published = []

            def _capture(*args, **kwargs):
                published.append(1)
                return MagicMock(timetable_hash="h")

            with patch(
                "modules.timetable.timetable_engine.TimetableEngine.write_execution_timetable_from_master_matrix",
                new=_capture,
            ):
                ts._supervisor_cycle(project_root)
    assert published == []


def test_supervisor_publishes_on_mismatch(project_root):
    from modules.timetable import timetable_supervisor as ts

    tt_file = project_root / "data" / "timetable" / "timetable_current.json"
    tt_file.parent.mkdir(parents=True)
    tt_file.write_text(
        json.dumps({"session_trading_date": "2026-04-06", "streams": [{"stream": "ES1"}]}),
        encoding="utf-8",
    )

    df = pd.DataFrame({"Stream": ["ES1"], "trade_date": ["2026-04-07"]})

    with patch("modules.timetable.cme_session.get_cme_trading_date", return_value="2026-04-07"):
        with patch("modules.matrix.file_manager.get_current_master_matrix_df", return_value=df):
            calls = []

            def _capture(*args, **kwargs):
                calls.append(kwargs.get("publish_context") or {})
                return MagicMock(timetable_hash="abc", changed=True)

            with patch(
                "modules.timetable.timetable_engine.TimetableEngine.write_execution_timetable_from_master_matrix",
                new=_capture,
            ):
                ts._supervisor_cycle(project_root)

    assert len(calls) == 1
    assert calls[0].get("reason") == "session_roll_supervisor"
    assert calls[0].get("source") == "auto"


def test_supervisor_no_publish_when_matrix_missing(project_root):
    from modules.timetable import timetable_supervisor as ts

    tt_file = project_root / "data" / "timetable" / "timetable_current.json"
    tt_file.parent.mkdir(parents=True)
    tt_file.write_text(
        json.dumps({"session_trading_date": "2026-04-06", "streams": []}),
        encoding="utf-8",
    )

    with patch("modules.timetable.cme_session.get_cme_trading_date", return_value="2026-04-07"):
        with patch("modules.matrix.file_manager.get_current_master_matrix_df", return_value=None):
            with patch(
                "modules.timetable.timetable_engine.TimetableEngine.write_execution_timetable_from_master_matrix",
            ) as w:
                ts._supervisor_cycle(project_root)
                w.assert_not_called()


def test_supervisor_skips_replay_document(project_root):
    from modules.timetable import timetable_supervisor as ts

    tt_file = project_root / "data" / "timetable" / "timetable_current.json"
    tt_file.parent.mkdir(parents=True)
    tt_file.write_text(
        json.dumps({"replay": True, "session_trading_date": "2020-01-01", "streams": []}),
        encoding="utf-8",
    )

    with patch("modules.timetable.cme_session.get_cme_trading_date", return_value="2026-04-07"):
        with patch("modules.matrix.file_manager.get_current_master_matrix_df", return_value=pd.DataFrame({"x": [1]})):
            with patch(
                "modules.timetable.timetable_engine.TimetableEngine.write_execution_timetable_from_master_matrix",
            ) as w:
                ts._supervisor_cycle(project_root)
                w.assert_not_called()


def test_publish_lock_nonblocking_skips_when_held(project_root):
    from modules.timetable import timetable_supervisor as ts

    tt_file = project_root / "data" / "timetable" / "timetable_current.json"
    tt_file.parent.mkdir(parents=True)
    tt_file.write_text(
        json.dumps({"session_trading_date": "2026-04-06", "streams": []}),
        encoding="utf-8",
    )

    df = pd.DataFrame({"Stream": ["ES1"]})

    with patch("modules.timetable.cme_session.get_cme_trading_date", return_value="2026-04-07"):
        with patch("modules.matrix.file_manager.get_current_master_matrix_df", return_value=df):
            with ts.timetable_publish_blocking():
                with patch(
                    "modules.timetable.timetable_engine.TimetableEngine.write_execution_timetable_from_master_matrix",
                ) as w:
                    ts._supervisor_cycle(project_root)
                    w.assert_not_called()
