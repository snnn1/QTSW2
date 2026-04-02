"""Timetable / eligibility ordering and CME boundary (hardening)."""

import json
import logging
import sys
from pathlib import Path
from datetime import datetime, timezone

import pytest

QTSW2_ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.timetable.cme_session import get_cme_trading_date, get_trading_date_cme
from modules.timetable.timetable_engine import (
    TimetableEngine,
    TimetableWriteBlockedMissingEligibility,
    TimetableWriteBlockedCmeMismatch,
    TimetableLivePublishBlocked,
)
from modules.timetable.eligibility_writer import write_eligibility_file
from modules.timetable.eligibility_session_policy import EligibilityOverwriteBlockedAfterSessionStart


def test_1730_ct_same_trading_date():
    """17:30 Chicago → session date stays same calendar day (CST)."""
    utc = datetime(2026, 3, 3, 23, 30, 0, tzinfo=timezone.utc)
    assert get_cme_trading_date(utc) == "2026-03-03"
    assert get_trading_date_cme(utc) == "2026-03-03"


def test_1800_ct_rolls_trading_date():
    """18:00 Chicago → next calendar day."""
    utc = datetime(2026, 3, 4, 0, 0, 0, tzinfo=timezone.utc)
    assert get_cme_trading_date(utc) == "2026-03-04"


def test_publish_timetable_without_eligibility_fails(tmp_path, monkeypatch):
    """No eligibility file → publish must not write timetable."""
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(
        "modules.timetable.timetable_engine.get_cme_trading_date",
        lambda _utc=None: "2026-04-01",
    )
    (tmp_path / "data" / "timetable").mkdir(parents=True)
    eng = TimetableEngine(project_root=str(tmp_path))
    streams = [
        {
            "stream": "ES1",
            "instrument": "ES",
            "session": "S1",
            "slot_time": "09:00",
            "decision_time": "09:00",
            "enabled": True,
        }
    ]
    with pytest.raises(TimetableWriteBlockedMissingEligibility):
        eng.publish_execution_timetable_current(streams, "2026-04-01")
    cur = tmp_path / "data" / "timetable" / "timetable_current.json"
    assert not cur.exists()


