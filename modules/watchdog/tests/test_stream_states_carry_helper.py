"""_resolve_watchdog_info_for_timetable_row: current key vs prior-session fallback."""

from __future__ import annotations

import sys
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.aggregator import _resolve_watchdog_info_for_timetable_row


def test_prefers_current_trading_date_key():
    cur = SimpleNamespace(state="OPEN")
    old = SimpleNamespace(state="RANGE_LOCKED")
    d = {("2026-03-28", "ES1"): old, ("2026-03-31", "ES1"): cur}
    assert _resolve_watchdog_info_for_timetable_row(d, "2026-03-31", "ES1") is cur


def test_falls_back_to_latest_prior_trading_date():
    fri = SimpleNamespace(state="OPEN")
    d = {("2026-03-28", "ES1"): fri}
    assert _resolve_watchdog_info_for_timetable_row(d, "2026-03-31", "ES1") is fri


def test_falls_back_lexicographic_latest_prior_when_multiple():
    older = SimpleNamespace(tag="older")
    newer = SimpleNamespace(tag="newer")
    d = {
        ("2026-03-27", "ES1"): older,
        ("2026-03-28", "ES1"): newer,
    }
    assert _resolve_watchdog_info_for_timetable_row(d, "2026-03-31", "ES1") is newer
