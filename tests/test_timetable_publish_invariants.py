"""Invariant tests for matrix-only live timetable publish and shared content hashing."""

import json
import sys
from pathlib import Path

import pandas as pd

QTSW2_ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.timetable.timetable_engine import TimetableEngine
from modules.timetable.timetable_content_hash import compute_content_hash_from_document
from modules.watchdog.timetable_poller import _compute_content_hash as poller_content_hash


def test_publish_execution_timetable_current_removed():
    """Bypass API removed: enabled/block_reason must never be supplied by arbitrary caller payload."""
    assert not hasattr(TimetableEngine, "publish_execution_timetable_current")


def test_watchdog_poller_hash_matches_timetable_content_hash():
    """Watchdog and robot/Python ledger use the same canonical document hash."""
    doc = {
        "as_of": "ignored",
        "source": "ignored",
        "session_trading_date": "2026-04-01",
        "timezone": "America/Chicago",
        "streams": [
            {
                "stream": "ES1",
                "slot_time": "09:00",
                "enabled": True,
            },
            {
                "stream": "NQ1",
                "slot_time": "10:00",
                "enabled": False,
                "block_reason": "no_valid_execution_slot",
            },
        ],
    }
    assert poller_content_hash(doc) == compute_content_hash_from_document(doc)


def test_live_publish_final_allowed_false_disables_all_streams(tmp_path, monkeypatch):
    """Execution publish: final_allowed false on session-day rows disables streams; JSON stream rows stay slim."""
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(
        "modules.timetable.timetable_engine.get_cme_trading_date",
        lambda _utc=None: "2026-04-02",
    )
    (tmp_path / "data" / "timetable").mkdir(parents=True)
    eng = TimetableEngine(project_root=str(tmp_path))
    d = pd.Timestamp("2026-04-02")
    rows = []
    for sid in eng.streams:
        sess = "S1" if sid.endswith("1") else "S2"
        slot0 = eng.session_time_slots[sess][0]
        rows.append(
            {
                "Stream": sid,
                "trade_date": d,
                "Time": slot0,
                "Time Change": "",
                "final_allowed": False,
                "filter_reasons": "scf_blocked",
            }
        )
    df = pd.DataFrame(rows)
    eng.write_execution_timetable_from_master_matrix(df, execution_mode=True)
    doc = json.loads(
        (tmp_path / "data" / "timetable" / "timetable_current.json").read_text(encoding="utf-8")
    )
    for s in doc["streams"]:
        if not isinstance(s, dict):
            continue
        assert "matrix_final_allowed" not in s, s
        assert s.get("enabled") is False, s
        br = s.get("block_reason") or ""
        assert br.startswith("matrix_filter_blocked:"), s
