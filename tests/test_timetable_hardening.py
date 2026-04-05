"""Timetable / eligibility ordering and CME boundary (hardening)."""

import json
import logging
import sys
from pathlib import Path
from datetime import datetime, timezone

import pytest
import pandas as pd

QTSW2_ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.timetable.cme_session import get_cme_trading_date, get_trading_date_cme
from modules.timetable.eligibility_writer import write_eligibility_file
from modules.timetable.eligibility_session_policy import EligibilityOverwriteBlockedAfterSessionStart
from modules.timetable.timetable_engine import (
    TimetableEngine,
    TimetableWriteBlockedCmeMismatch,
    TimetableLivePublishBlocked,
    _parse_slot_time,
)
from tests.stream_filters_fixtures import install_min_stream_filters
from tests.timetable_matrix_test_utils import matrix_time_valid_for_execution


def test_1730_ct_same_trading_date():
    """17:30 Chicago → session date stays same calendar day (CST)."""
    utc = datetime(2026, 3, 3, 23, 30, 0, tzinfo=timezone.utc)
    assert get_cme_trading_date(utc) == "2026-03-03"
    assert get_trading_date_cme(utc) == "2026-03-03"


def test_1800_ct_rolls_trading_date():
    """18:00 Chicago → next calendar day (weekday; no weekend roll)."""
    utc = datetime(2026, 3, 4, 0, 0, 0, tzinfo=timezone.utc)
    assert get_cme_trading_date(utc) == "2026-03-04"


def test_friday_after_close_rolls_to_monday():
    """Fri >= 18:00 CT → calendar Sat → weekend roll → Mon (parity with Matrix CME session label)."""
    utc = datetime(2026, 3, 7, 0, 1, 0, tzinfo=timezone.utc)  # Fri 2026-03-06 18:01 CST
    assert get_cme_trading_date(utc) == "2026-03-09"


def test_saturday_chicago_is_monday_session():
    utc = datetime(2026, 3, 7, 18, 0, 0, tzinfo=timezone.utc)  # Sat 2026-03-07 13:00 CDT
    assert get_cme_trading_date(utc) == "2026-03-09"


def test_sunday_before_close_still_monday_session():
    utc = datetime(2026, 3, 8, 22, 0, 0, tzinfo=timezone.utc)  # Sun 2026-03-08 17:00 CDT
    assert get_cme_trading_date(utc) == "2026-03-09"


def test_sunday_after_close_monday():
    utc = datetime(2026, 3, 8, 23, 1, 0, tzinfo=timezone.utc)  # Sun 2026-03-08 18:01 CDT
    assert get_cme_trading_date(utc) == "2026-03-09"


def test_publish_timetable_writes_without_eligibility_file(tmp_path, monkeypatch):
    """Execution publish does not require eligibility_{date}.json on disk."""
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(
        "modules.timetable.timetable_engine.get_cme_trading_date",
        lambda _utc=None: "2026-04-01",
    )
    (tmp_path / "data" / "timetable").mkdir(parents=True)
    install_min_stream_filters(tmp_path)
    eng = TimetableEngine(project_root=str(tmp_path))
    d = pd.Timestamp("2026-04-01")
    df = pd.DataFrame(
        [
            {
                "Stream": "ES1",
                "trade_date": d,
                "Time": "09:00",
                "Time Change": "",
                "final_allowed": True,
            }
        ]
    )
    eng.write_execution_timetable_from_master_matrix(df, execution_mode=True)
    cur = tmp_path / "data" / "timetable" / "timetable_current.json"
    assert cur.exists()


