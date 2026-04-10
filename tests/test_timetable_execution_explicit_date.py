"""Explicit session date for execution publish + preview (no SessionAuthority inference in API execution path)."""

import json
import sys
from pathlib import Path
from unittest.mock import patch

import pandas as pd
import pytest

QTSW2_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(QTSW2_ROOT))


def test_api_execution_rejects_missing_trading_date():
    from fastapi.testclient import TestClient

    from modules.dashboard.backend.main import app

    client = TestClient(app)
    r = client.post(
        "/api/timetable/execution",
        json={"replay": False, "source": "test", "mode": "live"},
    )
    assert r.status_code == 400
    detail = (r.json() or {}).get("detail", "")
    assert "trading_date" in str(detail).lower()


def test_preview_matches_build_streams_replay(monkeypatch, tmp_path):
    """Preview-only uses same stream-building path as execution (no disk write)."""
    monkeypatch.chdir(tmp_path)
    from tests.stream_filters_fixtures import install_min_stream_filters
    from modules.timetable.timetable_engine import (
        TimetableEngine,
        TimetableExecutionPreviewResult,
    )
    from tests.timetable_matrix_test_utils import matrix_time_valid_for_execution

    install_min_stream_filters(tmp_path)
    eng = TimetableEngine(project_root=str(tmp_path))
    d = pd.Timestamp("2026-04-02")
    rows = []
    for sid in eng.streams:
        sess = "S1" if sid.endswith("1") else "S2"
        slot0 = matrix_time_valid_for_execution(eng, sid)
        rows.append(
            {
                "Stream": sid,
                "trade_date": d,
                "Time": slot0,
                "Time Change": "",
                "final_allowed": True,
                "filter_reasons": "",
            }
        )
    df = pd.DataFrame(rows)
    pub = {"source": "preview", "reason": "test"}
    built = eng.build_streams_from_master_matrix(
        df,
        trade_date="2026-04-02",
        stream_filters=None,
        execution_mode=True,
        matrix_as_of_session=True,
    )
    out = eng.write_execution_timetable_from_master_matrix(
        df,
        trade_date="2026-04-02",
        execution_mode=True,
        replay=False,
        preview_only=True,
        publish_context=pub,
        mode="historical",
    )
    assert isinstance(out, TimetableExecutionPreviewResult)
    assert len(out.streams) == len(built)
    for a, b in zip(out.streams, built):
        assert a["stream"] == b["stream"]
        assert a["enabled"] == b["enabled"]
        assert a["slot_time"] == b["slot_time"]


def test_preview_does_not_write_timetable_current(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    from tests.stream_filters_fixtures import install_min_stream_filters
    from modules.timetable.timetable_engine import TimetableEngine, TimetableExecutionPreviewResult
    from tests.timetable_matrix_test_utils import matrix_time_valid_for_execution

    install_min_stream_filters(tmp_path)
    (tmp_path / "data" / "timetable").mkdir(parents=True)
    tt = tmp_path / "data" / "timetable" / "timetable_current.json"
    tt.write_text(json.dumps({"session_trading_date": "2020-01-01", "streams": []}), encoding="utf-8")
    before = tt.read_text(encoding="utf-8")

    eng = TimetableEngine(project_root=str(tmp_path))
    d = pd.Timestamp("2026-04-02")
    rows = []
    for sid in eng.streams:
        slot0 = matrix_time_valid_for_execution(eng, sid)
        rows.append(
            {
                "Stream": sid,
                "trade_date": d,
                "Time": slot0,
                "Time Change": "",
                "final_allowed": True,
                "filter_reasons": "",
            }
        )
    df = pd.DataFrame(rows)
    out = eng.write_execution_timetable_from_master_matrix(
        df,
        trade_date="2026-04-02",
        execution_mode=True,
        replay=False,
        preview_only=True,
        publish_context={"source": "preview"},
        mode="historical",
    )
    assert isinstance(out, TimetableExecutionPreviewResult)
    assert tt.read_text(encoding="utf-8") == before


def test_preview_timetable_hash_matches_publish_same_date_and_mode(tmp_path, monkeypatch):
    """Same (date + mode) → identical content hash for preview vs live publish (Section 5)."""
    monkeypatch.chdir(tmp_path)
    from tests.stream_filters_fixtures import install_min_stream_filters
    from modules.timetable.timetable_engine import (
        TimetableEngine,
        TimetableExecutionPreviewResult,
        TimetablePublishResult,
    )
    from tests.timetable_matrix_test_utils import matrix_time_valid_for_execution

    install_min_stream_filters(tmp_path)
    (tmp_path / "data" / "timetable").mkdir(parents=True)
    eng = TimetableEngine(project_root=str(tmp_path))
    d = pd.Timestamp("2026-04-02")
    rows = []
    for sid in eng.streams:
        sess = "S1" if sid.endswith("1") else "S2"
        slot0 = matrix_time_valid_for_execution(eng, sid)
        rows.append(
            {
                "Stream": sid,
                "trade_date": d,
                "Time": slot0,
                "Time Change": "",
                "final_allowed": True,
                "filter_reasons": "",
            }
        )
    df = pd.DataFrame(rows)
    prev = eng.write_execution_timetable_from_master_matrix(
        df,
        trade_date="2026-04-02",
        execution_mode=True,
        replay=False,
        preview_only=True,
        mode="live",
        publish_context={"source": "test"},
    )
    pub = eng.write_execution_timetable_from_master_matrix(
        df,
        trade_date="2026-04-02",
        execution_mode=True,
        replay=False,
        mode="live",
        publish_context={"source": "test"},
    )
    assert isinstance(prev, TimetableExecutionPreviewResult)
    assert isinstance(pub, TimetablePublishResult)
    assert prev.timetable_hash == pub.timetable_hash
