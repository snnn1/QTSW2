"""Exclusion-aware sequencer: merged excludes, pick_best init, empty selectable → []."""

import sys
from pathlib import Path

import pandas as pd

QTSW2_ROOT = Path(__file__).parent.parent.parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.matrix.sequencer_logic import process_stream_daily, decide_time_change
from modules.matrix.utils import normalize_time
from modules.matrix.stream_manager import merged_exclude_times_normalized_set
def _s1_days_es1(*days_and_results):
    rows = []
    for d, triples in days_and_results:
        ts = pd.Timestamp(d)
        for tt, res in triples:
            rows.append(
                {
                    "Stream": "ES1",
                    "trade_date": ts,
                    "Time": tt,
                    "Result": res,
                    "Session": "S1",
                }
            )
    return pd.DataFrame(rows)


def test_merged_exclude_times_unions_master():
    sf = {
        "master": {"exclude_times": ["08:00"]},
        "ES1": {"exclude_times": ["09:00"]},
    }
    assert merged_exclude_times_normalized_set("ES1", sf) == {"08:00", "09:00"}


def test_process_stream_daily_never_selects_excluded_time():
    df = _s1_days_es1(
        ("2026-01-05", [("07:30", "WIN"), ("08:00", "WIN"), ("09:00", "WIN")]),
    )
    full_filters = {
        "master": {"exclude_times": ["08:00"]},
        "ES1": {"exclude_times": []},
    }
    out = process_stream_daily(df, "ES1", full_filters, return_state=False)
    times = {normalize_time(str(x["Time"])) for x in out}
    assert "08:00" not in times


def test_exclude_08_initial_best_is_07_30_when_histories_flat():
    df = _s1_days_es1(
        ("2026-01-05", [("07:30", "WIN"), ("08:00", "WIN"), ("09:00", "WIN")]),
    )
    out = process_stream_daily(df, "ES1", {"ES1": {"exclude_times": ["08:00"]}})
    assert len(out) == 1
    assert normalize_time(str(out[0]["Time"])) == "07:30"


def test_all_session_times_excluded_returns_empty_no_raise():
    df = _s1_days_es1(
        ("2026-01-05", [("07:30", "WIN"), ("08:00", "WIN"), ("09:00", "WIN")]),
    )
    assert process_stream_daily(df, "ES1", {"ES1": {"exclude_times": ["07:30", "08:00", "09:00"]}}) == []


def test_decide_time_change_only_on_loss_and_strictly_better():
    # Current at 07:30 with sum 0; 09:00 has sum 5 — no switch without LOSS
    hist = {"07:30": [1, 1], "08:00": [], "09:00": [1, 1, 1, 1, 1]}
    sel = ["07:30", "08:00", "09:00"]
    assert decide_time_change("07:30", "WIN", sum(hist["07:30"]), hist, sel, "S1") is None
    assert decide_time_change("07:30", "LOSS", sum(hist["07:30"]), hist, sel, "S1") == "09:00"
    # Equal best_other vs current: no switch
    hist2 = {"07:30": [1, 1], "08:00": [], "09:00": [1, 1]}
    assert decide_time_change("07:30", "LOSS", 2, hist2, sel, "S1") is None


def test_no_daily_re_rank_histories_flat_stay_on_first_selectable():
    df = _s1_days_es1(
        ("2026-01-06", [("07:30", "WIN"), ("08:00", "WIN"), ("09:00", "WIN")]),
        ("2026-01-07", [("07:30", "WIN"), ("08:00", "WIN"), ("09:00", "WIN")]),
    )
    out = process_stream_daily(df, "ES1", {"ES1": {"exclude_times": ["08:00"]}})
    assert normalize_time(str(out[0]["Time"])) == "07:30"
    assert normalize_time(str(out[1]["Time"])) == "07:30"