def test_publish_succeeds_live_cme(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(
        "modules.timetable.timetable_engine.get_cme_trading_date",
        lambda _utc=None: "2026-04-02",
    )
    tdir = tmp_path / "data" / "timetable"
    tdir.mkdir(parents=True)
    install_min_stream_filters(tmp_path)
    eng = TimetableEngine(project_root=str(tmp_path))
    d = pd.Timestamp("2026-04-02")
    df = pd.DataFrame(
        [
            {
                "Stream": "ES1",
                "trade_date": d,
                "Time": "09:00",
                "Time Change": "",
                "final_allowed": True,
            }
        ]
    )
    eng.write_execution_timetable_from_master_matrix(df, execution_mode=True)
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
    install_min_stream_filters(tmp_path)
    eng = TimetableEngine(project_root=str(tmp_path))
    d = pd.Timestamp("2026-04-02")
    df = pd.DataFrame(
        [
            {
                "Stream": "ES1",
                "trade_date": d,
                "Time": "09:00",
                "Time Change": "",
                "final_allowed": True,
            }
        ]
    )
    streams = eng.build_streams_from_master_matrix(
        df, "2026-04-02", None, True, execution_replay=True
    )
    with pytest.raises(TimetableWriteBlockedCmeMismatch):
        eng._write_execution_timetable_file(
            streams,
            "2026-04-01",
            execution_document_source="master_matrix",
            ledger_writer="pytest",
            ledger_source="test_publish_session_date_not_cme_fails_before_write",
            enforce_cme_live=True,
        )
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
    """Latest row wins: Time Change on newest row; older row does not set slot_time."""
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
                "final_allowed": True,
            },
            {
                "Stream": "RTY1",
                "trade_date": d_cur,
                "Time": "09:00",
                "Time Change": "",
                "final_allowed": True,
            },
        ]
    )
    streams = eng._build_streams_execution_mode(
        df, date_cls(2026, 3, 20), "2026-03-20", {}
    )
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["RTY1"]["slot_time"] == "09:00"


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
                "final_allowed": True,
            },
            {
                "Stream": "RTY1",
                "trade_date": d_cur,
                "Time": "09:00",
                "Time_Change": "",
                "final_allowed": True,
            },
        ]
    )
    streams = eng._build_streams_execution_mode(
        df, date_cls(2026, 3, 20), "2026-03-20", {}
    )
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["RTY1"]["slot_time"] == "09:00"


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
                "final_allowed": True,
            },
            {"Stream": "RTY1", "trade_date": d_cur, "Time": "08:00", "Time Change": "", "final_allowed": True},
        ]
    )
    streams = eng._build_streams_execution_mode(
        df, date_cls(2026, 3, 20), "2026-03-20", {}
    )
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["RTY1"]["slot_time"] == "08:00"


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
                "final_allowed": True,
            },
        ]
    )
    streams = eng._build_streams_execution_mode(
        df, date_cls(2026, 4, 2), "2026-04-02", {}
    )
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["RTY1"]["slot_time"] == "08:00"


def test_execution_mode_streams_sorted_by_slot_time_descending(tmp_path):
    """Published list order: latest slot_time first (same comparator as 09:30 > 08:00 > 07:30)."""
    import pandas as pd
    from datetime import date as date_cls

    _ = tmp_path
    eng = TimetableEngine(project_root=str(QTSW2_ROOT))
    d = pd.Timestamp("2026-03-20")
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
            }
        )
    df = pd.DataFrame(rows)
    streams = eng._build_streams_execution_mode(
        df, date_cls(2026, 3, 20), "2026-03-20", {}
    )
    keys = [_parse_slot_time(s.get("slot_time")) for s in streams]
    assert keys == sorted(keys, reverse=True), [s.get("slot_time") for s in streams]


def test_execution_mode_matrix_time_kept_when_listed_in_exclude_times(tmp_path):
    """exclude_times does not remap published slot; matrix 08:00 stays 08:00 (log-only calendar eval)."""
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
                "final_allowed": True,
            },
        ]
    )
    sf = {"NQ1": {"exclude_times": ["08:00"], "exclude_days_of_week": [], "exclude_days_of_month": []}}
    streams = eng._build_streams_execution_mode(
        df, date_cls(2026, 3, 20), "2026-03-20", sf
    )
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["NQ1"]["slot_time"] == "08:00"


def test_execution_mode_ym_slot_unaffected_by_exclude_times(tmp_path):
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
                "final_allowed": True,
            },
        ]
    )
    sf = {"YM1": {"exclude_times": ["08:00"], "exclude_days_of_week": [], "exclude_days_of_month": []}}
    streams = eng._build_streams_execution_mode(
        df, date_cls(2026, 3, 20), "2026-03-20", sf
    )
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["YM1"]["slot_time"] == "08:00"


def test_execution_mode_nq_matrix_early_time_accepted_from_matrix(tmp_path):
    """S1 07:30 from the matrix is valid for any instrument when it is in session slots."""
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
                "final_allowed": True,
            },
        ]
    )
    streams = eng._build_streams_execution_mode(
        df, date_cls(2026, 3, 20), "2026-03-20", {}
    )
    by_stream = {s["stream"]: s for s in streams}
    assert by_stream["NQ1"]["enabled"] is True
    assert by_stream["NQ1"].get("block_reason") is None
    assert by_stream["NQ1"].get("slot_time") == "07:30"


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
                "final_allowed": True,
            },
        ]
    )
    sf = {"NQ1": {"exclude_times": ["08:00"], "exclude_days_of_week": [], "exclude_days_of_month": []}}
    streams = eng._build_streams_execution_mode(
        df, date_cls(2026, 3, 20), "2026-03-20", sf
    )
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
