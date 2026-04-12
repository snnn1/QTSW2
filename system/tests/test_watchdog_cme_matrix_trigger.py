"""CME rollover + startup timetable alignment → matrix pipeline scheduling."""

import asyncio
from datetime import datetime, timezone
from unittest.mock import patch


def test_first_poll_initializes_cme_day_only_when_timetable_aligned():
    from modules.watchdog.aggregator import WatchdogAggregator

    agg = WatchdogAggregator()
    utc = datetime(2026, 4, 1, 12, 0, tzinfo=timezone.utc)
    with patch(
        "modules.timetable.cme_session.get_cme_trading_date",
        return_value="2026-04-01",
    ):
        with patch.object(
            agg,
            "_get_startup_timetable_session_and_replay",
            return_value=("2026-04-01", False),
        ):
            agg._maybe_schedule_matrix_pipeline_on_cme_rollover(utc)
    assert agg._last_seen_cme_day == "2026-04-01"
    assert agg._matrix_pipeline_task is None
    assert agg._cme_startup_timetable_check_done is True


def test_startup_file_one_day_ahead_does_not_schedule_pipeline():
    """Early-published session_trading_date: effective CME matches — no maintenance run."""
    from modules.watchdog.aggregator import WatchdogAggregator

    agg = WatchdogAggregator()
    utc = datetime(2026, 4, 1, 12, 0, tzinfo=timezone.utc)
    with patch(
        "modules.timetable.cme_session.get_cme_trading_date",
        return_value="2026-04-01",
    ):
        with patch.object(
            agg,
            "_get_startup_timetable_session_and_replay",
            return_value=("2026-04-02", False),
        ):
            agg._maybe_schedule_matrix_pipeline_on_cme_rollover(utc)
    assert agg._last_seen_cme_day == "2026-04-01"
    assert agg._matrix_pipeline_task is None
    assert agg._cme_startup_timetable_check_done is True


def test_startup_mismatch_schedules_pipeline_with_same_retry_policy_as_rollover():
    from modules.watchdog.aggregator import WatchdogAggregator

    async def _run():
        agg = WatchdogAggregator()
        captured = []

        async def fake_run(cme_day: str, *, retry_on_failure: bool = True):
            captured.append((cme_day, retry_on_failure))

        utc = datetime(2026, 4, 2, 12, 0, tzinfo=timezone.utc)
        with patch.object(agg, "_run_matrix_pipeline_subprocess", side_effect=fake_run):
            with patch(
                "modules.timetable.cme_session.get_cme_trading_date",
                return_value="2026-04-02",
            ):
                with patch.object(
                    agg,
                    "_get_startup_timetable_session_and_replay",
                    return_value=("2026-04-01", False),
                ):
                    agg._maybe_schedule_matrix_pipeline_on_cme_rollover(utc)
                    assert agg._matrix_pipeline_task is not None
                    await agg._matrix_pipeline_task
        assert captured == [("2026-04-02", True)]

    asyncio.run(_run())


def test_rollover_schedules_pipeline_task_with_retry_flag():
    from modules.watchdog.aggregator import WatchdogAggregator

    async def _run():
        agg = WatchdogAggregator()
        agg._cme_startup_timetable_check_done = True
        agg._last_seen_cme_day = "2026-04-01"
        captured = []

        async def fake_run(cme_day: str, *, retry_on_failure: bool = True):
            captured.append((cme_day, retry_on_failure))

        utc = datetime(2026, 4, 2, 0, 0, tzinfo=timezone.utc)
        with patch.object(agg, "_run_matrix_pipeline_subprocess", side_effect=fake_run):
            with patch(
                "modules.timetable.cme_session.get_cme_trading_date",
                return_value="2026-04-02",
            ):
                agg._maybe_schedule_matrix_pipeline_on_cme_rollover(utc)
                assert agg._last_seen_cme_day == "2026-04-02"
                assert agg._matrix_pipeline_task is not None
                await agg._matrix_pipeline_task
        assert captured == [("2026-04-02", True)]

    asyncio.run(_run())
