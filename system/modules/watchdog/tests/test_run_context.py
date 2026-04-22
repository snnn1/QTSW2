from __future__ import annotations

import json
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

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
