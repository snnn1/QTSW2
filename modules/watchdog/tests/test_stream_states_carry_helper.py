"""_watchdog_info_for_stream_today / _latest_prior_watchdog_info_for_stream: today-only vs prior diagnostic."""

from __future__ import annotations

import sys
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.aggregator import (
    _latest_prior_watchdog_info_for_stream,
    _watchdog_info_for_stream_today,
)


def test_today_key_returns_current_info():
    cur = SimpleNamespace(state="OPEN", trading_date="2026-03-31")
    old = SimpleNamespace(state="RANGE_LOCKED", trading_date="2026-03-28")
    d = {("2026-03-28", "ES1"): old, ("2026-03-31", "ES1"): cur}
    assert _watchdog_info_for_stream_today(d, "2026-03-31", "ES1") is cur


def test_today_key_missing_returns_none_even_if_prior_exists():
    fri = SimpleNamespace(state="OPEN", trading_date="2026-03-28")
    d = {("2026-03-28", "ES1"): fri}
    assert _watchdog_info_for_stream_today(d, "2026-03-31", "ES1") is None


def test_prior_helper_returns_latest_prior_lexicographic():
    older = SimpleNamespace(tag="older", trading_date="2026-03-27")
    newer = SimpleNamespace(tag="newer", trading_date="2026-03-28")
    d = {
        ("2026-03-27", "ES1"): older,
        ("2026-03-28", "ES1"): newer,
    }
    assert _latest_prior_watchdog_info_for_stream(d, "2026-03-31", "ES1") is newer


def test_prior_helper_ignores_future_dated_keys():
    """Mis-keyed state on a later calendar day must not be treated as 'prior'."""
    future = SimpleNamespace(tag="future", trading_date="2026-04-07")
    actual_prior = SimpleNamespace(tag="prior", trading_date="2026-04-05")
    d = {
        ("2026-04-05", "ES1"): actual_prior,
        ("2026-04-07", "ES1"): future,
    }
    assert _latest_prior_watchdog_info_for_stream(d, "2026-04-06", "ES1") is actual_prior


def test_prior_helper_none_when_only_today_key():
    cur = SimpleNamespace(state="OPEN")
    d = {("2026-03-31", "ES1"): cur}
    assert _latest_prior_watchdog_info_for_stream(d, "2026-03-31", "ES1") is None
