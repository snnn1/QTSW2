"""Execution publish refuses empty merged stream_filters; optional API payload satisfies merge."""

import json
import sys
from pathlib import Path

import pandas as pd
import pytest

QTSW2_ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.timetable.timetable_engine import TimetableEngine
from tests.stream_filters_fixtures import install_min_stream_filters


def _minimal_matrix_df(eng: TimetableEngine, trade_ts: pd.Timestamp) -> pd.DataFrame:
    rows = []
    for sid in eng.streams:
        sess = "S1" if sid.endswith("1") else "S2"
        slot0 = eng.session_time_slots[sess][0]
        rows.append(
            {
                "Stream": sid,
                "trade_date": trade_ts,
                "Session": sess,
                "Time": slot0,
                "Time Change": "",
                "final_allowed": True,
                "filter_reasons": "",
            }
        )
    return pd.DataFrame(rows)


def test_execution_publish_blocked_when_no_stream_filters_merged(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(
        "modules.timetable.timetable_engine.get_cme_trading_date",
        lambda _utc=None: "2026-04-02",
    )
    (tmp_path / "data" / "timetable").mkdir(parents=True)
    eng = TimetableEngine(project_root=str(tmp_path))
    df = _minimal_matrix_df(eng, pd.Timestamp("2026-04-02"))
    with pytest.raises(RuntimeError, match="TIMETABLE_PUBLISH_BLOCKED"):
        eng.write_execution_timetable_from_master_matrix(
            df, execution_mode=True, stream_filters=None
        )


def test_execution_publish_ok_with_min_config_on_disk(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(
        "modules.timetable.timetable_engine.get_cme_trading_date",
        lambda _utc=None: "2026-04-02",
    )
    (tmp_path / "data" / "timetable").mkdir(parents=True)
    install_min_stream_filters(tmp_path)
    eng = TimetableEngine(project_root=str(tmp_path))
    df = _minimal_matrix_df(eng, pd.Timestamp("2026-04-02"))
    eng.write_execution_timetable_from_master_matrix(
        df, execution_mode=True, stream_filters=None
    )
    cur = tmp_path / "data" / "timetable" / "timetable_current.json"
    assert cur.is_file()
    doc = json.loads(cur.read_text(encoding="utf-8"))
    assert doc.get("session_trading_date") == "2026-04-02"
    assert isinstance(doc.get("streams"), list) and len(doc["streams"]) > 0


def test_execution_publish_ok_with_payload_only_no_disk_file(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(
        "modules.timetable.timetable_engine.get_cme_trading_date",
        lambda _utc=None: "2026-04-02",
    )
    (tmp_path / "data" / "timetable").mkdir(parents=True)
    eng = TimetableEngine(project_root=str(tmp_path))
    df = _minimal_matrix_df(eng, pd.Timestamp("2026-04-02"))
    sf = {
        "master": {
            "exclude_days_of_week": [],
            "exclude_days_of_month": [],
            "exclude_times": [],
        }
    }
    eng.write_execution_timetable_from_master_matrix(
        df, execution_mode=True, stream_filters=sf
    )
    doc = json.loads(
        (tmp_path / "data" / "timetable" / "timetable_current.json").read_text(
            encoding="utf-8"
        )
    )
    by_s = {s["stream"]: s for s in doc["streams"] if isinstance(s, dict)}
    for sid in eng.streams:
        assert sid in by_s and by_s[sid].get("enabled") is True, by_s[sid]
