from __future__ import annotations

import json
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.aggregator import WatchdogAggregator
from modules.watchdog.event_feed import EventFeedGenerator
from modules.watchdog.run_context import build_run_context, resolve_active_run_context


def _workspace_temp_dir() -> Path:
    base = Path.cwd() / "tmp" / "pytest_watchdog"
    base.mkdir(parents=True, exist_ok=True)
    path = base / uuid.uuid4().hex
    path.mkdir(parents=True, exist_ok=False)
    return path


def test_resolve_active_run_context_uses_latest_run_pointer(monkeypatch):
    project_root = _workspace_temp_dir()
    run_root = project_root / "runs" / "abc123"
    (run_root / "state" / "stream_journals").mkdir(parents=True)
    (run_root / "state" / "execution_journals").mkdir(parents=True)
    (run_root / "derived" / "execution_summaries").mkdir(parents=True)
    (project_root / "runs" / "LATEST_RUN.txt").write_text("runs/abc123\n", encoding="utf-8")
    monkeypatch.setenv("WATCHDOG_PROJECT_ROOT", str(project_root))
    monkeypatch.delenv("WATCHDOG_PERSISTENCE_BASE", raising=False)
    monkeypatch.delenv("WATCHDOG_FALLBACK_ROOT", raising=False)

    context = resolve_active_run_context()

    assert context.project_root == project_root.resolve()
    assert context.persistence_base == run_root.resolve()
    assert context.run_id == "abc123"
    assert context.is_run_scoped is True
    assert context.robot_logs_dir == run_root.resolve() / "logs" / "robot"
    assert context.frontend_feed_file == run_root.resolve() / "logs" / "robot" / "frontend_feed.jsonl"
    assert context.slot_journals_dir == run_root.resolve() / "state" / "stream_journals"
    assert context.execution_journals_dir == run_root.resolve() / "state" / "execution_journals"
    assert context.execution_summaries_dir == run_root.resolve() / "derived" / "execution_summaries"


def test_build_run_context_defaults_to_project_root(monkeypatch):
    project_root = _workspace_temp_dir()
    (project_root / "state" / "stream_journals").mkdir(parents=True)
    (project_root / "state" / "execution_journals").mkdir(parents=True)
    (project_root / "derived" / "execution_summaries").mkdir(parents=True)
    monkeypatch.setenv("WATCHDOG_PROJECT_ROOT", str(project_root))
    monkeypatch.delenv("WATCHDOG_PERSISTENCE_BASE", raising=False)
    monkeypatch.delenv("WATCHDOG_FALLBACK_ROOT", raising=False)

    context = build_run_context()

    assert context.persistence_base == project_root.resolve()
    assert context.run_id is None
    assert context.is_run_scoped is False
    assert context.slot_journals_dir == project_root.resolve() / "state" / "stream_journals"
    assert context.execution_journals_dir == project_root.resolve() / "state" / "execution_journals"
    assert context.execution_summaries_dir == project_root.resolve() / "derived" / "execution_summaries"


def test_live_session_authority_prefers_project_root_over_latest_run(monkeypatch):
    project_root = _workspace_temp_dir()
    run_root = project_root / "runs" / "old_playback"
    run_root.mkdir(parents=True)
    (project_root / "runs" / "LATEST_RUN.txt").write_text("runs/old_playback\n", encoding="utf-8")
    (project_root / "data" / "session").mkdir(parents=True)
    (project_root / "data" / "timetable").mkdir(parents=True)
    (project_root / "data" / "session" / "session_authority.json").write_text(
        json.dumps(
            {
                "mode": "manual",
                "session_trading_date": "2026-05-06",
                "metadata": {"requested_replay": False},
            }
        ),
        encoding="utf-8",
    )
    (project_root / "data" / "timetable" / "timetable_current.json").write_text(
        json.dumps(
            {
                "session_trading_date": "2026-05-06",
                "metadata": {"replay": False},
            }
        ),
        encoding="utf-8",
    )
    monkeypatch.setenv("WATCHDOG_PROJECT_ROOT", str(project_root))
    monkeypatch.delenv("WATCHDOG_PERSISTENCE_BASE", raising=False)
    monkeypatch.delenv("WATCHDOG_FALLBACK_ROOT", raising=False)

    context = resolve_active_run_context()

    assert context.persistence_base == project_root.resolve()
    assert context.run_id is None
    assert context.is_run_scoped is False


