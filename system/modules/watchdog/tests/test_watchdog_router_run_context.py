from __future__ import annotations

import asyncio
import json
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.backend.routers import watchdog as router_module


def _workspace_temp_dir() -> Path:
    base = Path.cwd() / "tmp" / "pytest_watchdog"
    base.mkdir(parents=True, exist_ok=True)
    path = base / uuid.uuid4().hex
    path.mkdir(parents=True, exist_ok=False)
    return path


class _AggregatorStub:
    def __init__(self) -> None:
        self.current_run_id_calls = []
        self.get_events_since_calls = []
        self.status_calls = []
        self.stream_states_calls = []
        self.active_intents_calls = []
        self.daily_journal_calls = []
        self.slot_lifecycle_calls = []

    def get_current_run_id(self, context=None):
        self.current_run_id_calls.append(context)
        return getattr(context, "run_id", None) or "active-run"

    def get_events_since(self, run_id, since_seq, context=None):
        self.get_events_since_calls.append((run_id, since_seq, context))
        return [
            {
                "run_id": run_id,
                "event_seq": since_seq + 1,
                "event_type": "ENGINE_START",
                "timestamp_utc": "2026-04-20T00:00:00+00:00",
                "data": {},
            }
        ]

    def get_watchdog_status_for_context(self, context=None):
        self.status_calls.append(context)
        return {"snapshot_utc": "2026-04-20T00:00:00+00:00", "run_id": getattr(context, "run_id", None)}

    def get_stream_states_for_context(self, context=None):
        self.stream_states_calls.append(context)
        return {"streams": [], "timetable_unavailable": False}

    def get_active_intents_for_context(self, context=None):
        self.active_intents_calls.append(context)
        return {"timestamp_chicago": "2026-04-20T00:00:00-05:00", "intents": []}

    def get_daily_journal_for_context(self, trading_date, context=None):
        self.daily_journal_calls.append((trading_date, context))
        return {"trading_date": trading_date, "streams": [], "summary": None}

    def get_events_for_slot_lifecycle_for_context(self, n=500, context=None):
        self.slot_lifecycle_calls.append((n, context))
        return []


def test_get_events_honors_explicit_run_root(monkeypatch):
    project_root = _workspace_temp_dir()
    run_root = project_root / "runs" / "run77"
    (run_root / "logs" / "robot").mkdir(parents=True)
    monkeypatch.setenv("WATCHDOG_PROJECT_ROOT", str(project_root))

    stub = _AggregatorStub()
    monkeypatch.setattr(router_module, "aggregator_instance", stub)

    payload = asyncio.run(router_module.get_events(since_seq=9, run_root=str(run_root)))

    assert payload["run_id"] == "run77"
    assert payload["next_seq"] == 10
    assert payload["persistence_root"] == str(run_root.resolve())
    assert stub.current_run_id_calls[0].persistence_base == run_root.resolve()
    assert stub.get_events_since_calls[0][0] == "run77"
    assert stub.get_events_since_calls[0][2].frontend_feed_file == run_root.resolve() / "logs" / "robot" / "frontend_feed.jsonl"


def test_get_execution_summary_reads_run_scoped_summary(monkeypatch):
    project_root = _workspace_temp_dir()
    run_root = project_root / "runs" / "run88"
    summary_dir = run_root / "derived" / "execution_summaries"
    summary_dir.mkdir(parents=True)
    (summary_dir / "2026-04-20.json").write_text(
        json.dumps({"trading_date": "2026-04-20", "total_trades": 4}),
        encoding="utf-8",
    )
    monkeypatch.setenv("WATCHDOG_PROJECT_ROOT", str(project_root))

    payload = asyncio.run(
        router_module.get_execution_summary("2026-04-20", run_root=str(run_root))
    )

    assert payload["trading_date"] == "2026-04-20"
    assert payload["total_trades"] == 4


def test_status_and_daily_journal_forward_run_context(monkeypatch):
    project_root = _workspace_temp_dir()
    run_root = project_root / "runs" / "run99"
    (run_root / "logs" / "robot").mkdir(parents=True)
    monkeypatch.setenv("WATCHDOG_PROJECT_ROOT", str(project_root))

    stub = _AggregatorStub()
    monkeypatch.setattr(router_module, "aggregator_instance", stub)

    status_payload = asyncio.run(router_module.get_watchdog_status(run_root=str(run_root)))
    journal_payload = asyncio.run(
        router_module.get_daily_journal("2026-04-20", run_root=str(run_root))
    )

    assert status_payload["run_id"] == "run99"
    assert stub.status_calls[0].persistence_base == run_root.resolve()
    assert journal_payload["trading_date"] == "2026-04-20"
    assert stub.daily_journal_calls[0][0] == "2026-04-20"
    assert stub.daily_journal_calls[0][1].persistence_base == run_root.resolve()
