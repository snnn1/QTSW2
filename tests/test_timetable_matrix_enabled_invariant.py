"""Execution timetable: ``enabled`` follows matrix ``final_allowed`` for the session day (plus valid slot)."""

import json
import sys
from datetime import date as date_cls
from pathlib import Path

import pandas as pd

QTSW2_ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.timetable.timetable_engine import TimetableEngine


def _truthy_final_allowed(v) -> bool:
    return v is True or v == True


def test_timetable_execution_streams_matrix_final_allowed_full_write(tmp_path, monkeypatch):
    """``final_allowed`` on the session day's row gates ``enabled``; block_reason carries matrix reasons."""
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(
        "modules.timetable.timetable_engine.get_cme_trading_date",
        lambda _utc=None: "2026-04-02",
    )
    (tmp_path / "data" / "timetable").mkdir(parents=True)
    eng = TimetableEngine(project_root=str(tmp_path))
    d = pd.Timestamp("2026-04-02")
    rows = []
    for i, sid in enumerate(eng.streams):
        sess = "S1" if sid.endswith("1") else "S2"
        slot0 = eng.session_time_slots[sess][0]
        fa = i % 3 != 0
        rows.append(
            {
                "Stream": sid,
                "trade_date": d,
                "Session": sess,
                "Time": slot0,
                "Time Change": "",
                "final_allowed": fa,
                "filter_reasons": "dow_filter(mon)" if not fa else "",
            }
        )
    df = pd.DataFrame(rows)
    eng.write_execution_timetable_from_master_matrix(df, execution_mode=True)
    doc = json.loads(
        (tmp_path / "data" / "timetable" / "timetable_current.json").read_text(encoding="utf-8")
    )
    by_stream = {s["stream"]: s for s in doc["streams"] if isinstance(s, dict)}
    for r in rows:
        sk = r["Stream"]
        s = by_stream[sk]
        assert "matrix_final_allowed" not in s, sk
        assert "instrument" not in s and "session" not in s and "decision_time" not in s, s
        assert set(s.keys()) <= {"stream", "slot_time", "enabled", "block_reason"}, s
        exp_en = _truthy_final_allowed(r["final_allowed"])
        assert s["enabled"] is exp_en, (sk, s, r)
        if exp_en:
            assert s.get("block_reason") is None, (sk, s)
        else:
            br = s.get("block_reason") or ""
            assert br.startswith("matrix_filter_blocked:"), (sk, br)


def test_build_streams_execution_mode_respects_final_allowed_per_row():
    eng = TimetableEngine(project_root=str(QTSW2_ROOT))
    d = pd.Timestamp("2026-04-02")
    rows = []
    for sid in eng.streams[:4]:
        sess = "S1" if sid.endswith("1") else "S2"
        slot0 = eng.session_time_slots[sess][0]
        fa = sid.endswith("1")
        rows.append(
            {
                "Stream": sid,
                "trade_date": d,
                "Session": sess,
                "Time": slot0,
                "Time Change": "",
                "final_allowed": fa,
                "filter_reasons": "test_reason" if not fa else "",
            }
        )
    df = pd.DataFrame(rows)
    streams = eng._build_streams_execution_mode(
        df, date_cls(2026, 4, 2), "2026-04-02", {}
    )
    by_s = {x["stream"]: x for x in streams}
    for r in rows:
        sk = r["Stream"]
        exp = _truthy_final_allowed(r["final_allowed"])
        assert by_s[sk]["enabled"] is exp, (sk, by_s[sk], r)
        assert "matrix_final_allowed" not in by_s[sk], sk
        assert "instrument" not in by_s[sk] and "session" not in by_s[sk], by_s[sk]
        assert set(by_s[sk].keys()) <= {"stream", "slot_time", "enabled", "block_reason"}, by_s[sk]
        if exp:
            assert by_s[sk].get("block_reason") is None, (sk, by_s[sk])
        else:
            assert (by_s[sk].get("block_reason") or "").startswith("matrix_filter_blocked:"), (
                sk,
                by_s[sk],
            )


def test_execution_mode_calendar_blocked_uses_session_trading_date_dow():
    """DOW/DOM calendar from session_trading_date (2026-04-06 = Monday), not wall-clock."""
    eng = TimetableEngine(project_root=str(QTSW2_ROOT))
    d = pd.Timestamp("2026-04-06")
    rows = []
    for sid in eng.streams:
        sess = "S1" if sid.endswith("1") else "S2"
        slot0 = eng.session_time_slots[sess][0]
        rows.append(
            {
                "Stream": sid,
                "trade_date": d,
                "Session": sess,
                "Time": slot0,
                "Time Change": "",
                "final_allowed": True,
                "filter_reasons": "",
            }
        )
    df = pd.DataFrame(rows)
    sf = {
        "master": {
            "exclude_days_of_week": ["Monday"],
            "exclude_days_of_month": [],
            "exclude_times": [],
        }
    }
    streams = eng._build_streams_execution_mode(df, date_cls(2026, 4, 6), "2026-04-06", sf)
    assert all(not s["enabled"] for s in streams), streams
    for s in streams:
        br = s.get("block_reason") or ""
        assert br.startswith("calendar_filter_blocked:Monday:6"), (s["stream"], br)


def test_execution_mode_final_allowed_from_max_trade_date_per_stream():
    """latest_final_allowed / filter_reasons come from max(trade_date) row per stream, not session slice only."""
    eng = TimetableEngine(project_root=str(QTSW2_ROOT))
    d_session = pd.Timestamp("2026-04-10")
    d_later = pd.Timestamp("2026-04-20")
    rows = []
    for sid in eng.streams:
        sess = "S1" if sid.endswith("1") else "S2"
        slot0 = eng.session_time_slots[sess][0]
        rows.append(
            {
                "Stream": sid,
                "trade_date": d_session,
                "Session": sess,
                "Time": slot0,
                "Time Change": "",
                "final_allowed": True,
                "filter_reasons": "",
            }
        )
        if sid == "ES1":
            rows.append(
                {
                    "Stream": sid,
                    "trade_date": d_later,
                    "Session": sess,
                    "Time": slot0,
                    "Time Change": "",
                    "final_allowed": False,
                    "filter_reasons": "later_row",
                }
            )
    df = pd.DataFrame(rows)
    streams = eng._build_streams_execution_mode(df, date_cls(2026, 4, 10), "2026-04-10", {})
    by_s = {x["stream"]: x for x in streams}
    assert by_s["ES1"]["enabled"] is False
    assert (by_s["ES1"].get("block_reason") or "").startswith("matrix_filter_blocked:"), by_s["ES1"]
    assert by_s["NQ1"]["enabled"] is True, by_s["NQ1"]