def test_active_playback_scenario_pointer_prefers_run_root_over_live_session(monkeypatch):
    project_root = _workspace_temp_dir()
    run_root = project_root / "runs" / "playback_scenario_abc"
    scenario_dir = run_root / "playback_scenario"
    scenario_dir.mkdir(parents=True)
    manifest = {
        "mode": "multi_day_carryover",
        "run_id": "playback_scenario_abc",
        "dates": ["2026-04-28"],
        "timetables": {"2026-04-28": {"path": "timetables/timetable_2026-04-28.json"}},
    }
    manifest_path = scenario_dir / "playback_scenario.json"
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    (project_root / "configs" / "robot").mkdir(parents=True)
    (project_root / "configs" / "robot" / "playback_scenario_current.json").write_text(
        json.dumps(
            {
                "mode": "multi_day_carryover",
                "run_id": "playback_scenario_abc",
                "manifest_path": str(manifest_path),
                "source": "matrix_ui_playback_scenario",
            }
        ),
        encoding="utf-8",
    )
    (project_root / "data" / "session").mkdir(parents=True)
    (project_root / "data" / "timetable").mkdir(parents=True)
    (project_root / "data" / "session" / "session_authority.json").write_text(
        json.dumps({"session_trading_date": "2026-05-06", "metadata": {"requested_replay": False}}),
        encoding="utf-8",
    )
    (project_root / "data" / "timetable" / "timetable_current.json").write_text(
        json.dumps({"session_trading_date": "2026-05-06", "metadata": {"replay": False}}),
        encoding="utf-8",
    )
    monkeypatch.setenv("WATCHDOG_PROJECT_ROOT", str(project_root))
    monkeypatch.delenv("WATCHDOG_PERSISTENCE_BASE", raising=False)
    monkeypatch.delenv("WATCHDOG_FALLBACK_ROOT", raising=False)

    context = resolve_active_run_context()

    assert context.persistence_base == run_root.resolve()
    assert context.run_id == "playback_scenario_abc"
    assert context.is_run_scoped is True


def test_run_scoped_current_run_id_uses_context_even_if_feed_has_other_run():
    project_root = _workspace_temp_dir()
    context = build_run_context(project_root / "runs" / "scenario_run")
    agg = object.__new__(WatchdogAggregator)
    agg._get_run_id_from_most_recent_feed_event = lambda context=None: "other-run"

    assert agg.get_current_run_id(context) == "scenario_run"


def test_event_feed_processes_active_run_root(monkeypatch):
    project_root = _workspace_temp_dir()
    run_root = project_root / "runs" / "run42"
    robot_logs_dir = run_root / "logs" / "robot"
    robot_logs_dir.mkdir(parents=True)
    (project_root / "runs" / "LATEST_RUN.txt").write_text("runs/run42\n", encoding="utf-8")
    (robot_logs_dir / "robot_ENGINE.jsonl").write_text(
        json.dumps(
            {
                "event": "ENGINE_START",
                "ts_utc": "2026-04-20T00:00:00+00:00",
                "run_id": "run42",
                "data": {},
            }
        )
        + "\n",
        encoding="utf-8",
    )

    monkeypatch.setenv("WATCHDOG_PROJECT_ROOT", str(project_root))
    monkeypatch.delenv("WATCHDOG_PERSISTENCE_BASE", raising=False)
    monkeypatch.delenv("WATCHDOG_FALLBACK_ROOT", raising=False)
    monkeypatch.setattr(
        "modules.watchdog.event_feed.ROBOT_LOG_READ_POSITIONS_FILE",
        project_root / "data" / "robot_log_read_positions.json",
    )

    feed = EventFeedGenerator()
    processed = feed.process_new_events()

    frontend_feed = robot_logs_dir / "frontend_feed.jsonl"
    assert processed == 1
    assert frontend_feed.exists()
    lines = frontend_feed.read_text(encoding="utf-8").splitlines()
    assert len(lines) == 1
    event = json.loads(lines[0])
    assert event["run_id"] == "run42"
    assert event["event_type"] == "ENGINE_START"
    assert not (project_root / "logs" / "robot" / "frontend_feed.jsonl").exists()