def test_publish_succeeds_when_eligibility_matches(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(
        "modules.timetable.timetable_engine.get_cme_trading_date",
        lambda _utc=None: "2026-04-02",
    )
    tdir = tmp_path / "data" / "timetable"
    tdir.mkdir(parents=True)
    write_eligibility_file(
        [{"stream": "ES1", "enabled": True}],
        "2026-04-02",
        str(tdir),
        overwrite=True,
    )
    eng = TimetableEngine(project_root=str(tmp_path))
    streams = [
        {
            "stream": "ES1",
            "instrument": "ES",
            "session": "S1",
            "slot_time": "09:00",
            "decision_time": "09:00",
            "enabled": True,
        }
    ]
    eng.publish_execution_timetable_current(streams, "2026-04-02")
    cur = tdir / "timetable_current.json"
    assert cur.exists()
    doc = json.loads(cur.read_text(encoding="utf-8"))
    assert doc.get("session_trading_date") == "2026-04-02"


def test_write_execution_timetable_blocks_without_scratch_dir(tmp_path, monkeypatch):
    """Analyzer dataframe path must not write live timetable_current."""
    monkeypatch.chdir(tmp_path)
    eng = TimetableEngine(project_root=str(tmp_path))
    import pandas as pd
    df = pd.DataFrame(
        [
            {
                "stream_id": "ES1",
                "session": "S1",
                "selected_time": "09:00",
                "allowed": True,
                "trade_date": "2026-04-01",
            }
        ]
    )
    with pytest.raises(TimetableLivePublishBlocked):
        eng.write_execution_timetable(df, "2026-04-01")


def test_matrix_non_execution_mode_requires_output_dir(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    import pandas as pd
    eng = TimetableEngine(project_root=str(tmp_path))
    df = pd.DataFrame([{"Stream": "ES1", "trade_date": pd.Timestamp("2026-04-01")}])
    with pytest.raises(TimetableLivePublishBlocked):
        eng.write_execution_timetable_from_master_matrix(df, execution_mode=False)


def test_publish_session_date_not_cme_fails_before_write(tmp_path, monkeypatch):
    """Live publish: session_trading_date must match CME for now (no silent write)."""
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(
        "modules.timetable.timetable_engine.get_cme_trading_date",
        lambda _utc=None: "2026-04-02",
    )
    tdir = tmp_path / "data" / "timetable"
    tdir.mkdir(parents=True)
    eng = TimetableEngine(project_root=str(tmp_path))
    streams = [
        {
            "stream": "ES1",
            "instrument": "ES",
            "session": "S1",
            "slot_time": "09:00",
            "decision_time": "09:00",
            "enabled": True,
        }
    ]
    with pytest.raises(TimetableWriteBlockedCmeMismatch):
        eng.publish_execution_timetable_current(streams, "2026-04-01")
    assert not (tdir / "timetable_current.json").exists()


def test_eligibility_overwrite_blocked_after_session_start(tmp_path, monkeypatch):
    """overwrite=True must fail closed once execution session has started for that session date."""
    monkeypatch.chdir(tmp_path)
    tdir = tmp_path / "data" / "timetable"
    tdir.mkdir(parents=True)
    (tdir / "eligibility_2026-04-02.json").write_text(
        '{"session_trading_date":"2026-04-02","eligible_streams":[]}', encoding="utf-8"
    )
    monkeypatch.setattr(
        "modules.timetable.eligibility_session_policy.is_execution_session_started_for_date",
        lambda _sd, now_utc=None: True,
    )
    with pytest.raises(EligibilityOverwriteBlockedAfterSessionStart):
        write_eligibility_file(
            [{"stream": "ES1", "enabled": True}],
            "2026-04-02",
            str(tdir),
            overwrite=True,
        )


def test_eligibility_overwrite_allowed_before_session_when_forced_window(tmp_path, monkeypatch):
    """Before session open, overwrite=True may proceed (operator workflow)."""
    monkeypatch.chdir(tmp_path)
    tdir = tmp_path / "data" / "timetable"
    tdir.mkdir(parents=True)
    (tdir / "eligibility_2026-04-02.json").write_text(
        '{"session_trading_date":"2026-04-02","eligible_streams":[]}', encoding="utf-8"
    )
    monkeypatch.setattr(
        "modules.timetable.eligibility_session_policy.is_execution_session_started_for_date",
        lambda _sd, now_utc=None: False,
    )
    path = write_eligibility_file(
        [{"stream": "ES1", "enabled": True}],
        "2026-04-02",
        str(tdir),
        overwrite=True,
    )
    assert path is not None
    assert path.exists()


def test_execution_mode_time_change_overrides_time_when_both_calendar_days_present(tmp_path):
    """Previous-day row + current day in matrix: Time Change on previous row sets slot (execution_mode)."""
    import pandas as pd
    from datetime import date as date_cls

    _ = tmp_path
    eng = TimetableEngine(project_root=str(QTSW2_ROOT))
    d_prev = pd.Timestamp("2026-03-19")
    d_cur = pd.Timestamp("2026-03-20")
    df = pd.DataFrame(
        [
            {
                "Stream": "RTY1",
                "trade_date": d_prev,
                "Time": "07:30",
                "Time Change": "08:00",
            },
            {
                "Stream": "RTY1",
                "trade_date": d_cur,
                "Time": "09:00",
                "Time Change": "",
            },
        ]
    )
    elig = {s: {"enabled": False, "reason": "test"} for s in eng.streams}
    elig["RTY1"] = {"enabled": True, "reason": ""}
    streams = eng._build_streams_execution_mode(df, date_cls(2026, 3, 20), elig, {})
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["RTY1"]["slot_time"] == "08:00"


def test_execution_mode_time_change_column_time_underscore_and_timestamp_rhs(tmp_path):
    import pandas as pd
    from datetime import date as date_cls

    _ = tmp_path
    eng = TimetableEngine(project_root=str(QTSW2_ROOT))
    d_prev = pd.Timestamp("2026-03-19")
    d_cur = pd.Timestamp("2026-03-20")
    df = pd.DataFrame(
        [
            {
                "Stream": "RTY1",
                "trade_date": d_prev,
                "Time": "07:30",
                "Time_Change": "2026-03-20 08:00:00",
            },
            {
                "Stream": "RTY1",
                "trade_date": d_cur,
                "Time": "09:00",
                "Time_Change": "",
            },
        ]
    )
    elig = {s: {"enabled": False, "reason": "test"} for s in eng.streams}
    elig["RTY1"] = {"enabled": True, "reason": ""}
    streams = eng._build_streams_execution_mode(df, date_cls(2026, 3, 20), elig, {})
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["RTY1"]["slot_time"] == "08:00"


def test_execution_mode_arrow_form_time_change(tmp_path):
    import pandas as pd
    from datetime import date as date_cls

    _ = tmp_path
    eng = TimetableEngine(project_root=str(QTSW2_ROOT))
    d_prev = pd.Timestamp("2026-03-19")
    d_cur = pd.Timestamp("2026-03-20")
    df = pd.DataFrame(
        [
            {
                "Stream": "RTY1",
                "trade_date": d_prev,
                "Time": "07:30",
                "Time Change": "07:30 -> 09:00",
            },
            {"Stream": "RTY1", "trade_date": d_cur, "Time": "08:00", "Time Change": ""},
        ]
    )
    elig = {s: {"enabled": False, "reason": "test"} for s in eng.streams}
    elig["RTY1"] = {"enabled": True, "reason": ""}
    streams = eng._build_streams_execution_mode(df, date_cls(2026, 3, 20), elig, {})
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["RTY1"]["slot_time"] == "09:00"


def test_execution_mode_time_change_when_only_previous_day_in_matrix(tmp_path):
    """Matrix has no row for CME day T; slot for T must still use Time Change from T-1."""
    import pandas as pd
    from datetime import date as date_cls

    _ = tmp_path
    eng = TimetableEngine(project_root=str(QTSW2_ROOT))
    d_prev = pd.Timestamp("2026-04-01")
    df = pd.DataFrame(
        [
            {
                "Stream": "RTY1",
                "trade_date": d_prev,
                "Time": "07:30",
                "Time Change": "08:00",
            },
        ]
    )
    elig = {s: {"enabled": False, "reason": "test"} for s in eng.streams}
    elig["RTY1"] = {"enabled": True, "reason": ""}
    streams = eng._build_streams_execution_mode(df, date_cls(2026, 4, 2), elig, {})
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["RTY1"]["slot_time"] == "08:00"


def test_execution_mode_shifts_when_matrix_slot_in_merged_exclude_times(tmp_path):
    """Merged exclude_times: matrix 08:00 excluded → publish next valid slot (09:00 for NQ)."""
    import pandas as pd
    from datetime import date as date_cls

    _ = tmp_path
    eng = TimetableEngine(project_root=str(QTSW2_ROOT))
    d = pd.Timestamp("2026-03-20")
    df = pd.DataFrame(
        [
            {
                "Stream": "NQ1",
                "trade_date": d,
                "Time": "08:00",
                "Time Change": "",
            },
        ]
    )
    elig = {s: {"enabled": False, "reason": "test"} for s in eng.streams}
    elig["NQ1"] = {"enabled": True, "reason": ""}
    sf = {"NQ1": {"exclude_times": ["08:00"], "exclude_days_of_week": [], "exclude_days_of_month": []}}
    streams = eng._build_streams_execution_mode(df, date_cls(2026, 3, 20), elig, sf)
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["NQ1"]["slot_time"] == "09:00"


def test_execution_mode_ym_excluded_middle_slot_shifts_forward(tmp_path):
    import pandas as pd
    from datetime import date as date_cls

    _ = tmp_path
    eng = TimetableEngine(project_root=str(QTSW2_ROOT))
    d = pd.Timestamp("2026-03-20")
    df = pd.DataFrame(
        [
            {
                "Stream": "YM1",
                "trade_date": d,
                "Time": "08:00",
                "Time Change": "",
            },
        ]
    )
    elig = {s: {"enabled": False, "reason": "test"} for s in eng.streams}
    elig["YM1"] = {"enabled": True, "reason": ""}
    sf = {"YM1": {"exclude_times": ["08:00"], "exclude_days_of_week": [], "exclude_days_of_month": []}}
    streams = eng._build_streams_execution_mode(df, date_cls(2026, 3, 20), elig, sf)
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["YM1"]["slot_time"] == "09:00"


def test_execution_mode_nq_matrix_early_time_remapped_for_write_guard(tmp_path):
    """S1 07:30 in matrix for non-YM is remapped to the next instrument-allowed slot (08:00)."""
    import pandas as pd
    from datetime import date as date_cls

    _ = tmp_path
    eng = TimetableEngine(project_root=str(QTSW2_ROOT))
    d = pd.Timestamp("2026-03-20")
    df = pd.DataFrame(
        [
            {
                "Stream": "NQ1",
                "trade_date": d,
                "Time": "07:30",
                "Time Change": "",
            },
        ]
    )
    elig = {s: {"enabled": False, "reason": "test"} for s in eng.streams}
    elig["NQ1"] = {"enabled": True, "reason": ""}
    streams = eng._build_streams_execution_mode(df, date_cls(2026, 3, 20), elig, {})
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["NQ1"]["slot_time"] == "08:00"


def test_execution_mode_matrix_time_unchanged_when_not_excluded_in_timetable(tmp_path):
    import pandas as pd
    from datetime import date as date_cls

    _ = tmp_path
    eng = TimetableEngine(project_root=str(QTSW2_ROOT))
    d = pd.Timestamp("2026-03-20")
    df = pd.DataFrame(
        [
            {
                "Stream": "NQ1",
                "trade_date": d,
                "Time": "09:00",
                "Time Change": "",
            },
        ]
    )
    elig = {s: {"enabled": False, "reason": "test"} for s in eng.streams}
    elig["NQ1"] = {"enabled": True, "reason": ""}
    sf = {"NQ1": {"exclude_times": ["08:00"], "exclude_days_of_week": [], "exclude_days_of_month": []}}
    streams = eng._build_streams_execution_mode(df, date_cls(2026, 3, 20), elig, sf)
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["NQ1"]["slot_time"] == "09:00"


def test_eligibility_bypass_session_guard_allows_overwrite(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    tdir = tmp_path / "data" / "timetable"
    tdir.mkdir(parents=True)
    (tdir / "eligibility_2026-04-02.json").write_text(
        '{"session_trading_date":"2026-04-02","eligible_streams":[]}', encoding="utf-8"
    )
    monkeypatch.setattr(
        "modules.timetable.eligibility_session_policy.is_execution_session_started_for_date",
        lambda _sd, now_utc=None: True,
    )
    path = write_eligibility_file(
        [{"stream": "ES1", "enabled": True}],
        "2026-04-02",
        str(tdir),
        overwrite=True,
        bypass_session_immutability_guard=True,
        bypass_audit_operator="pytest",
        bypass_audit_source="tests/test_timetable_hardening.py",
        bypass_audit_reason="unit_test_bypass_path",
    )
    assert path is not None


def test_eligibility_bypass_emits_critical_structured_event(caplog, tmp_path, monkeypatch):
    """Emergency bypass must emit one CRITICAL JSON line for SIEM/audit (operator/source/reason)."""
    monkeypatch.chdir(tmp_path)
    tdir = tmp_path / "data" / "timetable"
    tdir.mkdir(parents=True)
    (tdir / "eligibility_2026-04-02.json").write_text(
        '{"session_trading_date":"2026-04-02","eligible_streams":[]}', encoding="utf-8"
    )
    monkeypatch.setattr(
        "modules.timetable.eligibility_session_policy.is_execution_session_started_for_date",
        lambda _sd, now_utc=None: True,
    )
    caplog.set_level(logging.CRITICAL)
    write_eligibility_file(
        [{"stream": "ES1", "enabled": True}],
        "2026-04-02",
        str(tdir),
        overwrite=True,
        bypass_session_immutability_guard=True,
        bypass_audit_operator="pytest_operator",
        bypass_audit_source="test_eligibility_bypass_emits_critical_structured_event",
        bypass_audit_reason="verify_audit_log",
    )
    critical = [r for r in caplog.records if r.levelno == logging.CRITICAL]
    assert critical, "expected CRITICAL log for immutability bypass"
    payload = json.loads(critical[0].getMessage())
    assert payload.get("event") == "ELIGIBILITY_IMMUTABILITY_BYPASS"
    assert payload.get("session_trading_date") == "2026-04-02"
    assert payload.get("operator") == "pytest_operator"
    assert payload.get("source") == "test_eligibility_bypass_emits_critical_structured_event"
    assert payload.get("reason") == "verify_audit_log"
