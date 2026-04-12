"""resolve_live_execution_session_trading_date: live file session vs canonical CME."""

from datetime import datetime, timezone

from modules.timetable.cme_session import resolve_live_execution_session_trading_date


def test_ok_when_file_matches_cme():
    utc = datetime(2026, 4, 1, 17, 0, tzinfo=timezone.utc)  # morning CT same calendar day
    eff, reason = resolve_live_execution_session_trading_date("2026-04-01", utc, is_replay_document=False)
    assert eff == "2026-04-01"
    assert reason == "ok"


def test_clamped_when_file_one_day_ahead_before_rolover():
    # ~07:00 Chicago on 2026-04-01 → CME session 2026-04-01; file says 2026-04-02
    utc = datetime(2026, 4, 1, 12, 0, 0, tzinfo=timezone.utc)
    eff, reason = resolve_live_execution_session_trading_date("2026-04-02", utc, is_replay_document=False)
    assert eff == "2026-04-01"
    assert reason == "clamped_ahead"


def test_reject_when_file_stale():
    utc = datetime(2026, 4, 2, 12, 0, 0, tzinfo=timezone.utc)
    eff, reason = resolve_live_execution_session_trading_date("2026-04-01", utc, is_replay_document=False)
    assert eff is None
    assert reason == "reject_mismatch"


def test_replay_ignores_cme():
    utc = datetime(2026, 4, 2, 12, 0, 0, tzinfo=timezone.utc)
    eff, reason = resolve_live_execution_session_trading_date("2026-04-01", utc, is_replay_document=True)
    assert eff == "2026-04-01"
    assert reason == "replay_ok"
