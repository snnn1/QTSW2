import asyncio
import json
import shutil
import sys
from contextlib import contextmanager
from pathlib import Path
import uuid

import pandas as pd

REPO_ROOT = Path(__file__).resolve().parents[4]
SYSTEM_ROOT = REPO_ROOT / "system"
sys.path.insert(0, str(SYSTEM_ROOT))

from modules.dashboard.backend import main as backend_main
from modules.dashboard.backend.main import ExecutionTimetableRequest, save_execution_timetable
from modules.timetable.timetable_engine import TimetablePublishResult


def _workspace_temp_dir() -> Path:
    base = Path.cwd() / "tmp" / "pytest_backend_authority"
    base.mkdir(parents=True, exist_ok=True)
    path = base / uuid.uuid4().hex
    path.mkdir(parents=True, exist_ok=False)
    return path


def test_execution_publish_updates_manual_authority(monkeypatch):
    temp_root = _workspace_temp_dir() / "qtsw2"
    (temp_root / "data" / "timetable").mkdir(parents=True, exist_ok=True)
    (temp_root / "data" / "session").mkdir(parents=True, exist_ok=True)

    class FakeEngine:
        def __init__(self, *args, **kwargs):
            pass

        def write_execution_timetable_from_master_matrix(self, matrix_df, trade_date, **kwargs):
            timetable_path = temp_root / "data" / "timetable" / "timetable_current.json"
            timetable_path.write_text(
                json.dumps(
                    {
                        "session_trading_date": trade_date,
                        "source": "manual",
                        "streams": [{"stream": "ES1", "slot_time": "08:00", "enabled": True}],
                        "metadata": {"replay": False},
                    }
                ),
                encoding="utf-8",
            )
            return TimetablePublishResult(
                changed=True,
                skipped_no_change=False,
                timetable_hash="hash-1",
                previous_hash="",
            )

    @contextmanager
    def _no_publish_lock():
        yield

    monkeypatch.setattr(backend_main, "QTSW2_ROOT", temp_root)
    monkeypatch.setattr(
        "modules.matrix.file_manager.get_current_master_matrix_df",
        lambda: pd.DataFrame([{"Stream": "ES1", "trade_date": pd.Timestamp("2026-04-21")}]),
    )
    monkeypatch.setattr("modules.timetable.timetable_engine.TimetableEngine", FakeEngine)
    monkeypatch.setattr(
        "modules.timetable.timetable_supervisor.timetable_publish_blocking",
        _no_publish_lock,
    )

    try:
        result = asyncio.run(
            save_execution_timetable(
                ExecutionTimetableRequest(
                    trading_date="2026-04-21",
                    mode="live",
                    replay=False,
                    source="manual",
                    reason="publish",
                )
            )
        )

        authority_path = temp_root / "data" / "session" / "session_authority.json"
        authority = json.loads(authority_path.read_text(encoding="utf-8"))

        assert result["status"] == "published"
        assert authority["mode"] == "manual"
        assert authority["session_trading_date"] == "2026-04-21"
        assert authority["locked"] is True
        assert authority["set_by"] == "POST /api/timetable/execution"
        assert authority["reason"] == "manual_execution_publish"
        assert authority["metadata"]["publish_mode"] == "live"
        assert authority["metadata"]["requested_source"] == "manual"
        assert authority["metadata"]["requested_reason"] == "publish"
    finally:
        shutil.rmtree(temp_root.parent, ignore_errors=True)


def test_historical_execution_publish_updates_replay_authority_and_file(monkeypatch):
    temp_root = _workspace_temp_dir() / "qtsw2"
    (temp_root / "data" / "timetable").mkdir(parents=True, exist_ok=True)
    (temp_root / "data" / "session").mkdir(parents=True, exist_ok=True)

    class FakeEngine:
        def __init__(self, *args, **kwargs):
            pass

        def write_execution_timetable_from_master_matrix(self, matrix_df, trade_date, **kwargs):
            timetable_path = temp_root / "data" / "timetable" / "timetable_replay_current.json"
            timetable_path.write_text(
                json.dumps(
                    {
                        "session_trading_date": trade_date,
                        "source": "replay",
                        "streams": [{"stream": "ES1", "slot_time": "08:00", "enabled": True}],
                        "metadata": {"replay": True},
                    }
                ),
                encoding="utf-8",
            )
            return TimetablePublishResult(
                changed=True,
                skipped_no_change=False,
                timetable_hash="hash-replay",
                previous_hash="",
            )

    @contextmanager
    def _no_publish_lock():
        yield

    monkeypatch.setattr(backend_main, "QTSW2_ROOT", temp_root)
    monkeypatch.setattr(
        "modules.matrix.file_manager.get_current_master_matrix_df",
        lambda: pd.DataFrame([{"Stream": "ES1", "trade_date": pd.Timestamp("2026-04-02")}]),
    )
    monkeypatch.setattr("modules.timetable.timetable_engine.TimetableEngine", FakeEngine)
    monkeypatch.setattr(
        "modules.timetable.timetable_supervisor.timetable_publish_blocking",
        _no_publish_lock,
    )

    try:
        result = asyncio.run(
            save_execution_timetable(
                ExecutionTimetableRequest(
                    trading_date="2026-04-02",
                    mode="historical",
                    replay=False,
                    source="manual",
                    reason="playback",
                )
            )
        )

        authority_path = temp_root / "data" / "session" / "session_authority.json"
        authority = json.loads(authority_path.read_text(encoding="utf-8"))

        assert result["status"] == "published"
        assert str(result["file"]).endswith("timetable_replay_current.json")
        assert authority["mode"] == "replay"
        assert authority["session_trading_date"] == "2026-04-02"
        assert authority["locked"] is True
        assert authority["reason"] == "replay_publish"
        assert authority["metadata"]["publish_mode"] == "historical"
        assert authority["metadata"]["requested_source"] == "manual"
        assert authority["metadata"]["requested_reason"] == "playback"
    finally:
        shutil.rmtree(temp_root.parent, ignore_errors=True)
